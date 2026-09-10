# Spoke VNet, subnets, NSGs. Peering to hub is NOT declared here — the
# network-registration root owns the peering pair.

resource "azurerm_virtual_network" "spoke" {
  name                = local.spoke_vnet_name
  address_space       = var.spoke_vnet_address_space
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags
}

# App Gateway subnet — required to be dedicated to AGW.
resource "azurerm_subnet" "appgw" {
  name                 = "snet-appgw"
  resource_group_name  = azurerm_resource_group.spoke.name
  virtual_network_name = azurerm_virtual_network.spoke.name
  address_prefixes     = [var.appgw_subnet_prefix]
}

# Workload subnet — agent runtime, capability host, VMSS (if any) land here.
resource "azurerm_subnet" "workload" {
  name                 = "snet-workload"
  resource_group_name  = azurerm_resource_group.spoke.name
  virtual_network_name = azurerm_virtual_network.spoke.name
  address_prefixes     = [var.workload_subnet_prefix]
}

# Private endpoint subnet — Foundry agent account PE lands here.
resource "azurerm_subnet" "private_endpoints" {
  name                              = "snet-pe"
  resource_group_name               = azurerm_resource_group.spoke.name
  virtual_network_name              = azurerm_virtual_network.spoke.name
  address_prefixes                  = [var.private_endpoint_subnet_prefix]
  private_endpoint_network_policies = "Enabled"
}

# ---------------------------------------------------------------------------
# Force default egress through the hub Firewall.
#
# Route tables are subscription-local — we can't attach the hub RG's route
# table to spoke subnets when hub and spoke are in different subscriptions.
# So we create an equivalent RT here in the spoke that points its default
# route at the hub Firewall's private IP (published by hub-base output).
# VNet peering (created by 03-network-registration) carries the traffic
# across.
# ---------------------------------------------------------------------------
resource "azurerm_route_table" "spoke_forced_tunnel" {
  name                = local.spoke_rt_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags

  route {
    name                   = "default-via-hub-firewall"
    address_prefix         = "0.0.0.0/0"
    next_hop_type          = "VirtualAppliance"
    next_hop_in_ip_address = local.hub_firewall_private_ip
  }
}

# App Gateway subnet is intentionally excluded — AGW health probes need
# direct outbound via the platform's dedicated route.
resource "azurerm_subnet_route_table_association" "workload" {
  subnet_id      = azurerm_subnet.workload.id
  route_table_id = azurerm_route_table.spoke_forced_tunnel.id
}

resource "azurerm_subnet_route_table_association" "private_endpoints" {
  subnet_id      = azurerm_subnet.private_endpoints.id
  route_table_id = azurerm_route_table.spoke_forced_tunnel.id
}

# ---------------------------------------------------------------------------
# NSGs — one per subnet, no unnecessary inbound.
# ---------------------------------------------------------------------------
resource "azurerm_network_security_group" "appgw" {
  name                = local.nsg_appgw_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags

  # Required for AGW v2 health infrastructure.
  security_rule {
    name                       = "allow-gwm-inbound"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "65200-65535"
    source_address_prefix      = "GatewayManager"
    destination_address_prefix = "*"
  }

  # Public HTTPS in — this is the only public inbound in the whole sample.
  security_rule {
    name                       = "allow-https-inbound"
    priority                   = 110
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "443"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }

  # AzureLoadBalancer probes.
  security_rule {
    name                       = "allow-azurelb-inbound"
    priority                   = 120
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "AzureLoadBalancer"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "appgw" {
  subnet_id                 = azurerm_subnet.appgw.id
  network_security_group_id = azurerm_network_security_group.appgw.id
}

resource "azurerm_network_security_group" "workload" {
  name                = local.nsg_workload_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags

  # Deny inbound from Internet by default — S-series policy S5/S6 enforces
  # that no public IPs land on non-AGW subnets, but defence-in-depth here.
  security_rule {
    name                       = "deny-internet-inbound"
    priority                   = 4000
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "workload" {
  subnet_id                 = azurerm_subnet.workload.id
  network_security_group_id = azurerm_network_security_group.workload.id
}

resource "azurerm_network_security_group" "private_endpoints" {
  name                = local.nsg_pe_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags

  security_rule {
    name                       = "deny-internet-inbound"
    priority                   = 4000
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "private_endpoints" {
  subnet_id                 = azurerm_subnet.private_endpoints.id
  network_security_group_id = azurerm_network_security_group.private_endpoints.id
}
