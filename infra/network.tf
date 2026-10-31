resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

locals {
  suffix       = random_string.suffix.result
  acr_name     = "${var.name_prefix}acr${local.suffix}"
  storage_name = "${var.name_prefix}logs${local.suffix}"

  tags = {
    project = "telemoqnet"
    purpose = "honeypot"
  }
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${var.name_prefix}"
  location = var.location
  tags     = local.tags
}

resource "azurerm_virtual_network" "main" {
  name                = "vnet-${var.name_prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  address_space       = ["10.50.0.0/24"]
  tags                = local.tags
}

resource "azurerm_subnet" "honeypot" {
  name                 = "snet-honeypot"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.50.0.0/26"]
}

resource "azurerm_subnet" "private_endpoints" {
  name                 = "snet-private-endpoints"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.50.0.64/27"]
}

resource "azurerm_network_security_group" "honeypot" {
  name                = "nsg-${var.name_prefix}-honeypot"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

# Inbound: only the honeypot ports. Everything else hits the default deny.
resource "azurerm_network_security_rule" "in_honeypot" {
  name                        = "allow-honeypot-inbound"
  priority                    = 100
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_ranges     = ["23", "2323", "1883"]
  source_address_prefix       = "Internet"
  destination_address_prefix  = "*"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}

# Outbound: container registry for image pulls. Registry layers are served from
# Azure Storage, so the Storage tag is also required. This is a residual
# exfiltration path (any storage account in Azure); closing it needs a registry
# private endpoint, which requires the Premium registry SKU.
resource "azurerm_network_security_rule" "out_acr" {
  name                        = "allow-acr-outbound"
  priority                    = 100
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = "*"
  destination_address_prefix  = "AzureContainerRegistry"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}

resource "azurerm_network_security_rule" "out_storage" {
  name                        = "allow-storage-outbound"
  priority                    = 110
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "443"
  source_address_prefix       = "*"
  destination_address_prefix  = "Storage"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}

resource "azurerm_network_security_rule" "out_bootstrap" {
  count                       = var.allow_bootstrap_egress ? 1 : 0
  name                        = "allow-bootstrap-outbound"
  priority                    = 120
  direction                   = "Outbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_ranges     = ["80", "443"]
  source_address_prefix       = "*"
  destination_address_prefix  = "Internet"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}

resource "azurerm_network_security_rule" "out_deny_internet" {
  name                        = "deny-internet-outbound"
  priority                    = 4000
  direction                   = "Outbound"
  access                      = "Deny"
  protocol                    = "*"
  source_port_range           = "*"
  destination_port_range      = "*"
  source_address_prefix       = "*"
  destination_address_prefix  = "Internet"
  resource_group_name         = azurerm_resource_group.main.name
  network_security_group_name = azurerm_network_security_group.honeypot.name
}

resource "azurerm_subnet_network_security_group_association" "honeypot" {
  subnet_id                 = azurerm_subnet.honeypot.id
  network_security_group_id = azurerm_network_security_group.honeypot.id
}
