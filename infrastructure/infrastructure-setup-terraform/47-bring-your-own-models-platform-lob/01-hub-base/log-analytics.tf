# Platform Log Analytics workspace. hub-workload attaches APIM, Firewall,
# and Foundry-account diagnostics here. The spoke LAW is a separate
# workspace owned by spoke-base — LoB business data does not flow up.

resource "azurerm_log_analytics_workspace" "platform" {
  name                = local.law_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  sku                 = "PerGB2018"
  retention_in_days   = var.law_retention_in_days
  tags                = local.common_tags
}
