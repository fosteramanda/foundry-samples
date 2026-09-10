output "foundry_agent_account_id" {
  description = "Foundry agent account id."
  value       = azapi_resource.foundry_agent_account.id
}

output "foundry_agent_account_name" {
  description = "Foundry agent account name."
  value       = azapi_resource.foundry_agent_account.name
}

output "project_id" {
  description = "Foundry project id. Consumed by spoke-workload/agent."
  value       = azapi_resource.project.id
}

output "project_name" {
  description = "Foundry project name."
  value       = azapi_resource.project.name
}

output "project_managed_identity_object_id" {
  description = <<-EOT
    Object ID of the project's system-assigned managed identity. This is
    the value the platform admin pastes into
    hub-allowlist/allowlist.auto.tfvars via the step-4 PR.
  EOT
  value       = azapi_resource.project.output.identity.principalId
}

output "connection_name" {
  description = "APIM connection name attached to the project."
  value       = azapi_resource.connection_gateway.name
}

output "catalog_version_consumed" {
  description = "Version of the platform catalog this project was created against. Useful for drift detection if the platform bumps the contract."
  value       = local.catalog_version
}
