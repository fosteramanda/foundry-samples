resource "azurerm_log_analytics_workspace" "lob" {
  name                = local.law_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  sku                 = "PerGB2018"
  retention_in_days   = var.law_retention_in_days
  tags                = local.common_tags
}
