resource "azurerm_api_management_named_value" "gemini_api_key" {
  name                = "gemini-api-key"
  display_name        = "gemini-api-key"
  resource_group_name = local.hub_rg_name
  api_management_name = azurerm_api_management.apim.name
  secret              = true

  value_from_key_vault {
    secret_id = azurerm_key_vault_secret.gemini_api_key.versionless_id
  }

  depends_on = [azurerm_role_assignment.kv_apim_secrets_user]
}

resource "azurerm_api_management_named_value" "groq_api_key" {
  name                = "groq-api-key"
  display_name        = "groq-api-key"
  resource_group_name = local.hub_rg_name
  api_management_name = azurerm_api_management.apim.name
  secret              = true

  value_from_key_vault {
    secret_id = azurerm_key_vault_secret.groq_api_key.versionless_id
  }

  depends_on = [azurerm_role_assignment.kv_apim_secrets_user]
}
