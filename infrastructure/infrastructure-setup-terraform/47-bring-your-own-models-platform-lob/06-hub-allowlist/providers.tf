provider "azurerm" {
  features {}
  subscription_id = var.platform_subscription_id
  tenant_id       = var.tenant_id
}
