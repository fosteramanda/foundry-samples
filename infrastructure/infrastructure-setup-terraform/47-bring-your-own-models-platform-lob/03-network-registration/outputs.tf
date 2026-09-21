output "peering_hub_to_spoke_id" {
  description = "Hub-side peering id."
  value       = azurerm_virtual_network_peering.hub_to_spoke.id
}

output "peering_spoke_to_hub_id" {
  description = "Spoke-side peering id."
  value       = azurerm_virtual_network_peering.spoke_to_hub.id
}

output "spoke_dns_link_ids" {
  description = "IDs of the private DNS zone links to the spoke VNet, keyed by zone name."
  value       = { for k, v in azurerm_private_dns_zone_virtual_network_link.spoke_links : k => v.id }
}
