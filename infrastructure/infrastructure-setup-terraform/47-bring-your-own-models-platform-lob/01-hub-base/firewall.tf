# Azure Firewall Standard with a policy that allows outbound HTTPS only to
# the FQDNs listed in var.provider_fqdn_allowlist. Any other egress from
# the hub or spoke — forced through this Firewall by the spoke route table
# published by network.tf — is denied by default.

resource "azurerm_public_ip" "firewall" {
  name                = local.firewall_pip_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.common_tags
}

resource "azurerm_firewall_policy" "hub" {
  name                = local.firewall_policy
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  sku                 = "Standard"
  tags                = local.common_tags
}

# Application rule collection: HTTPS-only, FQDN allowlist.
resource "azurerm_firewall_policy_rule_collection_group" "providers" {
  name               = "external-model-providers"
  firewall_policy_id = azurerm_firewall_policy.hub.id
  priority           = 200

  application_rule_collection {
    name     = "allow-model-provider-fqdns"
    priority = 100
    action   = "Allow"

    rule {
      name              = "https-to-approved-providers"
      source_addresses  = ["*"]
      destination_fqdns = var.provider_fqdn_allowlist
      protocols {
        type = "Https"
        port = 443
      }
    }
  }
}

resource "azurerm_firewall" "hub" {
  name                = local.firewall_name
  location            = var.location
  resource_group_name = azurerm_resource_group.hub.name
  sku_name            = "AZFW_VNet"
  sku_tier            = "Standard"
  firewall_policy_id  = azurerm_firewall_policy.hub.id
  tags                = local.common_tags

  ip_configuration {
    name                 = "afw-ipcfg"
    subnet_id            = azurerm_subnet.firewall.id
    public_ip_address_id = azurerm_public_ip.firewall.id
  }
}
