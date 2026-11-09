locals {
  analysis_runtime_enabled = var.analysis_enabled && var.analysis_runtime_enabled
  analysis_tables          = toset(["Sessions", "Logins", "Commands", "Events", "PayloadChunks", "ImportStatus"])
  analysis_columns = [
    { name = "TimeGenerated", type = "datetime" },
    { name = "SessionId", type = "string" },
    { name = "StartedAt", type = "datetime" },
    { name = "Protocol", type = "string" },
    { name = "SourceBlob", type = "string" },
    { name = "SourceETag", type = "string" },
    { name = "ImportRunId", type = "string" },
    { name = "RecordIndex", type = "long" },
    { name = "EntityKind", type = "string" },
    { name = "ChunkIndex", type = "long" },
    { name = "ChunkData", type = "string" },
    { name = "PayloadHash", type = "string" },
    { name = "Payload", type = "dynamic" },
    { name = "HasChunks", type = "boolean" },
    { name = "ExpectedRows", type = "long" },
    { name = "State", type = "string" },
  ]
  analysis_environment = local.analysis_runtime_enabled ? {
    AZURE_CLIENT_ID       = azurerm_user_assigned_identity.analysis[0].client_id
    BLOB_ENDPOINT         = trimsuffix(azurerm_storage_account.logs.primary_blob_endpoint, "/")
    BLOB_CONTAINER        = var.container_name
    INGESTION_ENDPOINT    = azapi_resource.analysis_dcr[0].output.properties.endpoints.logsIngestion
    DCR_IMMUTABLE_ID      = azapi_resource.analysis_dcr[0].output.properties.immutableId
    CHECKPOINT_CONTAINER  = azurerm_storage_container.analysis_checkpoints[0].name
    WORKSPACE_CUSTOMER_ID = azurerm_log_analytics_workspace.main.workspace_id
    AUTO_INGEST           = tostring(var.analysis_auto_ingest_enabled)
    LOOKBACK_DAYS         = tostring(var.analysis_lookback_days)
    FORCE_REIMPORT        = "false"
    OPERATION_ID          = ""
    VERIFY_OPERATION      = ""
    START_DATE            = ""
    END_DATE              = ""
    DRY_RUN               = tostring(!var.analysis_auto_ingest_enabled)
    MAX_BLOBS             = "50000"
    MAX_SOURCE_BYTES      = "524288000"
    MAX_BLOB_BYTES        = "2097152"
    MAX_INGESTION_BYTES   = "1048576000"
    MAX_SECONDS           = "240"
  } : {}
  analysis_queries = {
    sessions       = { title = "Recent sessions", file = "sessions.kql" }
    traffic        = { title = "Sessions over time", file = "traffic.kql" }
    authentication = { title = "Authentication outcomes", file = "authentication.kql" }
    sources        = { title = "Source addresses", file = "sources.kql" }
    locations      = { title = "Source countries and cities", file = "locations.kql" }
    usernames      = { title = "Login usernames", file = "usernames.kql" }
    commands       = { title = "Captured commands", file = "commands.kql" }
    mqtt           = { title = "MQTT event types", file = "mqtt.kql" }
    details        = { title = "Session details", file = "details.kql" }
    chunks         = { title = "Reconstruct large records", file = "chunks.kql" }
    imports        = { title = "Import delivery status", file = "imports.kql" }
  }
}

resource "azurerm_subnet" "analysis" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  name                 = "snet-session-analysis"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.50.0.128/27"]
  delegation {
    name = "container-apps"
    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_user_assigned_identity" "analysis" {
  count               = local.analysis_runtime_enabled ? 1 : 0
  name                = "id-${var.name_prefix}-analysis"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_role_assignment" "analysis_reader" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  scope                = azurerm_storage_container.sessions.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_user_assigned_identity.analysis[0].principal_id
}

resource "azurerm_role_assignment" "analysis_pull" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.analysis[0].principal_id
}

# AzureRM's workspace_table resource cannot define a custom schema. Use ARM,
# never the storage data plane, for tables and the Direct DCR endpoint.
resource "azapi_resource" "analysis_table" {
  for_each  = var.analysis_enabled ? local.analysis_tables : toset([])
  type      = "Microsoft.OperationalInsights/workspaces/tables@2022-10-01"
  name      = "Honeypot${each.key}_CL"
  parent_id = azurerm_log_analytics_workspace.main.id
  lifecycle {
    prevent_destroy = true
  }
  body = {
    properties = {
      plan                 = "Analytics"
      retentionInDays      = 30
      totalRetentionInDays = 30
      schema = {
        name    = "Honeypot${each.key}_CL"
        columns = local.analysis_columns
      }
    }
  }
}

