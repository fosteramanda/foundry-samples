# Azure Bastion — the only interactive path into private hub and spoke
# resources for operators. No public IPs land on VMs; RDP/SSH is not
# exposed on either plane's network.

resource "azurerm_public_ip" "bastion" {
  name                = local.bastion_pip_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.common_tags
}

resource "azurerm_bastion_host" "hub" {
  name                = local.bastion_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  sku                 = "Standard"
  tags                = local.common_tags

  ip_configuration {
    name                 = "bastion-ipcfg"
    subnet_id            = azurerm_subnet.bastion.id
    public_ip_address_id = azurerm_public_ip.bastion.id
  }
}
