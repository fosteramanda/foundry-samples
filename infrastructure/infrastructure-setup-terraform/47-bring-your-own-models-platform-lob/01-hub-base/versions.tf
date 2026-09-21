# hub-base — providers pinned to the same major versions used by sample 46
# so upgrade decisions apply to the whole platform/LOB story at once.
terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.37"
    }
    azapi = {
      source  = "azure/azapi"
      version = "~> 2.5"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
  }
  required_version = ">= 1.10.0, < 2.0.0"

  # Remote state strongly recommended: hub-workload and network-registration
  # consume outputs from this root via terraform_remote_state or a published
  # contract file. See ../README.md § "Planned repository layout".
  # backend "azurerm" {}
}
