resource "azurerm_log_analytics_workspace" "law" {
  provider            = azurerm.platform
  name                = local.law_name
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "appi" {
  provider            = azurerm.platform
  name                = local.appi_name
  location            = var.location
  resource_group_name = azurerm_resource_group.platform.name
  workspace_id        = azurerm_log_analytics_workspace.law.id
  application_type    = "other"
}
