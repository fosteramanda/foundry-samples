resource "azurerm_api_management_api_policy" "inference" {
  provider            = azurerm.platform
  api_name            = azurerm_api_management_api.inference.name
  api_management_name = azurerm_api_management.apim.name
  resource_group_name = azurerm_resource_group.platform.name

  xml_content = templatefile("${path.module}/apim-policy.xml.tftpl", {
    tenant_id               = local.tenant_id
    token_audience          = local.token_audience
    quota_tokens_per_minute = var.quota_tokens_per_minute

    foundry_endpoint       = "https://${local.foundry_model_account_name}.openai.azure.com"
    foundry_managed_id_res = local.token_audience
    foundry_alias          = var.foundry_model_name

    gemini_alias    = var.gemini_alias
    gemini_upstream = "https://generativelanguage.googleapis.com/v1beta/openai"
    gemini_model    = var.gemini_model_name

    groq_alias    = var.groq_alias
    groq_upstream = "https://api.groq.com/openai/v1"
    groq_model    = var.groq_model_name

    project_alpha_oid = azapi_resource.project_alpha.identity[0].principal_id
    project_beta_oid  = azapi_resource.project_beta.identity[0].principal_id
  })

  depends_on = [
    azurerm_api_management_named_value.gemini_api_key,
    azurerm_api_management_named_value.groq_api_key,
    azurerm_role_assignment.apim_cog_user_on_foundry_model,
    azurerm_api_management_api.inference,
  ]
}
