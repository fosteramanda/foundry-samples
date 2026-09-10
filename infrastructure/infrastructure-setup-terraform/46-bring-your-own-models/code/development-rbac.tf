resource "azurerm_role_assignment" "developer_on_project_alpha" {
  provider           = azurerm.development
  scope              = azapi_resource.project_alpha.id
  role_definition_id = "/subscriptions/${var.development_subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${var.developer_role_definition_id}"
  principal_id       = var.developer_group_object_id
  principal_type     = "Group"
}

resource "azurerm_role_assignment" "developer_on_project_beta" {
  provider           = azurerm.development
  scope              = azapi_resource.project_beta.id
  role_definition_id = "/subscriptions/${var.development_subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${var.developer_role_definition_id}"
  principal_id       = var.developer_group_object_id
  principal_type     = "Group"
}
