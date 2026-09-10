# ApiManagement connection on the project. Points at the platform gateway's
# **private URL** — reachable only because network-registration linked the
# spoke VNet to the privatelink.azure-api.net zone owned by the hub.
#
# The connection uses ProjectManagedIdentity auth: at request time the
# Foundry SDK trades the project MI for a token whose `oid` claim matches
# what hub-allowlist added to the APIM policy.

resource "azapi_resource" "connection_gateway" {
  type      = "Microsoft.CognitiveServices/accounts/projects/connections@2025-04-01-preview"
  name      = "gateway"
  parent_id = azapi_resource.project.id

  schema_validation_enabled = false

  body = {
    properties = {
      category      = "ApiManagement"
      target        = local.gateway_private_url
      authType      = "ProjectManagedIdentity"
      audience      = local.gateway_audience
      isSharedToAll = false
      credentials   = {}
      metadata      = local.gateway_connection_metadata
    }
  }

  depends_on = [azapi_resource.project_capability_host]
}
