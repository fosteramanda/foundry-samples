output "apim_api_policy_id" {
  description = "The APIM API policy resource id."
  value       = azurerm_api_management_api_policy.inference.id
}

output "allowlist_size" {
  description = "Convenience — number of OIDs currently allowed. Useful for CI to gate on non-zero after step 5."
  value       = length(var.project_oid_allowlist)
}
