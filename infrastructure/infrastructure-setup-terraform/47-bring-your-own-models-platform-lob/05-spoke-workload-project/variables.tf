variable "spoke_subscription_id" {
  description = "Same as spoke-base's spoke_subscription_id."
  type        = string
}

variable "tenant_id" {
  description = "Entra tenant. Must match hub-workload's tenant_id."
  type        = string
}

variable "name_prefix" {
  description = "Same as spoke-base's name_prefix."
  type        = string
  default     = "contoso-byom-lob-hr"
}

variable "spoke_base_state_path" {
  description = "Path to spoke-base's terraform.tfstate."
  type        = string
  default     = "../02-spoke-base/terraform.tfstate"
}

variable "hub_base_state_path" {
  description = "Path to hub-base's terraform.tfstate. Needed to look up the hub-owned Foundry private DNS zone IDs so the Foundry PE can register its A records cross-subscription."
  type        = string
  default     = "../01-hub-base/terraform.tfstate"
}

variable "contract_path" {
  description = "Path to the platform contract file published by hub-workload. Read once at plan time; never modified."
  type        = string
  default     = "../contracts/hub-workload.contract.json"
}

variable "project_name" {
  description = "Foundry project name (lower-case, no spaces). One per LoB / environment."
  type        = string
  default     = "hrbot"
}

variable "project_display_name" {
  description = "Foundry project display name shown in the portal."
  type        = string
  default     = "HR Bot"
}

variable "project_description" {
  description = "Foundry project description."
  type        = string
  default     = "LoB HR agent project — consumes the platform gateway; no local model deployments."
}
