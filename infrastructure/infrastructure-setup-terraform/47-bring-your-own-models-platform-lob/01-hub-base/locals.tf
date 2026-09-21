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
  random_suffix = random_string.suffix.result

  # --------------------------------------------------------------------------
  # Naming — hub-owned resources only.
  # --------------------------------------------------------------------------
  hub_rg_name       = "rg-${var.name_prefix}-hub-base-${local.random_suffix}"
  hub_vnet_name     = "vnet-${var.name_prefix}-hub-${local.random_suffix}"
  firewall_name     = "afw-${var.name_prefix}-hub-${local.random_suffix}"
  firewall_policy   = "afwp-${var.name_prefix}-hub-${local.random_suffix}"
  firewall_pip_name = "pip-afw-${var.name_prefix}-hub-${local.random_suffix}"
  bastion_name      = "bas-${var.name_prefix}-hub-${local.random_suffix}"
  bastion_pip_name  = "pip-bas-${var.name_prefix}-hub-${local.random_suffix}"
  law_name          = "law-${var.name_prefix}-hub-${local.random_suffix}"
  route_table_name  = "rt-${var.name_prefix}-hub-${local.random_suffix}"

  common_tags = {
    scenario          = "byom-platform-lob"
    sample            = "47-bring-your-own-models-platform-lob"
    plane             = "hub"
    root              = "hub-base"
    subscription_role = "platform"
  }

  # --------------------------------------------------------------------------
  # Private DNS zones the hub owns. These are linked to the hub VNet here;
  # the spoke link is added by the network-registration root so that neither
  # side reads the other's state directly.
  # --------------------------------------------------------------------------
  private_dns_zones = toset([
    "privatelink.azure-api.net",               # APIM private endpoint
    "privatelink.openai.azure.com",            # Foundry model account (OpenAI plane)
    "privatelink.cognitiveservices.azure.com", # Foundry model account (Cognitive Services plane)
    "privatelink.services.ai.azure.com",       # Foundry AI Services plane
    "privatelink.vaultcore.azure.net"          # Key Vault
  ])
}
