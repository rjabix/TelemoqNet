mock_provider "azurerm" {
  mock_resource "azurerm_resource_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test" }
  }
  mock_resource "azurerm_virtual_network" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Network/virtualNetworks/vnet-test" }
  }
  mock_resource "azurerm_subnet" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Network/virtualNetworks/vnet-test/subnets/test" }
  }
  mock_resource "azurerm_storage_account" {
    defaults = {
      id                    = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Storage/storageAccounts/testlogs"
      primary_blob_endpoint = "https://testlogs.blob.core.windows.net/"
    }
  }
  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.OperationalInsights/workspaces/law-test"
      workspace_id = "00000000-0000-0000-0000-000000000002"
    }
  }
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-test"
      principal_id = "00000000-0000-0000-0000-000000000003"
      client_id    = "00000000-0000-0000-0000-000000000004"
    }
  }
  mock_resource "azurerm_container_registry" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.ContainerRegistry/registries/testacr" }
  }
  mock_resource "azurerm_network_security_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Network/networkSecurityGroups/nsg-test" }
  }
  mock_resource "azurerm_container_app_environment" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.App/managedEnvironments/test" }
  }
}
mock_provider "azapi" {}
mock_provider "random" {}

override_resource {
  target = azapi_resource.analysis_dcr
  values = {
    id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-test/providers/Microsoft.Insights/dataCollectionRules/test"
    output = { properties = {
      immutableId = "dcr-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
      endpoints   = { logsIngestion = "https://test.ingest.monitor.azure.com" }
    } }
  }
}

variables {
  subscription_id       = "00000000-0000-0000-0000-000000000001"
  github_repo           = "example/test"
  admin_ssh_public_key  = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDZkA1vUqlLWdW7cQn2qY1A3VE9S0Y3fEQjZ4sQZ4kOJ test"
  analysis_enabled      = true
  analysis_image_digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
}

run "retained_without_runtime" {
  command = plan
  assert {
    condition     = length(azapi_resource.analysis_table) == 6 && length(azurerm_storage_container.analysis_checkpoints) == 1
    error_message = "Disabling runtime must keep all six tables and the checkpoints."
  }
  assert {
    condition     = length(azurerm_container_app_job.analysis) == 0 && length(azurerm_container_app_environment.analysis) == 0
    error_message = "Disabled runtime must not provision compute or managed networking."
  }
  assert {
    condition     = alltrue([for prefix in azurerm_storage_management_policy.analysis[0].rule[0].filters[0].prefix_match : startswith(prefix, "analysis-checkpoints/")])
    error_message = "Checkpoint expiry must never apply to captured evidence."
  }
}

run "manual_runtime" {
  command = plan
  variables { analysis_runtime_enabled = true }
  assert {
    condition     = length(azurerm_container_app_job.analysis[0].manual_trigger_config) == 1 && length(azurerm_container_app_job.analysis[0].schedule_trigger_config) == 0
    error_message = "Manual runtime must not schedule imports."
  }
  assert {
    condition     = azurerm_container_app_job.analysis[0].replica_retry_limit == 0 && length(azurerm_subnet_network_security_group_association.analysis) == 1
    error_message = "Runtime needs isolation and no unbounded platform retries."
  }
  assert {
    condition     = azurerm_network_security_rule.analysis["deny_in"].destination_port_range == "*" && azurerm_network_security_rule.analysis["deny_out"].destination_port_ranges == null
    error_message = "Azure requires wildcard ports in the singular field, never the plural port list."
  }
}

run "scheduled_runtime" {
  command = plan
  variables {
    analysis_runtime_enabled     = true
    analysis_auto_ingest_enabled = true
  }
  assert {
    condition     = azurerm_container_app_job.analysis[0].schedule_trigger_config[0].cron_expression == "*/15 * * * *" && local.analysis_environment.DRY_RUN == "false"
    error_message = "Opted-in automatic runtime must schedule actual incremental ingestion."
  }
}

run "reject_schedule_without_runtime" {
  command = plan
  variables { analysis_auto_ingest_enabled = true }
  expect_failures = [var.analysis_auto_ingest_enabled]
}

run "optional_alerts" {
  command = plan
  variables { analysis_alert_emails = ["operator@example.com"] }
  assert {
    condition     = length(azurerm_monitor_scheduled_query_rules_alert_v2.analysis) == 2 && length(azurerm_monitor_activity_log_alert.analysis_cleanup) == 1
    error_message = "Operators need importer, stale-delivery and cleanup-failure alerts."
  }
  assert {
    condition     = length(azurerm_consumption_budget_subscription.analysis) == 1 && azurerm_consumption_budget_subscription.analysis[0].amount == 30
    error_message = "The opt-in budget must cover the dedicated subscription, including managed networking."
  }
}
