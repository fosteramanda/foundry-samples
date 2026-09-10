# H-series Azure Policy assignments (hub scope).
#
# Assigned at the hub resource group in this sample so peers can keep using
# the same subscription for other testing. In production these belong at
# subscription scope (or higher — management group is the recommended
# landing-zone shape). Effect is Deny per the sample's demo posture; see
# ../README.md § "Azure Policy guardrails".

# ---------------------------------------------------------------------------
# H1 — Built-in "Foundry model deployments should only use approved models"
# ---------------------------------------------------------------------------
data "azurerm_policy_definition_built_in" "h1_approved_models" {
  display_name = "Foundry model deployments should only use approved models"
}

resource "azurerm_resource_group_policy_assignment" "h1_approved_models" {
  name                 = "h1-approved-models"
  display_name         = "H1 — Approved models on hub Foundry deployments"
  resource_group_id    = azurerm_resource_group.hub.id
  policy_definition_id = data.azurerm_policy_definition_built_in.h1_approved_models.id
  description          = "Hub-scoped allowlist by publisher (and optional asset id). Spokes use the same policy with an empty allowlist (see S1)."

  parameters = jsonencode({
    effect            = { value = var.policy_effect_hub }
    allowedPublishers = { value = var.approved_model_publishers }
    allowedAssetIds   = { value = var.approved_model_asset_ids }
  })
}

# ---------------------------------------------------------------------------
# H2 — Restrict Foundry deployment SKUs (custom).
#      Shape follows the docs:
#      https://learn.microsoft.com/azure/foundry/foundry-models/concepts/deployment-types#restrict-deployment-types-with-azure-policy
# ---------------------------------------------------------------------------
resource "azurerm_policy_definition" "h2_restrict_sku" {
  name         = "h2-foundry-restrict-deployment-sku"
  policy_type  = "Custom"
  mode         = "All"
  display_name = "H2 — Foundry deployments must use an approved SKU"
  description  = "Denies Foundry model deployments whose sku.name is not in the allowlist. Blocks PTU / Batch / DeveloperTier by omission."

  metadata = jsonencode({ category = "Foundry (BYOM sample 47)" })

  parameters = jsonencode({
    effect = {
      type          = "String"
      allowedValues = ["Deny", "Audit", "Disabled"]
      defaultValue  = "Deny"
      metadata      = { displayName = "Effect" }
    }
    allowedSkus = {
      type         = "Array"
      defaultValue = ["GlobalStandard", "DataZoneStandard"]
      metadata     = { displayName = "Allowed deployment SKUs" }
    }
  })

  policy_rule = jsonencode({
    if = {
      allOf = [
        { field = "type", equals = "Microsoft.CognitiveServices/accounts/deployments" },
        {
          not = {
            field = "Microsoft.CognitiveServices/accounts/deployments/sku.name"
            in    = "[parameters('allowedSkus')]"
          }
        }
      ]
    }
    then = { effect = "[parameters('effect')]" }
  })
}

resource "azurerm_resource_group_policy_assignment" "h2_restrict_sku" {
  name                 = "h2-restrict-sku"
  display_name         = "H2 — Restrict deployment SKU on hub Foundry"
  resource_group_id    = azurerm_resource_group.hub.id
  policy_definition_id = azurerm_policy_definition.h2_restrict_sku.id

  parameters = jsonencode({
    effect      = { value = var.policy_effect_hub }
    allowedSkus = { value = var.approved_deployment_skus }
  })
}

# ---------------------------------------------------------------------------
# H3 — Foundry accounts must have publicNetworkAccess = Disabled (custom)
# ---------------------------------------------------------------------------
resource "azurerm_policy_definition" "h3_pna_disabled" {
  name         = "h3-foundry-pna-disabled"
  policy_type  = "Custom"
  mode         = "All"
  display_name = "H3 — Foundry accounts must disable public network access"
  description  = "Ensures every Foundry account in scope is unreachable from the public internet — traffic must arrive via private endpoint."

  metadata = jsonencode({ category = "Foundry (BYOM sample 47)" })

  parameters = jsonencode({
    effect = {
      type          = "String"
      allowedValues = ["Deny", "Audit", "Disabled"]
      defaultValue  = "Deny"
      metadata      = { displayName = "Effect" }
    }
  })

  policy_rule = jsonencode({
    if = {
      allOf = [
        { field = "type", equals = "Microsoft.CognitiveServices/accounts" },
        {
          field     = "Microsoft.CognitiveServices/accounts/publicNetworkAccess"
          notEquals = "Disabled"
        }
      ]
    }
    then = { effect = "[parameters('effect')]" }
  })
}

resource "azurerm_resource_group_policy_assignment" "h3_pna_disabled" {
  name                 = "h3-pna-disabled"
  display_name         = "H3 — Foundry PNA disabled on hub"
  resource_group_id    = azurerm_resource_group.hub.id
  policy_definition_id = azurerm_policy_definition.h3_pna_disabled.id

  parameters = jsonencode({
    effect = { value = var.policy_effect_hub }
  })
}

# ---------------------------------------------------------------------------
# H4 — Foundry accounts must disable local (key) auth (custom)
# ---------------------------------------------------------------------------
resource "azurerm_policy_definition" "h4_disable_local_auth" {
  name         = "h4-foundry-disable-local-auth"
  policy_type  = "Custom"
  mode         = "All"
  display_name = "H4 — Foundry accounts must disable local authentication"
  description  = "Forces all Foundry API access to use Entra identity. Rejects account writes where properties.disableLocalAuth is not true."

  metadata = jsonencode({ category = "Foundry (BYOM sample 47)" })

  parameters = jsonencode({
    effect = {
      type          = "String"
      allowedValues = ["Deny", "Audit", "Disabled"]
      defaultValue  = "Deny"
      metadata      = { displayName = "Effect" }
    }
  })

  policy_rule = jsonencode({
    if = {
      allOf = [
        { field = "type", equals = "Microsoft.CognitiveServices/accounts" },
        {
          field     = "Microsoft.CognitiveServices/accounts/disableLocalAuth"
          notEquals = true
        }
      ]
    }
    then = { effect = "[parameters('effect')]" }
  })
}

resource "azurerm_resource_group_policy_assignment" "h4_disable_local_auth" {
  name                 = "h4-disable-local-auth"
  display_name         = "H4 — Disable local auth on hub Foundry"
  resource_group_id    = azurerm_resource_group.hub.id
  policy_definition_id = azurerm_policy_definition.h4_disable_local_auth.id

  parameters = jsonencode({
    effect = { value = var.policy_effect_hub }
  })
}
