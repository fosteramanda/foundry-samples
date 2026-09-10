# AGW backend wiring for the agent.
#
# spoke-base created the AGW with a placeholder HTTP listener and a
# lifecycle.ignore_changes on backend/listener/rule (so this root can own
# them without state conflict).
#
# Azure Application Gateway does NOT expose child ARM resource types for
# backend pools / listeners / rules — everything lives inside the parent
# AGW's properties. So we PATCH the parent AGW via azapi_update_resource
# to insert the agent-specific configuration.
#
# In a production sample you would attach a TLS listener with a real cert.
# The demo listens on HTTP:80 with a host-header of the AGW's default
# *.cloudapp.azure.com FQDN so it works without a custom domain / cert.

locals {
  backend_pool_name       = "bp-agent-${var.agent_name}"
  backend_settings_name   = "bs-agent-${var.agent_name}"
  listener_name           = "lst-agent-${var.agent_name}"
  frontend_port_name_http = "fp-http-80"
  rule_name               = "rr-agent-${var.agent_name}"
  probe_name              = "probe-agent-${var.agent_name}"

  agw_frontend_ip_config_id = "${local.appgw_id}/frontendIPConfigurations/feip-public"
  agw_frontend_port_id      = "${local.appgw_id}/frontendPorts/${local.frontend_port_name_http}"
  agw_backend_pool_id       = "${local.appgw_id}/backendAddressPools/${local.backend_pool_name}"
  agw_backend_settings_id   = "${local.appgw_id}/backendHttpSettingsCollection/${local.backend_settings_name}"
  agw_http_listener_id      = "${local.appgw_id}/httpListeners/${local.listener_name}"
  agw_probe_id              = "${local.appgw_id}/probes/${local.probe_name}"
}

resource "azapi_update_resource" "agw_backend_wiring" {
  type        = "Microsoft.Network/applicationGateways@2024-05-01"
  resource_id = local.appgw_id

  body = {
    properties = {
      frontendPorts = [
        {
          name = local.frontend_port_name_http
          properties = {
            port = 80
          }
        }
      ]
      backendAddressPools = [
        {
          name = local.backend_pool_name
          properties = {
            backendAddresses = [
              {
                fqdn = local.agent_endpoint_backend_fqdn
              }
            ]
          }
        }
      ]
      probes = [
        {
          name = local.probe_name
          properties = {
            protocol                            = "Https"
            host                                = local.agent_endpoint_backend_fqdn
            path                                = "/"
            interval                            = 30
            timeout                             = 30
            unhealthyThreshold                  = 3
            pickHostNameFromBackendHttpSettings = false
            match = {
              statusCodes = ["200-499"]
            }
          }
        }
      ]
      backendHttpSettingsCollection = [
        {
          name = local.backend_settings_name
          properties = {
            port                           = 443
            protocol                       = "Https"
            cookieBasedAffinity            = "Disabled"
            requestTimeout                 = 60
            pickHostNameFromBackendAddress = true
            probe                          = { id = local.agw_probe_id }
          }
        }
      ]
      httpListeners = [
        {
          name = local.listener_name
          properties = {
            protocol                    = "Http"
            frontendIPConfiguration     = { id = local.agw_frontend_ip_config_id }
            frontendPort                = { id = local.agw_frontend_port_id }
            hostName                    = local.listener_hostname
            requireServerNameIndication = false
          }
        }
      ]
      requestRoutingRules = [
        {
          name = local.rule_name
          properties = {
            priority            = 100
            ruleType            = "Basic"
            httpListener        = { id = local.agw_http_listener_id }
            backendAddressPool  = { id = local.agw_backend_pool_id }
            backendHttpSettings = { id = local.agw_backend_settings_id }
          }
        }
      ]
    }
  }
}
