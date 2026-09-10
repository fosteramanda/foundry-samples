output "spoke_resource_group_name" {
  description = "LoB resource group. Consumed by network-registration (peering DNS) and spoke-workload."
  value       = azurerm_resource_group.spoke.name
}

output "spoke_resource_group_id" {
  description = "LoB resource group id."
  value       = azurerm_resource_group.spoke.id
}

output "spoke_location" {
  description = "Region — must match hub-base."
  value       = azurerm_resource_group.spoke.location
}

output "spoke_vnet_id" {
  description = "Spoke VNet id. Consumed by network-registration (peering) and by spoke-workload for PE subnet lookup."
  value       = azurerm_virtual_network.spoke.id
}

output "spoke_vnet_name" {
  description = "Spoke VNet name."
  value       = azurerm_virtual_network.spoke.name
}

output "workload_subnet_id" {
  description = "Workload subnet id — where the agent runtime and capability host consumers land."
  value       = azurerm_subnet.workload.id
}

output "private_endpoint_subnet_id" {
  description = "Spoke PE subnet id. spoke-workload/project places the Foundry agent account PE here."
  value       = azurerm_subnet.private_endpoints.id
}

output "app_gateway_id" {
  description = "App Gateway id — spoke-workload/agent adds real listeners and backend pools."
  value       = azurerm_application_gateway.spoke.id
}

output "app_gateway_name" {
  description = "App Gateway name."
  value       = azurerm_application_gateway.spoke.name
}

output "app_gateway_public_fqdn" {
  description = "AGW public IP FQDN — the only public entry point in the whole sample. Empty until PIP is provisioned."
  value       = azurerm_public_ip.appgw.fqdn
}

output "app_gateway_public_ip" {
  description = "AGW public IP — the only public address in the whole sample."
  value       = azurerm_public_ip.appgw.ip_address
}

output "lob_log_analytics_workspace_id" {
  description = "LoB LAW id. Consumed by spoke-workload for diagnostic settings."
  value       = azurerm_log_analytics_workspace.lob.id
}
