# Link every hub-owned private DNS zone to the spoke VNet. This is what
# lets spoke-workload resolve <apim>.privatelink.azure-api.net to the
# APIM PE in the hub subscription without any cross-subscription DNS setup.
#
# The DNS zones live in the hub RG; the spoke VNet lives in the spoke
# subscription. The hub-aliased provider is used because the DNS zone
# resource is what we're linking against.

resource "azurerm_private_dns_zone_virtual_network_link" "spoke_links" {
  provider = azurerm.hub

  for_each = local.hub_dns_zones

  name                  = "link-spoke-${replace(each.key, ".", "-")}"
  resource_group_name   = local.hub_rg_name
  private_dns_zone_name = each.key
  virtual_network_id    = local.spoke_vnet_id
  registration_enabled  = false
  tags                  = local.common_tags
}
