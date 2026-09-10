# Outputs consumed by hub-workload and network-registration. These are the
# only cross-root data points from hub-base; nothing else in this state
# should be read by other roots.

output "hub_resource_group_name" {
  description = "Resource group that owns hub-base resources. Consumed by hub-workload for co-location and by network-registration for DNS zone lookups."
  value       = azurerm_resource_group.hub.name
}

output "hub_resource_group_id" {
  description = "Resource group id for policy re-scoping in hub-workload."
  value       = azurerm_resource_group.hub.id
}

output "hub_location" {
  description = "Region — spoke-base and network-registration must use the same value."
  value       = azurerm_resource_group.hub.location
}

output "hub_vnet_id" {
  description = "Hub VNet id — network-registration reads this to build the peering."
  value       = azurerm_virtual_network.hub.id
}

output "hub_vnet_name" {
  description = "Hub VNet name — network-registration uses this for peering names."
  value       = azurerm_virtual_network.hub.name
}

output "apim_subnet_id" {
  description = "Subnet id where APIM Standard v2 lands in hub-workload. Delegation is already applied."
  value       = azurerm_subnet.apim.id
}

output "private_endpoint_subnet_id" {
  description = "Subnet id for Foundry / APIM / Key Vault private endpoints in hub-workload."
  value       = azurerm_subnet.private_endpoints.id
}

output "private_dns_zone_ids" {
  description = "Private DNS zone ids keyed by zone name. hub-workload attaches PE A-records here; network-registration adds the spoke VNet link."
  value       = { for name, zone in azurerm_private_dns_zone.zones : name => zone.id }
}

output "private_dns_zone_names" {
  description = "Convenience list of private DNS zone names — same set as private_dns_zone_ids."
  value       = [for zone in azurerm_private_dns_zone.zones : zone.name]
}

output "spoke_forced_tunnel_route_table_id" {
  description = "Route table associated with spoke subnets by spoke-base to force default egress through the hub Firewall private IP."
  value       = azurerm_route_table.spoke_via_firewall.id
}

output "hub_firewall_private_ip" {
  description = "Hub Firewall private IP — published for observability. The spoke route table already carries it."
  value       = azurerm_firewall.hub.ip_configuration[0].private_ip_address
}

output "platform_log_analytics_workspace_id" {
  description = "Platform Log Analytics workspace id. hub-workload attaches APIM, Firewall, and Foundry diagnostics here."
  value       = azurerm_log_analytics_workspace.platform.id
}
