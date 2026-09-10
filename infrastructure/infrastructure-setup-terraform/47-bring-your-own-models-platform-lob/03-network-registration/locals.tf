data "terraform_remote_state" "hub_base" {
  backend = "local"
  config = {
    path = var.hub_base_state_path
  }
}

data "terraform_remote_state" "spoke_base" {
  backend = "local"
  config = {
    path = var.spoke_base_state_path
  }
}

locals {
  hub_vnet_id   = data.terraform_remote_state.hub_base.outputs.hub_vnet_id
  hub_vnet_name = data.terraform_remote_state.hub_base.outputs.hub_vnet_name
  hub_rg_name   = data.terraform_remote_state.hub_base.outputs.hub_resource_group_name
  hub_dns_zones = data.terraform_remote_state.hub_base.outputs.private_dns_zone_ids
  hub_dns_names = data.terraform_remote_state.hub_base.outputs.private_dns_zone_names

  spoke_vnet_id   = data.terraform_remote_state.spoke_base.outputs.spoke_vnet_id
  spoke_vnet_name = data.terraform_remote_state.spoke_base.outputs.spoke_vnet_name
  spoke_rg_name   = data.terraform_remote_state.spoke_base.outputs.spoke_resource_group_name

  common_tags = {
    scenario = "byom-platform-lob"
    sample   = "47-bring-your-own-models-platform-lob"
    plane    = "cross"
    root     = "network-registration"
  }
}
