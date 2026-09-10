# Two resource groups: one per subscription.

resource "azurerm_resource_group" "platform" {
  provider = azurerm.platform
  name     = local.platform_rg_name
  location = var.location

  tags = {
    scenario          = "byom-multi-vendor"
    sample            = "46-bring-your-own-models"
    tier              = "platform"
    subscription_role = "admin-owned"
  }
}

resource "azurerm_resource_group" "development" {
  provider = azurerm.development
  name     = local.development_rg_name
  location = var.location

  tags = {
    scenario          = "byom-multi-vendor"
    sample            = "46-bring-your-own-models"
    tier              = "development"
    subscription_role = "developer-facing"
  }
}
