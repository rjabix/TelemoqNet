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
