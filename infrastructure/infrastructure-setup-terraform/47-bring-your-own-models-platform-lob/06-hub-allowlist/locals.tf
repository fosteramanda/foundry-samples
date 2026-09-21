data "terraform_remote_state" "hub_workload" {
  backend = "local"
  config = {
    path = var.hub_workload_state_path
  }
}

locals {
  apim_name            = data.terraform_remote_state.hub_workload.outputs.apim_name
  apim_rg              = data.terraform_remote_state.hub_workload.outputs.apim_resource_group_name
  apim_api_name        = data.terraform_remote_state.hub_workload.outputs.apim_inference_api_name
  token_audience       = data.terraform_remote_state.hub_workload.outputs.token_audience
  tenant_id            = data.terraform_remote_state.hub_workload.outputs.tenant_id
  quota_tokens_per_min = data.terraform_remote_state.hub_workload.outputs.quota_tokens_per_minute
  render               = data.terraform_remote_state.hub_workload.outputs.policy_render_inputs
}
