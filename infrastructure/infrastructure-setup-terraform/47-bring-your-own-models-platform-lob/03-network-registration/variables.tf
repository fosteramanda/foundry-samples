variable "platform_subscription_id" {
  description = "Hub (Platform) subscription id."
  type        = string
}

variable "spoke_subscription_id" {
  description = "Spoke (LoB) subscription id."
  type        = string
}

variable "hub_base_state_path" {
  description = "Path to hub-base terraform.tfstate."
  type        = string
  default     = "../01-hub-base/terraform.tfstate"
}

variable "spoke_base_state_path" {
  description = "Path to spoke-base terraform.tfstate."
  type        = string
  default     = "../02-spoke-base/terraform.tfstate"
}

variable "peering_name_prefix" {
  description = "Prefix for peering names."
  type        = string
  default     = "peer"
}
