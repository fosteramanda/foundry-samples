variable "spoke_subscription_id" {
  description = "Same as spoke-base's spoke_subscription_id."
  type        = string
}

variable "tenant_id" {
  description = "Entra tenant."
  type        = string
}

variable "spoke_base_state_path" {
  description = "Path to spoke-base's terraform.tfstate — provides the spoke VNet id + RG."
  type        = string
  default     = "../02-spoke-base/terraform.tfstate"
}

variable "spoke_project_state_path" {
  description = "Path to spoke-workload/project's terraform.tfstate — the project id we grant the human on."
  type        = string
  default     = "../05-spoke-workload-project/terraform.tfstate"
}

variable "vm_admin_username" {
  description = "Linux VM local admin username."
  type        = string
  default     = "azureuser"
}

variable "vm_size" {
  description = <<-EOT
    Azure VM size. Standard_D2s_v3 is the safe default in Sweden Central
    (standardDSv3Family typically has default quota). Alternatives with
    default headroom: Standard_B2ms, Standard_D2s_v6.
  EOT
  type        = string
  default     = "Standard_D2s_v3"
}

variable "bastion_subnet_prefix" {
  description = "CIDR for AzureBastionSubnet. Must be at least /26 and named exactly 'AzureBastionSubnet'."
  type        = string
  default     = "10.20.3.0/26"
}

variable "jumpbox_subnet_prefix" {
  description = "CIDR for the small jumpbox subnet."
  type        = string
  default     = "10.20.4.0/28"
}

variable "human_object_id" {
  description = <<-EOT
    Entra object id (OID) of the human who will run the demo notebook. Gets
    granted 'Azure AI User' on the spoke Foundry project so the notebook's
    `az login` can author agents. Look it up with:
      az ad signed-in-user show --query id -o tsv
  EOT
  type        = string
}

variable "ssh_public_key_path" {
  description = <<-EOT
    Absolute path where the generated PUBLIC key is written locally so you
    can inspect it. Default is the module dir.
  EOT
  type        = string
  default     = ""
}

variable "ssh_private_key_path" {
  description = <<-EOT
    Absolute path where the generated PRIVATE key is written locally. If
    left empty, defaults to the module dir + jumpbox_id_rsa. This file
    holds a plaintext PEM — treat it accordingly.
  EOT
  type        = string
  default     = ""
}
