# Bastion needs a dedicated subnet named EXACTLY "AzureBastionSubnet".
resource "azurerm_subnet" "bastion" {
  name                 = "AzureBastionSubnet"
  resource_group_name  = local.spoke_rg_name
  virtual_network_name = local.spoke_vnet_name
  address_prefixes     = [var.bastion_subnet_prefix]
}

# Small subnet for the jumpbox VM NIC.
resource "azurerm_subnet" "jumpbox" {
  name                 = "snet-jumpbox"
  resource_group_name  = local.spoke_rg_name
  virtual_network_name = local.spoke_vnet_name
  address_prefixes     = [var.jumpbox_subnet_prefix]
}

# NSG on the jumpbox subnet — deny all inbound except from Bastion.
resource "azurerm_network_security_group" "jumpbox" {
  name                = "nsg-snet-jumpbox"
  location            = local.spoke_location
  resource_group_name = local.spoke_rg_name
  tags                = local.common_tags

  security_rule {
    name                       = "allow-bastion-ssh-in"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "22"
    source_address_prefix      = var.bastion_subnet_prefix
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "jumpbox" {
  subnet_id                 = azurerm_subnet.jumpbox.id
  network_security_group_id = azurerm_network_security_group.jumpbox.id
}
