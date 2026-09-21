# APIM Standard v2 in the hub. Two networking legs:
#   * VNet integration into snet-apim (delegated in hub-base) — outbound
#     calls to the Foundry model account resolve to its private endpoint.
#   * Private endpoint into snet-pe — inbound spoke traffic hits the
#     private FQDN <apim>.privatelink.azure-api.net.
# Public network access is disabled so the gateway is not reachable from
# the internet.

resource "azurerm_api_management" "apim" {
  name                = local.apim_name
  location            = local.location
  resource_group_name = local.hub_rg_name
  publisher_name      = var.apim_publisher_name
  publisher_email     = var.apim_publisher_email
  sku_name            = var.apim_sku
  # StandardV2 requires publicNetworkAccess=Enabled at *create* time even
  # when a private endpoint is planned. We flip it to Disabled below via
  # azapi_update_resource after the PE is in place.
  public_network_access_enabled = true
  virtual_network_type          = "External"
  tags                          = local.common_tags

  virtual_network_configuration {
    subnet_id = local.apim_subnet_id
  }

  identity {
    type = "SystemAssigned"
  }

  lifecycle {
    ignore_changes = [public_network_access_enabled]
  }
}

# ---------------------------------------------------------------------------
# APIM MI needs to read from the Foundry model account it fronts. This is
# how the authentication-managed-identity policy in apim-policy.xml.tftpl
# gets a token that the Foundry model account will accept.
# ---------------------------------------------------------------------------
resource "azurerm_role_assignment" "apim_cog_user_on_foundry_model" {
  scope                = azapi_resource.foundry_model_account.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_api_management.apim.identity[0].principal_id
}

# ---------------------------------------------------------------------------
# APIM inbound private endpoint (spoke-facing).
# ---------------------------------------------------------------------------
resource "azurerm_private_endpoint" "apim" {
  name                = "pe-${local.apim_name}"
  location            = local.location
  resource_group_name = local.hub_rg_name
  subnet_id           = local.private_endpoint_subnet_id
  tags                = local.common_tags

  private_service_connection {
    name                           = "psc-apim"
    private_connection_resource_id = azurerm_api_management.apim.id
    is_manual_connection           = false
    subresource_names              = ["Gateway"]
  }

  private_dns_zone_group {
    name                 = "apim-zones"
    private_dns_zone_ids = [local.private_dns_zone_ids["privatelink.azure-api.net"]]
  }
}

# ---------------------------------------------------------------------------
# Phase 2: once the private endpoint exists, flip APIM public network access
# to Disabled. Cannot be done at create time — Azure returns
# ActivateServiceWithPrivateEndpointAccessNotAllowed.
# ---------------------------------------------------------------------------
resource "azapi_update_resource" "apim_disable_public_access" {
  type        = "Microsoft.ApiManagement/service@2023-05-01-preview"
  resource_id = azurerm_api_management.apim.id

  body = {
    properties = {
      publicNetworkAccess = "Disabled"
    }
  }

  depends_on = [azurerm_private_endpoint.apim]
}

# ---------------------------------------------------------------------------
# App Insights + logger. Feeds the platform LAW attached in hub-base.
# ---------------------------------------------------------------------------
resource "azurerm_application_insights" "appi" {
  name                = local.appi_name
  location            = local.location
  resource_group_name = local.hub_rg_name
  workspace_id        = local.platform_law_id
  application_type    = "other"
  tags                = local.common_tags
}

resource "azurerm_api_management_logger" "appi" {
  name                = "appi-logger"
  api_management_name = azurerm_api_management.apim.name
  resource_group_name = local.hub_rg_name
  resource_id         = azurerm_application_insights.appi.id

  application_insights {
    instrumentation_key = azurerm_application_insights.appi.instrumentation_key
  }
}
