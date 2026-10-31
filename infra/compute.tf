resource "azurerm_public_ip" "honeypot" {
  name                = "pip-${var.name_prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.tags
}

resource "azurerm_network_interface" "honeypot" {
  name                = "nic-${var.name_prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.honeypot.id
    private_ip_address_allocation = "Dynamic"
    public_ip_address_id          = azurerm_public_ip.honeypot.id
  }
}

resource "azurerm_linux_virtual_machine" "honeypot" {
  name                = "vm-${var.name_prefix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  size                = var.vm_size
  admin_username      = "honeyadmin"

  network_interface_ids           = [azurerm_network_interface.honeypot.id]
  disable_password_authentication = true

  # Azure requires a key even though no inbound SSH rule exists.
  admin_ssh_key {
    username   = "honeyadmin"
    public_key = var.admin_ssh_public_key
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.vm.id]
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "StandardSSD_LRS"
  }

  source_image_reference {
    publisher = "Canonical"
    offer     = "ubuntu-24_04-lts"
    sku       = "server"
    version   = "latest"
  }

  custom_data = base64encode(templatefile("${path.module}/cloud-init.yaml.tftpl", {
    acr_name         = azurerm_container_registry.main.name
    acr_login_server = azurerm_container_registry.main.login_server
    vm_client_id     = azurerm_user_assigned_identity.vm.client_id
    blob_endpoint    = azurerm_storage_account.logs.primary_blob_endpoint
    container_name   = var.container_name
  }))

  tags = local.tags
}
