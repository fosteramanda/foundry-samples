# Two aliased AzureRM/AzAPI provider pairs — one for the Platform
# subscription (Sub A, administrator-owned) and one for the Development
# subscription (Sub B, developer-facing). The subscription IDs are supplied
# through variables; the deploying identity must have sufficient rights
# in both subscriptions to create the resources and role assignments
# declared in this module.

provider "azurerm" {
  alias           = "platform"
  subscription_id = var.platform_subscription_id
  features {
    key_vault {
      purge_soft_delete_on_destroy    = true
      recover_soft_deleted_key_vaults = true
    }
  }
  storage_use_azuread = true
}

provider "azurerm" {
  alias           = "development"
  subscription_id = var.development_subscription_id
  features {}
  storage_use_azuread = true
}

provider "azapi" {
  alias           = "platform"
  subscription_id = var.platform_subscription_id
  tenant_id       = var.tenant_id
  use_cli         = true
}

provider "azapi" {
  alias           = "development"
  subscription_id = var.development_subscription_id
  tenant_id       = var.tenant_id
  use_cli         = true
}
