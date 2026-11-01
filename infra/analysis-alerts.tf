resource "azurerm_monitor_action_group" "analysis" {
  count               = var.analysis_enabled && length(var.analysis_alert_emails) > 0 ? 1 : 0
  name                = "ag-${var.name_prefix}-analysis"
  resource_group_name = azurerm_resource_group.main.name
  short_name          = "analysis"
  dynamic "email_receiver" {
    for_each = var.analysis_alert_emails
    content {
      name                    = "operator-${substr(sha256(email_receiver.value), 0, 8)}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }
  tags = local.tags
}

resource "azurerm_monitor_activity_log_alert" "analysis_cleanup" {
  count               = var.analysis_enabled && length(var.analysis_alert_emails) > 0 ? 1 : 0
  name                = "alert-${var.name_prefix}-analysis-cleanup"
  resource_group_name = azurerm_resource_group.main.name
  location            = "global"
  scopes              = [azurerm_resource_group.main.id]
  criteria {
    category       = "Administrative"
    operation_name = "Microsoft.App/managedEnvironments/delete"
    status         = "Failed"
  }
  action {
    action_group_id = azurerm_monitor_action_group.analysis[0].id
  }
  tags = local.tags
}

locals {
  analysis_alert_queries = {
    delivery = "${replace(file("${path.module}/analysis/queries/imports.kql"), "| take 500", "")}\n| where Delivery in ('Orphan: missing status marker', 'Stale partial delivery')"
    importer = "ContainerAppConsoleLogs_CL | where TimeGenerated > ago(1h) | where ContainerName_s == 'session-importer' | extend Event=parse_json(Log_s) | where Event.event in ('failed', 'source_failed', 'lease_release_deferred') or (Event.event == 'summary' and tolong(Event.failed) > 0)"
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "analysis" {
  for_each             = var.analysis_enabled && length(var.analysis_alert_emails) > 0 ? local.analysis_alert_queries : {}
  name                 = "alert-${var.name_prefix}-analysis-${each.key}"
  location             = azurerm_resource_group.main.location
  resource_group_name  = azurerm_resource_group.main.name
  scopes               = [azurerm_log_analytics_workspace.main.id]
  severity             = 2
  evaluation_frequency = "PT15M"
  window_duration      = "PT1H"
  # Container log tables may not exist until the first execution.
  skip_query_validation = true
  criteria {
    query                   = each.value
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0
    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }
  action {
    action_groups = [azurerm_monitor_action_group.analysis[0].id]
  }
  tags       = local.tags
  depends_on = [azapi_resource.analysis_table]
}

# This includes managed networking in its separate resource group and existing VM/ACR charges. Azure
# Consumption budgets use the subscription's billing currency, not a chosen USD rate.
resource "azurerm_consumption_budget_subscription" "analysis" {
  count           = var.analysis_enabled && length(var.analysis_alert_emails) > 0 ? 1 : 0
  name            = "budget-${var.name_prefix}"
  subscription_id = "/subscriptions/${var.subscription_id}"
  amount          = var.analysis_monthly_budget_amount
  time_grain      = "Monthly"
  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00'Z'", timestamp())
  }
  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThanOrEqualTo"
    contact_emails = var.analysis_alert_emails
  }
  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThanOrEqualTo"
    threshold_type = "Forecasted"
    contact_emails = var.analysis_alert_emails
  }
  lifecycle {
    ignore_changes = [time_period[0].start_date]
  }
}
