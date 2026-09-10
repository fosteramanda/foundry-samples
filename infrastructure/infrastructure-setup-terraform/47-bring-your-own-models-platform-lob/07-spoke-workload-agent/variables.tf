variable "spoke_subscription_id" {
  description = "Same as spoke-base's spoke_subscription_id."
  type        = string
}

variable "tenant_id" {
  description = "Entra tenant."
  type        = string
}

variable "spoke_base_state_path" {
  description = "Path to spoke-base's terraform.tfstate (for AGW id)."
  type        = string
  default     = "../02-spoke-base/terraform.tfstate"
}

variable "spoke_project_state_path" {
  description = "Path to spoke-workload/project's terraform.tfstate."
  type        = string
  default     = "../05-spoke-workload-project/terraform.tfstate"
}

variable "agent_name" {
  description = <<-EOT
    Logical agent name used to derive Application Gateway child names
    (backend pool, listener, rule, probe). The actual agent resource is
    NOT created by Terraform — see demo.ipynb for the data-plane call
    that creates the agent on the Foundry project. This value must match
    the agent name used at runtime so the AGW listener host header lines
    up if you later bind it to a custom domain.
  EOT
  type        = string
  default     = "hr-assistant"
}

variable "agent_endpoint_hostname" {
  description = <<-EOT
    Public hostname the AGW listener answers on. In production this is a
    CNAME to the AGW public FQDN with a TLS cert bound to the listener.
    For the demo we accept HTTP on port 80 with a host header of the AGW's
    default *.cloudapp.azure.com FQDN — no cert required.
  EOT
  type        = string
  default     = ""
}
