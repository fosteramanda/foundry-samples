# Foundry model deployment. Its name (e.g. gpt-4.1) is the deployment
# segment that Foundry SDK includes in the URL. APIM matches on this name
# and forwards to the Foundry account through the private endpoint.
#
# The SKU is fixed at GlobalStandard so H2 permits it (allowlist:
# GlobalStandard, DataZoneStandard).

resource "azapi_resource" "foundry_gpt_deployment" {
  type      = "Microsoft.CognitiveServices/accounts/deployments@2025-04-01-preview"
  name      = var.foundry_model_name
  parent_id = azapi_resource.foundry_model_account.id

  body = {
    sku = {
      name     = "GlobalStandard"
      capacity = var.foundry_model_capacity
    }
    properties = {
      model = {
        name    = var.foundry_model_name
        format  = "OpenAI"
        version = var.foundry_model_version
      }
    }
  }
}
