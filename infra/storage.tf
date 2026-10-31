resource "azurerm_storage_account" "logs" {
  name                     = local.storage_name
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"

  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  public_network_access_enabled   = false

  blob_properties {
    delete_retention_policy {
      days = 7
    }
  }

  tags = local.tags
}

# Created through the management plane (storage_account_id) because the account
# has no public network access and the app role cannot create containers.
resource "azurerm_storage_container" "sessions" {
  name                  = var.container_name
  storage_account_id    = azurerm_storage_account.logs.id
  container_access_type = "private"
}

resource "azurerm_private_dns_zone" "blob" {
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone" "acr" {
  name                = "privatelink.azurecr.io"
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "blob" {
  name                  = "link-${var.name_prefix}-blob"
  resource_group_name   = azurerm_resource_group.main.name
  private_dns_zone_name = azurerm_private_dns_zone.blob.name
  virtual_network_id    = azurerm_virtual_network.main.id
  tags                  = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "acr" {
  name                  = "link-${var.name_prefix}-acr"
  resource_group_name   = azurerm_resource_group.main.name
  private_dns_zone_name = azurerm_private_dns_zone.acr.name
  virtual_network_id    = azurerm_virtual_network.main.id
  tags                  = local.tags
}

resource "azurerm_private_endpoint" "blob" {
  name                = "pe-${var.name_prefix}-blob"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "blob"
    private_connection_resource_id = azurerm_storage_account.logs.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "blob"
    private_dns_zone_ids = [azurerm_private_dns_zone.blob.id]
  }
}

resource "azurerm_private_endpoint" "acr" {
  name                = "pe-${var.name_prefix}-acr"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "acr"
    private_connection_resource_id = azurerm_container_registry.main.id
    subresource_names              = ["registry"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "acr"
    private_dns_zone_ids = [azurerm_private_dns_zone.acr.id]
  }
}

# Write-only: the honeypot identity can upload blobs but cannot read, list or
# delete them. Verify the data action covers the upload calls the SDK makes.
resource "azurerm_role_definition" "log_writer" {
  name        = "${var.name_prefix}-log-writer"
  scope       = azurerm_storage_account.logs.id
  description = "Write-only blob access for the honeypot."

  permissions {
    actions      = []
    data_actions = ["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write"]
  }

  assignable_scopes = [azurerm_storage_account.logs.id]
}

resource "azurerm_role_assignment" "vm_log_writer" {
  scope              = azurerm_storage_container.sessions.id
  role_definition_id = azurerm_role_definition.log_writer.role_definition_resource_id
  principal_id       = azurerm_user_assigned_identity.vm.principal_id
}

resource "azurerm_role_assignment" "log_reader" {
  count                = var.log_reader_principal_id == null ? 0 : 1
  scope                = azurerm_storage_container.sessions.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = var.log_reader_principal_id
}
