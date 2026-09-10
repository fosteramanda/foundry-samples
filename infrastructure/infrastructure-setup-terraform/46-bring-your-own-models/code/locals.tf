# ============================================================================
# Client-config data sources and shared computed values.
# ============================================================================

data "azurerm_client_config" "platform" {
  provider = azurerm.platform
}

data "azurerm_client_config" "development" {
  provider = azurerm.development
}

resource "random_string" "suffix" {
  length      = 5
  min_numeric = 2
  numeric     = true
  special     = false
  lower       = true
  upper       = false
}

locals {
  tenant_id     = data.azurerm_client_config.platform.tenant_id
  random_suffix = random_string.suffix.result

  # ------------------------------------------------------------------------
  # Naming.
  # ------------------------------------------------------------------------
  platform_rg_name    = "rg-${var.name_prefix}-platform-${local.random_suffix}"
  development_rg_name = "rg-${var.name_prefix}-development-${local.random_suffix}"

  foundry_model_account_name = lower(replace("${var.name_prefix}models${local.random_suffix}", "-", ""))
  foundry_agent_account_name = lower(replace("${var.name_prefix}agents${local.random_suffix}", "-", ""))

  apim_name     = "${var.name_prefix}-apim-${local.random_suffix}"
  keyvault_name = substr(lower(replace("${var.name_prefix}kv${local.random_suffix}", "-", "")), 0, 24)
  law_name      = "law-${var.name_prefix}-${local.random_suffix}"
  appi_name     = "appi-${var.name_prefix}-${local.random_suffix}"

  # ------------------------------------------------------------------------
  # APIM API path and token audience.
  # ------------------------------------------------------------------------
  apim_api_path  = "inference"
  token_audience = "https://cognitiveservices.azure.com"

  # ------------------------------------------------------------------------
  # Approved catalog.
  # ------------------------------------------------------------------------
  catalog = {
    foundry = {
      alias          = var.foundry_model_name
      vendor         = "microsoft-foundry"
      upstream_model = var.foundry_model_name
    }
    gemini = {
      alias          = var.gemini_alias
      vendor         = "google-gemini"
      upstream_model = var.gemini_model_name
    }
    groq = {
      alias          = var.groq_alias
      vendor         = "groq-llama"
      upstream_model = var.groq_model_name
    }
  }

  approved_catalog_models_json = jsonencode([
    for key, model in local.catalog : {
      name = model.alias
      properties = {
        model = {
          name    = model.alias
          version = "1"
          format  = "OpenAI"
        }
      }
    }
  ])

  apim_gateway_target = "${azurerm_api_management.apim.gateway_url}/${local.apim_api_path}"
}
