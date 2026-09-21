resource "azurerm_api_management" "apim" {
  provider            = azurerm.platform
  name                = local.apim_name
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  publisher_name      = var.apim_publisher_name
  publisher_email     = var.apim_publisher_email
  sku_name            = var.apim_sku

  identity {
    type = "SystemAssigned"
  }
}

resource "azurerm_role_assignment" "apim_cog_user_on_foundry_model" {
  provider             = azurerm.platform
  scope                = azapi_resource.foundry_model_account.id
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_api_management.apim.identity[0].principal_id
}

resource "azurerm_api_management_logger" "appi" {
  provider            = azurerm.platform
  name                = "appi-logger"
  api_management_name = azurerm_api_management.apim.name
  resource_group_name = azurerm_resource_group.platform.name
  resource_id         = azurerm_application_insights.appi.id

  application_insights {
    instrumentation_key = azurerm_application_insights.appi.instrumentation_key
  }
}
