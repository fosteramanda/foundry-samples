resource "azurerm_resource_group" "spoke" {
  name     = local.spoke_rg_name
  location = var.location
  tags     = local.common_tags
}
