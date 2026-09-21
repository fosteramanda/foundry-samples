# hub-base runs against the Platform subscription only. It never touches the
# spoke subscription — that separation is enforced by not aliasing a spoke
# provider here at all.

provider "azurerm" {
  subscription_id = var.platform_subscription_id
  features {}
  storage_use_azuread = true
}

provider "azapi" {
  subscription_id = var.platform_subscription_id
  tenant_id       = var.tenant_id
  use_cli         = true
}
