output "public_ip" {
  value = azurerm_public_ip.honeypot.ip_address
}

output "acr_login_server" {
  value = azurerm_container_registry.main.login_server
}

output "acr_name" {
  value = azurerm_container_registry.main.name
}

output "blob_endpoint" {
  value = azurerm_storage_account.logs.primary_blob_endpoint
}

output "vm_name" {
  value = azurerm_linux_virtual_machine.honeypot.name
}

output "resource_group" {
  value = azurerm_resource_group.main.name
}

# GitHub repository variables (not secrets) for azure/login.
output "github_client_id" {
  value = azurerm_user_assigned_identity.github_deploy.client_id
}

output "tenant_id" {
  value = azurerm_user_assigned_identity.github_deploy.tenant_id
}

output "application_insights_name" {
  value = azurerm_application_insights.main.name
}

output "log_analytics_workspace_name" {
  value = azurerm_log_analytics_workspace.main.name
}

output "analysis" {
  description = "Non-secret identifiers for the optional manual session analysis service."
  value = var.analysis_enabled ? {
    job_name               = try(azurerm_container_app_job.analysis[0].name, null)
    managed_resource_group = try(azurerm_container_app_environment.analysis[0].infrastructure_resource_group_name, null)
    workspace_customer_id  = azurerm_log_analytics_workspace.main.workspace_id
    runtime_enabled        = local.analysis_runtime_enabled
    auto_ingest_enabled    = var.analysis_auto_ingest_enabled
    workspace_id           = azurerm_log_analytics_workspace.main.id
    workbook_id            = azurerm_application_insights_workbook.analysis[0].id
    workbook_portal_url    = "https://portal.azure.com/#resource${azurerm_application_insights_workbook.analysis[0].id}"
    image_repository       = "${azurerm_container_registry.main.login_server}/telemoqnet-session-importer"
    job_portal_url         = try("https://portal.azure.com/#resource${azurerm_container_app_job.analysis[0].id}/overview", null)
  } : null
}
