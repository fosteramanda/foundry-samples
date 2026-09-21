# Application Gateway WAF v2 — the ONLY public surface in the whole spoke.
# The public IP has no other legal owner in this subscription (enforced by
# S5). WAF policy is attached in Prevention mode.

resource "azurerm_public_ip" "appgw" {
  name                = local.appgw_pip_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  allocation_method   = "Static"
  sku                 = "Standard"
  zones               = ["1", "2", "3"]
  tags                = local.common_tags
}

resource "azurerm_web_application_firewall_policy" "appgw" {
  name                = local.waf_policy_name
  resource_group_name = azurerm_resource_group.spoke.name
  location            = var.location
  tags                = local.common_tags

  policy_settings {
    enabled                     = true
    mode                        = "Prevention"
    request_body_check          = true
    file_upload_limit_in_mb     = 100
    max_request_body_size_in_kb = 128
  }

  managed_rules {
    managed_rule_set {
      type    = "OWASP"
      version = "3.2"
    }
  }
}

# Placeholder backend pool. spoke-workload/agent (step 7) wires the real
# backend to the Foundry agent account's private endpoint. Keeping the pool
# empty here means step 7 can iterate without touching spoke-base.
resource "azurerm_application_gateway" "spoke" {
  name                = local.appgw_name
  location            = var.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.common_tags
  zones               = ["1", "2", "3"]

  sku {
    name = var.appgw_sku
    tier = var.appgw_sku
  }

  autoscale_configuration {
    min_capacity = var.appgw_capacity
    max_capacity = var.appgw_capacity + 2
  }

  firewall_policy_id = azurerm_web_application_firewall_policy.appgw.id

  gateway_ip_configuration {
    name      = "gwipcfg"
    subnet_id = azurerm_subnet.appgw.id
  }

  frontend_port {
    name = "port-https"
    port = 443
  }

  frontend_ip_configuration {
    name                 = "feip-public"
    public_ip_address_id = azurerm_public_ip.appgw.id
  }

  # ----- Placeholder listener → backend. spoke-workload/agent replaces the
  # backend address pool with the Foundry agent PE FQDN. -----
  backend_address_pool {
    name = "bap-placeholder"
  }

  backend_http_settings {
    name                  = "bhs-https"
    cookie_based_affinity = "Disabled"
    port                  = 443
    protocol              = "Https"
    request_timeout       = 60
  }

  # HTTPS listener requires a certificate. In the sample we ship a
  # self-signed placeholder that spoke-workload/agent replaces with a real
  # cert (Key Vault reference or uploaded PFX). The listener here uses HTTP
  # on port 80 so the placeholder AGW can come up without a certificate.
  frontend_port {
    name = "port-http"
    port = 80
  }

  http_listener {
    name                           = "listener-placeholder"
    frontend_ip_configuration_name = "feip-public"
    frontend_port_name             = "port-http"
    protocol                       = "Http"
  }

  request_routing_rule {
    name                       = "rr-placeholder"
    rule_type                  = "Basic"
    priority                   = 100
    http_listener_name         = "listener-placeholder"
    backend_address_pool_name  = "bap-placeholder"
    backend_http_settings_name = "bhs-https"
  }

  lifecycle {
    ignore_changes = [
      # spoke-workload/agent owns the backend + listener that serves the
      # agent endpoint. spoke-base owns only the shell.
      backend_address_pool,
      backend_http_settings,
      http_listener,
      frontend_port,
      ssl_certificate,
      request_routing_rule,
      probe,
    ]
  }
}
