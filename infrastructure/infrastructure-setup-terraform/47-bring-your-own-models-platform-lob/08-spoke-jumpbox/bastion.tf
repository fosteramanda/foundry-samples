resource "azurerm_public_ip" "bastion" {
  name                = local.bastion_pip
  location            = local.spoke_location
  resource_group_name = local.spoke_rg_name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.common_tags
}

resource "azurerm_bastion_host" "spoke" {
  name                = local.bastion_name
  location            = local.spoke_location
  resource_group_name = local.spoke_rg_name
  sku                 = "Standard"
  tags                = local.common_tags

  # Required for `az network bastion ssh|tunnel` (native-client access).
  tunneling_enabled  = true
  ip_connect_enabled = true

  ip_configuration {
    name                 = "bastion-ip-config"
    subnet_id            = azurerm_subnet.bastion.id
    public_ip_address_id = azurerm_public_ip.bastion.id
  }
}
