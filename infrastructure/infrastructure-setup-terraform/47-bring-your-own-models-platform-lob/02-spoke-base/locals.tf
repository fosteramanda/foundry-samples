data "terraform_remote_state" "hub_base" {
  backend = "local"
  config = {
    path = var.hub_base_state_path
  }
}

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length      = 5
  min_numeric = 2
  numeric     = true
  special     = false
  lower       = true
  upper       = false
}

locals {
  random_suffix = random_string.suffix.result

  spoke_rg_name   = "rg-${var.name_prefix}-${var.lob_name}-base-${local.random_suffix}"
  spoke_vnet_name = "vnet-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"
  appgw_name      = "agw-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"
  appgw_pip_name  = "pip-agw-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"
  waf_policy_name = "wafp-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"
  law_name        = "law-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"

  nsg_appgw_name    = "nsg-appgw-${var.lob_name}-${local.random_suffix}"
  nsg_workload_name = "nsg-workload-${var.lob_name}-${local.random_suffix}"
  nsg_pe_name       = "nsg-pe-${var.lob_name}-${local.random_suffix}"
  spoke_rt_name     = "rt-${var.name_prefix}-${var.lob_name}-${local.random_suffix}"

  # hub-base publishes the Firewall's private IP. Route tables are
  # subscription-local, so we build our own RT in the spoke that points
  # its default route at this next-hop.
  hub_firewall_private_ip = data.terraform_remote_state.hub_base.outputs.hub_firewall_private_ip

  common_tags = {
    scenario          = "byom-platform-lob"
    sample            = "47-bring-your-own-models-platform-lob"
    plane             = "spoke"
    root              = "spoke-base"
    lob               = var.lob_name
    subscription_role = "lob"
  }
}
