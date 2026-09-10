# Capability hosts enable the Foundry data-proxy so prompt/tool calls out
# of the project respect the private-network posture (see sample 47 README
# § D4). Two capability hosts are required, and they must be created in
# order:
#
#   1. Account-level capability host on the Foundry agent account.
#   2. Project-level capability host under the project.
#
# Attempting to create the project-level one first returns
# "Foundry Account capabilityHost Not Found".
#
# Both are "kind = Agents" with no persistent stores for this sample.
# Production deployments typically add customer-owned Storage / AI Search
# / Cosmos connections here for thread persistence and vector search —
# outside the scope of this sample.

resource "azapi_resource" "account_capability_host" {
  type      = "Microsoft.CognitiveServices/accounts/capabilityHosts@2025-04-01-preview"
  name      = "${local.foundry_agent_account_name}-capHost"
  parent_id = azapi_resource.foundry_agent_account.id

  schema_validation_enabled = false

  body = {
    properties = {
      capabilityHostKind = "Agents"
    }
  }

  depends_on = [azurerm_private_endpoint.foundry_agent]
}

resource "azapi_resource" "project_capability_host" {
  type      = "Microsoft.CognitiveServices/accounts/projects/capabilityHosts@2025-04-01-preview"
  name      = "agents"
  parent_id = azapi_resource.project.id

  schema_validation_enabled = false

  body = {
    properties = {
      capabilityHostKind = "Agents"
    }
  }

  depends_on = [azapi_resource.account_capability_host]
}
