# network-registration is the ONLY root that talks to both subscriptions.
# It brings up the peering pair and the spoke-side DNS zone links, then
# stays out of the way. Neither hub-base nor spoke-base needs to know
# about the other side of the peering.

provider "azurerm" {
  alias           = "hub"
  subscription_id = var.platform_subscription_id
  features {}
  storage_use_azuread = true
}

provider "azurerm" {
  alias           = "spoke"
  subscription_id = var.spoke_subscription_id
  features {}
  storage_use_azuread = true
}
