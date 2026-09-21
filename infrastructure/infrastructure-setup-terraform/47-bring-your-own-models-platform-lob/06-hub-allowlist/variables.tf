variable "platform_subscription_id" {
  description = "Same as hub-workload's platform_subscription_id."
  type        = string
}

variable "tenant_id" {
  description = "Same as hub-workload's tenant_id."
  type        = string
}

variable "hub_workload_state_path" {
  description = "Path to hub-workload's terraform.tfstate. hub-allowlist reads APIM name, RG, API name, tenant, audience, quota, and policy render inputs — nothing else."
  type        = string
  default     = "../04-hub-workload/terraform.tfstate"
}

# ============================================================================
# The allowlist — the only thing this root is really about
# ============================================================================

variable "project_oid_allowlist" {
  description = <<-EOT
    OIDs of spoke project managed identities that are allowed to call the
    gateway. This is the field the step-4 PR edits (see
    allowlist.auto.tfvars). Empty list means the gateway rejects all callers
    that carry an oid claim (which every workload MI does), effectively
    keeping the gateway closed to spokes.
  EOT
  type        = list(string)
  default     = []
}