resource "azapi_resource" "analysis_dcr" {
  count     = var.analysis_enabled ? 1 : 0
  type      = "Microsoft.Insights/dataCollectionRules@2023-03-11"
  name      = "dcr-${var.name_prefix}-sessions"
  parent_id = azurerm_resource_group.main.id
  location  = azurerm_resource_group.main.location
  tags      = local.tags
  body = {
    kind = "Direct"
    properties = {
      streamDeclarations = {
        for table in local.analysis_tables : "Custom-Honeypot${table}_CL" => { columns = local.analysis_columns }
      }
      destinations = {
        logAnalytics = [{ name = "sessions", workspaceResourceId = azurerm_log_analytics_workspace.main.id }]
      }
      dataFlows = [for table in local.analysis_tables : {
        streams      = ["Custom-Honeypot${table}_CL"]
        destinations = ["sessions"]
        transformKql = "source"
        outputStream = "Custom-Honeypot${table}_CL"
      }]
    }
  }
  response_export_values = ["properties.immutableId", "properties.endpoints.logsIngestion"]
  depends_on             = [azapi_resource.analysis_table]
}

resource "azurerm_role_assignment" "analysis_ingestion" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  scope                = azapi_resource.analysis_dcr[0].id
  role_definition_name = "Monitoring Metrics Publisher"
  principal_id         = azurerm_user_assigned_identity.analysis[0].principal_id
}

resource "azurerm_container_app_environment" "analysis" {
  count                          = local.analysis_runtime_enabled ? 1 : 0
  name                           = "cae-${var.name_prefix}-analysis"
  location                       = azurerm_resource_group.main.location
  resource_group_name            = azurerm_resource_group.main.name
  infrastructure_subnet_id       = azurerm_subnet.analysis[0].id
  internal_load_balancer_enabled = true
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.main.id
  # VNet integration accesses the existing Blob PE. No new environment PE,
  # NAT gateway, dedicated workload profile, or externally exposed application.
  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
  tags       = local.tags
  depends_on = [azurerm_subnet_network_security_group_association.analysis]
}

