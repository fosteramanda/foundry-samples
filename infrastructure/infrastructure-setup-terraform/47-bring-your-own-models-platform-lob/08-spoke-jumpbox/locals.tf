data "terraform_remote_state" "spoke_base" {
  backend = "local"
  config = {
    path = var.spoke_base_state_path
  }
}

data "terraform_remote_state" "spoke_project" {
  backend = "local"
  config = {
    path = var.spoke_project_state_path
  }
}

resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
  numeric = true
}

locals {
  random_suffix   = random_string.suffix.result
  spoke_rg_name   = data.terraform_remote_state.spoke_base.outputs.spoke_resource_group_name
  spoke_vnet_name = data.terraform_remote_state.spoke_base.outputs.spoke_vnet_name
  spoke_vnet_id   = data.terraform_remote_state.spoke_base.outputs.spoke_vnet_id
  spoke_location  = data.terraform_remote_state.spoke_base.outputs.spoke_location

  project_id = data.terraform_remote_state.spoke_project.outputs.project_id

  vm_name       = "vm-jumpbox-${local.random_suffix}"
  bastion_name  = "bas-spoke-${local.random_suffix}"
  nic_name      = "nic-jumpbox-${local.random_suffix}"
  bastion_pip   = "pip-bas-spoke-${local.random_suffix}"
  ssh_pub_path  = var.ssh_public_key_path != "" ? var.ssh_public_key_path : "${path.module}/jumpbox_id_rsa.pub"
  ssh_priv_path = var.ssh_private_key_path != "" ? var.ssh_private_key_path : "${path.module}/jumpbox_id_rsa"

  common_tags = {
    sample    = "47-byom-platform-lob"
    root      = "08-spoke-jumpbox"
    workload  = "runtime-demo"
    plane     = "spoke"
    ephemeral = "true"
  }
}
