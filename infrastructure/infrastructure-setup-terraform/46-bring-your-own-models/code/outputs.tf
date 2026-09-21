# ============================================================================
# Platform tier outputs (Sub A)
# ============================================================================

output "platform_resource_group_name" {
  description = "Name of the Platform resource group in Sub A."
  value       = azurerm_resource_group.platform.name
}

output "foundry_model_account_name" {
  description = "Name of the Foundry model account in Sub A that hosts the backend gpt-4o deployment."
  value       = azapi_resource.foundry_model_account.name
}

output "apim_name" {
  description = "Name of the shared Azure API Management AI Gateway."
  value       = azurerm_api_management.apim.name
}

output "apim_gateway_url" {
  description = "APIM gateway URL. The Foundry ApiManagement connection targets <apim_gateway_url>/<inference_api_path>."
  value       = azurerm_api_management.apim.gateway_url
}

output "apim_inference_api_path" {
  description = "APIM inference API path segment. Combined with apim_gateway_url to form the connection target."
  value       = local.apim_api_path
}

output "key_vault_name" {
  description = "Name of the Key Vault holding third-party provider API keys."
  value       = azurerm_key_vault.kv.name
}

output "log_analytics_workspace_id" {
  description = "Log Analytics workspace ID for unified telemetry across all vendors."
  value       = azurerm_log_analytics_workspace.law.workspace_id
}

output "log_analytics_workspace_resource_id" {
  description = "Log Analytics workspace ARM resource ID."
  value       = azurerm_log_analytics_workspace.law.id
}

# ============================================================================
# Development tier outputs (Sub B)
# ============================================================================

output "development_resource_group_name" {
  description = "Name of the Development resource group in Sub B."
  value       = azurerm_resource_group.development.name
}

output "foundry_agent_account_name" {
  description = "Name of the Foundry agent account in Sub B that hosts the governed projects."
  value       = azapi_resource.foundry_agent_account.name
}

output "project_alpha_endpoint" {
  description = "Foundry project endpoint for Project Alpha. Use with AIProjectClient(endpoint=...)."
  value       = "https://${local.foundry_agent_account_name}.services.ai.azure.com/api/projects/alpha"
}

output "project_beta_endpoint" {
  description = "Foundry project endpoint for Project Beta. Use with AIProjectClient(endpoint=...)."
  value       = "https://${local.foundry_agent_account_name}.services.ai.azure.com/api/projects/beta"
}

output "approved_model_aliases" {
  description = "List of gateway-approved model aliases visible to every governed project. Reference in agent code as <connection-name>/<alias>."
  value       = [for _, m in local.catalog : m.alias]
}

output "gateway_connection_name" {
  description = "Name of the ApiManagement connection created on each project. Combine with an alias to reference a model: e.g., gateway/gpt-4o."
  value       = "gateway"
}
