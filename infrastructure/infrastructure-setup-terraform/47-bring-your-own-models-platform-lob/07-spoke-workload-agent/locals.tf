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

locals {
  spoke_rg_name     = data.terraform_remote_state.spoke_base.outputs.spoke_resource_group_name
  location          = data.terraform_remote_state.spoke_base.outputs.spoke_location
  appgw_id          = data.terraform_remote_state.spoke_base.outputs.app_gateway_id
  appgw_name        = data.terraform_remote_state.spoke_base.outputs.app_gateway_name
  appgw_public_fqdn = data.terraform_remote_state.spoke_base.outputs.app_gateway_public_fqdn

  project_id                 = data.terraform_remote_state.spoke_project.outputs.project_id
  foundry_agent_account_name = data.terraform_remote_state.spoke_project.outputs.foundry_agent_account_name

  # Foundry data-plane hostname the agent runtime exposes.
  # https://<account>.services.ai.azure.com/api/projects/<project>
  agent_endpoint_backend_fqdn = "${local.foundry_agent_account_name}.services.ai.azure.com"

  # Listener host header. Defaults to the AGW's public *.cloudapp.azure.com
  # FQDN so the demo works without a custom domain / TLS cert.
  listener_hostname = var.agent_endpoint_hostname != "" ? var.agent_endpoint_hostname : local.appgw_public_fqdn
}
