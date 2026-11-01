# Checkpoint writes are scoped to a different container from captured evidence.
# This container survives runtime removal, including its identity being replaced.
resource "azurerm_storage_container" "analysis_checkpoints" {
  count                 = var.analysis_enabled ? 1 : 0
  name                  = "analysis-checkpoints"
  storage_account_id    = azurerm_storage_account.logs.id
  container_access_type = "private"
  lifecycle {
    prevent_destroy = true
    precondition {
      condition     = var.container_name != "analysis-checkpoints"
      error_message = "The checkpoint container must be separate from captured sessions."
    }
  }
}

resource "azurerm_role_assignment" "analysis_checkpoints" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  scope                = azurerm_storage_container.analysis_checkpoints[0].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.analysis[0].principal_id
}

# Bound accumulated receipts/manifests without touching evidence or either lock.
resource "azurerm_storage_management_policy" "analysis" {
  count              = var.analysis_enabled ? 1 : 0
  storage_account_id = azurerm_storage_account.logs.id
  rule {
    name    = "expire-analysis-checkpoints"
    enabled = true
    filters {
      blob_types   = ["blockBlob"]
      prefix_match = ["analysis-checkpoints/receipts/", "analysis-checkpoints/operations/", "analysis-checkpoints/scans/"]
    }
    actions {
      base_blob {
        delete_after_days_since_modification_greater_than = 35
      }
    }
  }
}

resource "azurerm_role_assignment" "analysis_query" {
  count                = local.analysis_runtime_enabled ? 1 : 0
  scope                = azurerm_log_analytics_workspace.main.id
  role_definition_name = "Log Analytics Reader"
  principal_id         = azurerm_user_assigned_identity.analysis[0].principal_id
}

resource "azurerm_network_security_group" "analysis" {
  count               = local.analysis_runtime_enabled ? 1 : 0
  name                = "nsg-${var.name_prefix}-analysis"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

locals {
  # Workload-profiles environment dependencies, per Azure's firewall guidance.
  # No user application ingress is needed for a job.
  analysis_network_rules = {
    self_in   = { priority = 100, direction = "Inbound", protocol = "*", source = "10.50.0.128/27", destination = "10.50.0.128/27", ports = ["*"], access = "Allow" }
    probes    = { priority = 110, direction = "Inbound", protocol = "Tcp", source = "AzureLoadBalancer", destination = "*", ports = ["30000-32767"], access = "Allow" }
    deny_in   = { priority = 4000, direction = "Inbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
    self_out  = { priority = 100, direction = "Outbound", protocol = "*", source = "*", destination = "10.50.0.128/27", ports = ["*"], access = "Allow" }
    private   = { priority = 110, direction = "Outbound", protocol = "Tcp", source = "*", destination = "10.50.0.64/27", ports = ["443"], access = "Allow" }
    dns       = { priority = 120, direction = "Outbound", protocol = "*", source = "*", destination = "168.63.129.16", ports = ["53"], access = "Allow" }
    monitor   = { priority = 130, direction = "Outbound", protocol = "Tcp", source = "*", destination = "AzureMonitor", ports = ["443"], access = "Allow" }
    identity  = { priority = 140, direction = "Outbound", protocol = "Tcp", source = "*", destination = "AzureActiveDirectory", ports = ["443"], access = "Allow" }
    registry  = { priority = 150, direction = "Outbound", protocol = "Tcp", source = "*", destination = "MicrosoftContainerRegistry", ports = ["443"], access = "Allow" }
    frontdoor = { priority = 160, direction = "Outbound", protocol = "Tcp", source = "*", destination = "AzureFrontDoor.FirstParty", ports = ["443"], access = "Allow" }
    storage   = { priority = 170, direction = "Outbound", protocol = "Tcp", source = "*", destination = "Storage.${var.location}", ports = ["443"], access = "Allow" }
    deny_out  = { priority = 4000, direction = "Outbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
  }
}

resource "azurerm_network_security_rule" "analysis" {
  for_each                    = local.analysis_runtime_enabled ? local.analysis_network_rules : {}
  name                        = each.key
  priority                    = each.value.priority
  direction                   = each.value.direction
  access                      = each.value.access
  protocol                    = each.value.protocol
  source_port_range           = "*"
  destination_port_range      = each.value.ports == ["*"] ? "*" : null
  destination_port_ranges     = each.value.ports == ["*"] ? null : each.value.ports
  source_address_prefix       = each.value.source
  destination_address_prefix  = each.value.destination
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.analysis[0].name
}

resource "azurerm_subnet_network_security_group_association" "analysis" {
  count                     = local.analysis_runtime_enabled ? 1 : 0
  subnet_id                 = azurerm_subnet.analysis[0].id
  network_security_group_id = azurerm_network_security_group.analysis[0].id
  depends_on                = [azurerm_network_security_rule.analysis]
}

# Override the honeypot NSG's default VirtualNetwork allow rules in both directions.
resource "azurerm_network_security_rule" "honeypot_analysis" {
  for_each                    = toset(["Inbound", "Outbound"])
  name                        = "deny-analysis-${lower(each.key)}"
  priority                    = 200
  direction                   = each.key
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = each.key == "Inbound" ? "10.50.0.128/27" : "*"
  destination_address_prefix  = each.key == "Outbound" ? "10.50.0.128/27" : "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}
