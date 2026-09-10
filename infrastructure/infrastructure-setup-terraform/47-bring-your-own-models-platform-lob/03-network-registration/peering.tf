# Both sides of the hub<->spoke VNet peering. Applied by the network-
# registration pipeline (admin-owned) so neither hub-base nor spoke-base
# has any dependency on the other's state.

resource "azurerm_virtual_network_peering" "hub_to_spoke" {
  provider                  = azurerm.hub
  name                      = "${var.peering_name_prefix}-hub-to-spoke"
  resource_group_name       = local.hub_rg_name
  virtual_network_name      = local.hub_vnet_name
  remote_virtual_network_id = local.spoke_vnet_id

  allow_virtual_network_access = true
  allow_forwarded_traffic      = true
  allow_gateway_transit        = false
  use_remote_gateways          = false
}

resource "azurerm_virtual_network_peering" "spoke_to_hub" {
  provider                  = azurerm.spoke
  name                      = "${var.peering_name_prefix}-spoke-to-hub"
  resource_group_name       = local.spoke_rg_name
  virtual_network_name      = local.spoke_vnet_name
  remote_virtual_network_id = local.hub_vnet_id

  allow_virtual_network_access = true
  allow_forwarded_traffic      = true
  allow_gateway_transit        = false
  use_remote_gateways          = false
}
