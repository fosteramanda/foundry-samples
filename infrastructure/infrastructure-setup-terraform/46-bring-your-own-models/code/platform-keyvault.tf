resource "azurerm_key_vault" "kv" {
  provider                      = azurerm.platform
  name                          = local.keyvault_name
  location                      = var.location
  resource_group_name           = azurerm_resource_group.platform.name
  tenant_id                     = local.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = false
  soft_delete_retention_days    = 7
  public_network_access_enabled = true
}

resource "azurerm_role_assignment" "kv_deployer_secrets_officer" {
  provider             = azurerm.platform
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.platform.object_id
}

resource "azurerm_role_assignment" "kv_apim_secrets_user" {
  provider             = azurerm.platform
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_api_management.apim.identity[0].principal_id
}

resource "time_sleep" "kv_rbac_propagation" {
  depends_on = [
    azurerm_role_assignment.kv_deployer_secrets_officer,
    azurerm_role_assignment.kv_apim_secrets_user,
  ]
  create_duration = "60s"
}

resource "azurerm_key_vault_secret" "gemini_api_key" {
  provider     = azurerm.platform
  name         = "gemini-api-key"
  value        = var.gemini_api_key
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  depends_on = [time_sleep.kv_rbac_propagation]
}

resource "azurerm_key_vault_secret" "groq_api_key" {
  provider     = azurerm.platform
  name         = "groq-api-key"
  value        = var.groq_api_key
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  depends_on = [time_sleep.kv_rbac_propagation]
}
