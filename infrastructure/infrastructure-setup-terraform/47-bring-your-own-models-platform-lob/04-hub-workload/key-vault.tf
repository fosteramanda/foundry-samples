# Key Vault for provider API keys. Two-phase network posture:
#   1. Create with public network access + default-Allow so the deployer
#      (running from a laptop / build agent on public internet) can write
#      the initial secret values.
#   2. After secrets are written, azapi_update_resource flips PNA to
#      Disabled and network_acls.default_action to Deny.
# lifecycle.ignore_changes keeps subsequent plans clean.
# Note: rotating a secret via a future apply requires temporarily
# re-enabling public access, or running Terraform from a jumpbox with a
# route to the private endpoint (Bastion in hub-base).

resource "azurerm_key_vault" "kv" {
  name                          = local.keyvault_name
  location                      = local.location
  resource_group_name           = local.hub_rg_name
  tenant_id                     = var.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = false
  soft_delete_retention_days    = 7
  public_network_access_enabled = true
  tags                          = local.common_tags

  network_acls {
    default_action = "Allow"
    bypass         = "AzureServices"
  }

  lifecycle {
    ignore_changes = [
      public_network_access_enabled,
      network_acls,
    ]
  }
}

resource "azurerm_private_endpoint" "keyvault" {
  name                = "pe-${local.keyvault_name}"
  location            = local.location
  resource_group_name = local.hub_rg_name
  subnet_id           = local.private_endpoint_subnet_id
  tags                = local.common_tags

  private_service_connection {
    name                           = "psc-keyvault"
    private_connection_resource_id = azurerm_key_vault.kv.id
    is_manual_connection           = false
    subresource_names              = ["vault"]
  }

  private_dns_zone_group {
    name                 = "keyvault-zones"
    private_dns_zone_ids = [local.private_dns_zone_ids["privatelink.vaultcore.azure.net"]]
  }
}

resource "azurerm_role_assignment" "kv_deployer_secrets_officer" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "kv_apim_secrets_user" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_api_management.apim.identity[0].principal_id
}

# RBAC propagation delay. Same 60-second sleep sample 46 uses — Entra
# takes a bit before a fresh Key Vault role assignment is honored.
resource "time_sleep" "kv_rbac_propagation" {
  depends_on = [
    azurerm_role_assignment.kv_deployer_secrets_officer,
    azurerm_role_assignment.kv_apim_secrets_user,
  ]
  create_duration = "60s"
}

resource "azurerm_key_vault_secret" "gemini_api_key" {
  name         = "gemini-api-key"
  value        = var.gemini_api_key
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  depends_on = [
    time_sleep.kv_rbac_propagation,
    azurerm_private_endpoint.keyvault,
  ]
}

resource "azurerm_key_vault_secret" "groq_api_key" {
  name         = "groq-api-key"
  value        = var.groq_api_key
  key_vault_id = azurerm_key_vault.kv.id
  content_type = "text/plain"

  depends_on = [
    time_sleep.kv_rbac_propagation,
    azurerm_private_endpoint.keyvault,
  ]
}

# Phase 2: after both secrets are written, close public network access.
resource "azapi_update_resource" "kv_disable_public_access" {
  type        = "Microsoft.KeyVault/vaults@2023-07-01"
  resource_id = azurerm_key_vault.kv.id

  body = {
    properties = {
      publicNetworkAccess = "Disabled"
      networkAcls = {
        defaultAction = "Deny"
        bypass        = "AzureServices"
      }
    }
  }

  depends_on = [
    azurerm_key_vault_secret.gemini_api_key,
    azurerm_key_vault_secret.groq_api_key,
    azurerm_private_endpoint.keyvault,
  ]
}