resource "azurerm_container_app_job" "analysis" {
  count                        = local.analysis_runtime_enabled && var.analysis_image_digest != null ? 1 : 0
  name                         = "job-${var.name_prefix}-session-import"
  location                     = azurerm_resource_group.main.location
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.analysis[0].id
  workload_profile_name        = "Consumption"
  replica_timeout_in_seconds   = 600
  replica_retry_limit          = 0
  dynamic "manual_trigger_config" {
    for_each = var.analysis_auto_ingest_enabled ? [] : [1]
    content {
      parallelism              = 1
      replica_completion_count = 1
    }
  }
  dynamic "schedule_trigger_config" {
    for_each = var.analysis_auto_ingest_enabled ? [1] : []
    content {
      cron_expression          = var.analysis_schedule
      parallelism              = 1
      replica_completion_count = 1
    }
  }
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.analysis[0].id]
  }
  registry {
    server   = azurerm_container_registry.main.login_server
    identity = azurerm_user_assigned_identity.analysis[0].id
  }
  template {
    container {
      name   = "session-importer"
      image  = "${azurerm_container_registry.main.login_server}/telemoqnet-session-importer@${var.analysis_image_digest}"
      cpu    = 0.25
      memory = "0.5Gi"
      dynamic "env" {
        for_each = local.analysis_environment
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }
  tags       = local.tags
  depends_on = [azurerm_role_assignment.analysis_pull, azurerm_role_assignment.analysis_reader, azurerm_role_assignment.analysis_ingestion, azurerm_role_assignment.analysis_checkpoints, azurerm_role_assignment.analysis_query, azurerm_subnet_network_security_group_association.analysis]
}

resource "azurerm_log_analytics_saved_search" "analysis_rows" {
  count                      = var.analysis_enabled ? 1 : 0
  name                       = "HoneypotRows"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  category                   = "Honeypot sessions"
  display_name               = "Complete deduplicated session records"
  function_alias             = "HoneypotRows"
  function_parameters        = ["SessionStart:datetime=datetime(2000-01-01)", "SessionEnd:datetime=datetime(2100-01-01)", "SelectedProtocol:string='all'", "SelectedSession:string=''", "SelectedIp:string=''", "SelectedCountry:string=''", "SelectedCity:string=''", "SelectedAuthentication:string='all'"]
  query                      = file("${path.module}/analysis/queries/rows.kql")
  depends_on                 = [azapi_resource.analysis_table]
}

resource "azurerm_log_analytics_saved_search" "analysis" {
  for_each                   = var.analysis_enabled ? local.analysis_queries : {}
  name                       = "Honeypot-${each.key}"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  category                   = "Honeypot sessions"
  display_name               = each.value.title
  query                      = file("${path.module}/analysis/queries/${each.value.file}")
  depends_on                 = [azurerm_log_analytics_saved_search.analysis_rows]
}

resource "azurerm_application_insights_workbook" "analysis" {
  count               = var.analysis_enabled ? 1 : 0
  name                = uuidv5("url", "${azurerm_log_analytics_workspace.main.id}/session-analysis")
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  display_name        = "Honeypot session analysis"
  source_id           = lower(azurerm_log_analytics_workspace.main.id)
  data_json = jsonencode({
    version = "Notebook/1.0"
    items = concat([
      { type = 1, content = { json = "# Captured honeypot sessions\nNew captured data appears after a verified import. Only complete imports appear in session charts. Filters select whole sessions, including their commands and events. IP locations are approximate; private/unresolved addresses show Unknown. Large record previews are marked; use the reconstruction query for full text. Workspace time is import time; Start/End below filter original session time. Import delivery status is an unfiltered operational view." } },
      { type = 9, content = {
        version = "KqlParameterItem/1.0"
        parameters = [
          { id = "start", name = "Start", type = 1, value = "2000-01-01T00:00:00Z", label = "Session start (UTC, inclusive)" },
          { id = "end", name = "End", type = 1, value = "2100-01-01T00:00:00Z", label = "Session end (UTC, exclusive)" },
          { id = "protocol", name = "Protocol", type = 2, value = "all", jsonData = jsonencode([{ value = "all", label = "All" }, { value = "telnet", label = "Telnet" }, { value = "mqtt", label = "MQTT" }]) },
          { id = "session", name = "SessionId", type = 1, value = "", label = "Session ID (blank for all)" },
          { id = "ip", name = "SourceIp", type = 1, value = "", label = "Source IP (exact IPv4/IPv6, no port; blank for all)" },
          { id = "country", name = "Country", type = 1, value = "", label = "Country (e.g. Poland or Unknown; blank for all)" },
          { id = "city", name = "City", type = 1, value = "", label = "City (exact name or Unknown; blank for all)" },
          { id = "authentication", name = "Authentication", type = 2, value = "all", label = "Session authentication", jsonData = jsonencode([{ value = "all", label = "All sessions" }, { value = "successful", label = "Successful login sessions" }, { value = "failed", label = "Failed login sessions" }, { value = "unknown", label = "No recorded authentication outcome" }]) },
        ]
        style = "pills"
      } },
      ], [for key in ["traffic", "authentication", "sources", "locations", "usernames", "commands", "mqtt", "sessions", "details", "imports"] : {
        type = 3
        content = {
          version                 = "KqlItem/1.0"
          title                   = local.analysis_queries[key].title
          query                   = key == "imports" ? file("${path.module}/analysis/queries/imports.kql") : replace(file("${path.module}/analysis/queries/${local.analysis_queries[key].file}"), "HoneypotRows()", "HoneypotRows(todatetime('{Start:escape}'), todatetime('{End:escape}'), '{Protocol:escape}', '{SessionId:escape}', '{SourceIp:escape}', '{Country:escape}', '{City:escape}', '{Authentication:escape}')")
          queryType               = 0
          resourceType            = "microsoft.operationalinsights/workspaces"
          crossComponentResources = [azurerm_log_analytics_workspace.main.id]
          timeContext             = { durationMs = 2592000000 }
          visualization           = key == "traffic" ? "timechart" : key == "authentication" ? "piechart" : contains(["sources", "usernames", "commands", "mqtt"], key) ? "barchart" : "table"
          size                    = 0
        }
    }])
    isLocked            = false
    fallbackResourceIds = [azurerm_log_analytics_workspace.main.id]
  })
  tags       = local.tags
  depends_on = [azurerm_log_analytics_saved_search.analysis]
}
