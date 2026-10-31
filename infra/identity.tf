resource "azurerm_container_registry" "main" {
  name                          = local.acr_name
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  sku                           = "Premium"
  admin_enabled                 = false
  public_network_access_enabled = true
  tags                          = local.tags
}

# Identity used by the honeypot VM and the container running on it.
resource "azurerm_user_assigned_identity" "vm" {
  name                = "id-${var.name_prefix}-vm"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_role_assignment" "vm_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.vm.principal_id
}

# Identity assumed by GitHub Actions through OIDC (no secrets).
resource "azurerm_user_assigned_identity" "github_deploy" {
  name                = "id-${var.name_prefix}-github-deploy"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_federated_identity_credential" "github_deploy" {
  name                      = "github-${var.github_environment}"
  user_assigned_identity_id = azurerm_user_assigned_identity.github_deploy.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = "https://token.actions.githubusercontent.com"
  subject                   = "repo:${coalesce(var.github_oidc_subject, var.github_repo)}:environment:${var.github_environment}"
}

resource "azurerm_role_assignment" "deploy_acr_push" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPush"
  principal_id         = azurerm_user_assigned_identity.github_deploy.principal_id
}

resource "azurerm_role_definition" "vm_deploy" {
  name        = "${var.name_prefix}-vm-deploy"
  scope       = azurerm_resource_group.main.id
  description = "Run commands on the honeypot VM (deploy only)."

  permissions {
    actions = [
      "Microsoft.Compute/virtualMachines/read",
      "Microsoft.Compute/virtualMachines/runCommand/action",
    ]
  }

  assignable_scopes = [azurerm_resource_group.main.id]
}

resource "azurerm_role_assignment" "deploy_vm_run_command" {
  scope              = azurerm_linux_virtual_machine.honeypot.id
  role_definition_id = azurerm_role_definition.vm_deploy.role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.github_deploy.principal_id
}
