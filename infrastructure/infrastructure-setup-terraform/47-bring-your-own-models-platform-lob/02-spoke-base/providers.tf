# spoke-base runs against the LoB (spoke) subscription only.

provider "azurerm" {
  subscription_id = var.spoke_subscription_id
  features {}
  storage_use_azuread = true
}

provider "azapi" {
  subscription_id = var.spoke_subscription_id
  tenant_id       = var.tenant_id
  use_cli         = true
}
