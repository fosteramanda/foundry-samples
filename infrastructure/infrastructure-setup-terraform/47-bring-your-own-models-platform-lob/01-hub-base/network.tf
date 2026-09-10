# Hub VNet, subnets, and the private DNS zones the hub owns.
# Peering to spoke VNets is intentionally NOT declared here — that is the
# job of the network-registration root, so hub-base can be applied before
# any spoke exists.

resource "azurerm_virtual_network" "hub" {
  name                = local.hub_vnet_name
  address_space       = var.hub_vnet_address_space
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  tags                = local.common_tags
}

# AzureFirewallSubnet — name is required by Azure Firewall.
resource "azurerm_subnet" "firewall" {
  name                 = "AzureFirewallSubnet"
  resource_group_name  = azurerm_resource_group.hub.name
  virtual_network_name = azurerm_virtual_network.hub.name
  address_prefixes     = [var.firewall_subnet_prefix]
}

# AzureBastionSubnet — name is required by Azure Bastion.
resource "azurerm_subnet" "bastion" {
  name                 = "AzureBastionSubnet"
  resource_group_name  = azurerm_resource_group.hub.name
  virtual_network_name = azurerm_virtual_network.hub.name
  address_prefixes     = [var.bastion_subnet_prefix]
}

# APIM Standard v2 subnet. Delegation is applied here so that peering and
# DNS linkage can be brought up before the hub-workload apply creates APIM.
resource "azurerm_subnet" "apim" {
  name                 = "snet-apim"
  resource_group_name  = azurerm_resource_group.hub.name
  virtual_network_name = azurerm_virtual_network.hub.name
  address_prefixes     = [var.apim_subnet_prefix]

  delegation {
    name = "apim-stv2"
    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

# APIM StandardV2 requires an NSG on its subnet even when public network
# access is disabled — Azure refuses service create with
# `NetworkSecurityGroupNotFound` otherwise. StandardV2 does not require the
# heavy v1 rule set; default rules are sufficient for a private-endpoint-only
# service. See https://aka.ms/apimvnet.
resource "azurerm_network_security_group" "apim" {
  name                = "nsg-snet-apim"
  location            = azurerm_resource_group.hub.location
  resource_group_name = azurerm_resource_group.hub.name
  tags                = local.common_tags
}

resource "azurerm_subnet_network_security_group_association" "apim" {
  subnet_id                 = azurerm_subnet.apim.id
  network_security_group_id = azurerm_network_security_group.apim.id
}

# Private endpoint subnet — Foundry, APIM, and Key Vault PEs land here.
resource "azurerm_subnet" "private_endpoints" {
  name                              = "snet-pe"
  resource_group_name               = azurerm_resource_group.hub.name
  virtual_network_name              = azurerm_virtual_network.hub.name
  address_prefixes                  = [var.private_endpoint_subnet_prefix]
  private_endpoint_network_policies = "Enabled"
}

# ----------------------------------------------------------------------------
# Private DNS zones — hub-owned, linked to the hub VNet only. The
# network-registration root adds the spoke VNet link so both planes resolve
# the same private FQDNs.
# ----------------------------------------------------------------------------
resource "azurerm_private_dns_zone" "zones" {
  for_each = local.private_dns_zones

  name                = each.value
  resource_group_name = azurerm_resource_group.hub.name
  tags                = local.common_tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "hub_links" {
  for_each = azurerm_private_dns_zone.zones

  name                  = "link-hub-${replace(each.key, ".", "-")}"
  resource_group_name   = azurerm_resource_group.hub.name
  private_dns_zone_name = each.value.name
  virtual_network_id    = azurerm_virtual_network.hub.id
  registration_enabled  = false
  tags                  = local.common_tags
}

# ----------------------------------------------------------------------------
# Route table advertised to spokes via network-registration. Forces spoke
# egress through the hub Firewall. Not attached to hub subnets — this is
# consumed by spoke-base, not by the hub itself.
# ----------------------------------------------------------------------------
resource "azurerm_route_table" "spoke_via_firewall" {
  name                = local.route_table_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  tags                = local.common_tags
}

resource "azurerm_route" "spoke_default_to_firewall" {
  name                   = "route-default-to-hub-fw"
  resource_group_name    = azurerm_resource_group.hub.name
  route_table_name       = azurerm_route_table.spoke_via_firewall.name
  address_prefix         = "0.0.0.0/0"
  next_hop_type          = "VirtualAppliance"
  next_hop_in_ip_address = azurerm_firewall.hub.ip_configuration[0].private_ip_address
}
