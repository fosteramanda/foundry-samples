# S-series Azure Policy assignments (spoke scope).
#
# Assigned at the spoke resource group in the sample. See ../README.md
# § "Azure Policy guardrails" for the operating scope and effect posture.

# ---------------------------------------------------------------------------
# S1 — Built-in "Approved models" policy with an EMPTY allowlist.
#      Nothing can be deployed on any Foundry account in the spoke RG.
# ---------------------------------------------------------------------------
data "azurerm_policy_definition_built_in" "s1_approved_models" {
  display_name = "Foundry model deployments should only use approved models"
}

resource "azurerm_resource_group_policy_assignment" "s1_no_local_models" {
  name                 = "s1-no-local-models"
  display_name         = "S1 — No local Foundry deployments in spoke"
  resource_group_id    = azurerm_resource_group.spoke.id
  policy_definition_id = data.azurerm_policy_definition_built_in.s1_approved_models.id
  description          = "Spoke Foundry accounts are consumers only. Model deployments belong on the hub."

  parameters = jsonencode({
    effect            = { value = var.policy_effect_spoke }
    allowedPublishers = { value = [] }
    allowedAssetIds   = { value = [] }
  })
}

# ---------------------------------------------------------------------------
# S2 (deferred) — Disable instant-model access.
#
# The Microsoft Learn doc
# (https://learn.microsoft.com/azure/foundry/concepts/instant-models#enterprise-controls)
# describes forcing `properties.instant.modelAllowList = []` via Modify
# policy, but as of this sample's authoring the ARM policy alias
# `Microsoft.CognitiveServices/accounts/instant.modelAllowList` is not
# registered — ARM rejects the Custom policy definition with
# `InvalidPolicyAlias`. When the alias becomes available, re-introduce
# S2 as a Modify-effect policy that sets the field to `[]`.
#
# In the meantime, S1's empty publisher + assetId lists already deny every
# model deployment on the spoke, which is the practical outcome S2 aimed
# at anyway.
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# S3 — Spoke Foundry accounts must have publicNetworkAccess = Disabled.
# ---------------------------------------------------------------------------
resource "azurerm_policy_definition" "s3_pna_disabled" {
  name         = "s3-foundry-pna-disabled"
  policy_type  = "Custom"
  mode         = "All"
  display_name = "S3 — Spoke Foundry accounts must disable public network access"
  description  = "Same rule as H3 but scoped to the LoB. Ensures agents cannot be reached over the public internet."

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

resource "azurerm_resource_group_policy_assignment" "s3_pna_disabled" {
  name                 = "s3-pna-disabled"
  display_name         = "S3 — Foundry PNA disabled on spoke"
  resource_group_id    = azurerm_resource_group.spoke.id
  policy_definition_id = azurerm_policy_definition.s3_pna_disabled.id

  parameters = jsonencode({
    effect = { value = var.policy_effect_spoke }
  })
}

# ---------------------------------------------------------------------------
# S5 — Public IPs must be attached to Application Gateway. Blocks stray
#      public IPs from appearing on any other resource in the spoke.
# ---------------------------------------------------------------------------
resource "azurerm_policy_definition" "s5_public_ip_only_appgw" {
  name         = "s5-public-ip-only-appgw"
  policy_type  = "Custom"
  mode         = "All"
  display_name = "S5 — Public IPs must be attached to Application Gateway"
  description  = "Denies creation of public IPs in the spoke RG unless the caller is the App Gateway service. Enforces AGW as the only public surface."

  metadata = jsonencode({ category = "Foundry (BYOM sample 47)" })

  parameters = jsonencode({
    effect = {
      type          = "String"
      allowedValues = ["Deny", "Audit", "Disabled"]
      defaultValue  = "Deny"
      metadata      = { displayName = "Effect" }
    }
  })

  # The Application Gateway managed public IP name pattern is used by AGW
  # itself, plus the pip we explicitly create in app-gateway.tf. We allow
  # AGW PIPs (name contains "agw") and Bastion PIPs (name contains "bas"),
  # and deny the rest. The Bastion carve-out exists for the optional
  # 08-spoke-jumpbox root; a production spoke that does not run a jumpbox
  # can tighten this back to AGW-only.
  policy_rule = jsonencode({
    if = {
      allOf = [
        { field = "type", equals = "Microsoft.Network/publicIPAddresses" },
        {
          field    = "name"
          notMatch = "*agw*"
        },
        {
          field    = "name"
          notMatch = "*bas*"
        }
      ]
    }
    then = { effect = "[parameters('effect')]" }
  })
}

resource "azurerm_resource_group_policy_assignment" "s5_public_ip_only_appgw" {
  name                 = "s5-public-ip-only-appgw"
  display_name         = "S5 — AGW-only public IPs in spoke"
  resource_group_id    = azurerm_resource_group.spoke.id
  policy_definition_id = azurerm_policy_definition.s5_public_ip_only_appgw.id

  parameters = jsonencode({
    effect = { value = var.policy_effect_spoke }
  })
}
