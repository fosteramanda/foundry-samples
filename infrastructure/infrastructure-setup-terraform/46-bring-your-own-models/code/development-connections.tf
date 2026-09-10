# One ApiManagement connection per project. Each connection surfaces the
# full approved model catalog under a shared connection name — developers
# reference models as `gateway/<alias>`. The connection uses
# ProjectManagedIdentity auth: at request time the Foundry SDK trades the
# project MI for a token whose `appid` claim identifies the calling project.
#
# Two critical shape rules for ApiManagement connections:
#   * metadata.models must be a JSON *string* (not a nested object). This is
#     what makes the catalog visible in the Foundry portal's model picker.
#   * audience must match the APIM validate-azure-ad-token policy exactly
#     (no trailing slash mismatch).

locals {
  gateway_connection_metadata = {
    audience            = local.token_audience
    deploymentInPath    = "true"
    inferenceAPIVersion = var.inference_api_version
    models              = local.approved_catalog_models_json
    customHeaders       = "{}"
    authConfig          = "{}"
  }
}

resource "azapi_resource" "connection_gateway_alpha" {
  provider  = azapi.development
  type      = "Microsoft.CognitiveServices/accounts/projects/connections@2025-04-01-preview"
  name      = "gateway"
  parent_id = azapi_resource.project_alpha.id

  # ProjectManagedIdentity is valid per the Foundry BYOM contract but not
  # yet listed in azapi's embedded schema; skip client-side validation.
  schema_validation_enabled = false

  body = {
    properties = {
      category      = "ApiManagement"
      target        = local.apim_gateway_target
      authType      = "ProjectManagedIdentity"
      audience      = local.token_audience
      isSharedToAll = false
      credentials   = {}
      metadata      = local.gateway_connection_metadata
    }
  }

  # Ensure the APIM policy (which allowlists this project's MI) exists
  # before the connection is created so first-call telemetry lines up.
  depends_on = [azurerm_api_management_api_policy.inference]
}

resource "azapi_resource" "connection_gateway_beta" {
  provider  = azapi.development
  type      = "Microsoft.CognitiveServices/accounts/projects/connections@2025-04-01-preview"
  name      = "gateway"
  parent_id = azapi_resource.project_beta.id

  schema_validation_enabled = false

  body = {
    properties = {
      category      = "ApiManagement"
      target        = local.apim_gateway_target
      authType      = "ProjectManagedIdentity"
      audience      = local.token_audience
      isSharedToAll = false
      credentials   = {}
      metadata      = local.gateway_connection_metadata
    }
  }

  depends_on = [azurerm_api_management_api_policy.inference]
}
