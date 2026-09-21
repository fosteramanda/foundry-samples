data "terraform_remote_state" "spoke_base" {
  backend = "local"
  config = {
    path = var.spoke_base_state_path
  }
}

data "terraform_remote_state" "hub_base" {
  backend = "local"
  config = {
    path = var.hub_base_state_path
  }
}

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length      = 5
  min_numeric = 2
  numeric     = true
  special     = false
  lower       = true
  upper       = false
}

locals {
  contract = jsondecode(file(var.contract_path))

  spoke_rg_name = data.terraform_remote_state.spoke_base.outputs.spoke_resource_group_name
  spoke_rg_id   = data.terraform_remote_state.spoke_base.outputs.spoke_resource_group_id
  location      = data.terraform_remote_state.spoke_base.outputs.spoke_location
  spoke_pe_snet = data.terraform_remote_state.spoke_base.outputs.private_endpoint_subnet_id

  # Foundry-account PEs need A records in these three hub-owned zones. Zone
  # IDs are cross-subscription; the PE resource + its DNS zone group live
  # on the spoke side, but the zone IDs point into the hub subscription.
  hub_dns_zones = data.terraform_remote_state.hub_base.outputs.private_dns_zone_ids
  foundry_dns_zone_ids = [
    local.hub_dns_zones["privatelink.services.ai.azure.com"],
    local.hub_dns_zones["privatelink.openai.azure.com"],
    local.hub_dns_zones["privatelink.cognitiveservices.azure.com"],
  ]

  random_suffix = random_string.suffix.result

  # Foundry agent account name must be a valid custom subdomain (lowercase,
  # alphanumeric, hyphens allowed). Compressed further to fit account limits.
  foundry_agent_account_name = lower(replace("${var.name_prefix}-fdry-${local.random_suffix}", "_", "-"))

  # Contract fields — the only handshake with the platform.
  gateway_private_url = local.contract.gateway.private_url
  gateway_audience    = local.contract.gateway.audience
  gateway_api_version = local.contract.gateway.inference_api_version
  approved_catalog    = local.contract.catalog.aliases
  catalog_version     = local.contract.catalog.version

  # ApiManagement connection metadata expects models as a JSON *string* (not
  # a nested object). This is what makes the catalog show up in the Foundry
  # portal's model picker and — critically — is what Foundry uses at
  # invocation time to validate `agent.model = "<connection>/<alias>"`.
  #
  # The required shape mirrors the official Foundry APIM connection sample:
  # each entry names the alias and carries a `properties.model` sub-object
  # with `name` + `version` + `format`. Any other shape (e.g. flat
  # `{name, apiVersion}`) makes Foundry return
  # "Model 'X' not found in connection 'gateway'" at run time.
  approved_catalog_models_json = jsonencode([
    for m in local.approved_catalog : {
      name = m.alias
      properties = {
        model = {
          # Inner name == the alias (matches sample 46). Because
          # deploymentInPath = true, Foundry builds URLs of the form
          # {target}/deployments/{model.name}/chat/completions — that
          # path segment must equal the alias APIM's inference policy
          # is switching on (see 06-hub-allowlist/apim-policy.xml.tftpl).
          name    = m.alias
          version = "1"
          format  = "OpenAI"
        }
      }
    }
  ])

  gateway_connection_metadata = {
    audience            = local.gateway_audience
    deploymentInPath    = "true"
    inferenceAPIVersion = local.gateway_api_version
    models              = local.approved_catalog_models_json
    customHeaders       = "{}"
    authConfig          = "{}"
  }

  common_tags = {
    scenario          = "byom-platform-lob"
    sample            = "47-bring-your-own-models-platform-lob"
    plane             = "spoke"
    root              = "spoke-workload/project"
    subscription_role = "lob"
    lob               = var.name_prefix
  }
}
