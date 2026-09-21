# hub-workload runs against the Platform subscription only (same subscription
# as hub-base). The spoke provider is intentionally not aliased.

provider "azurerm" {
  subscription_id = var.platform_subscription_id
  features {
    key_vault {
      purge_soft_delete_on_destroy    = true
      recover_soft_deleted_key_vaults = true
    }
  }
  storage_use_azuread = true
}

provider "azapi" {
  subscription_id = var.platform_subscription_id
  tenant_id       = var.tenant_id
  use_cli         = true
}
