# Configure the AzApi, AzureRM, Random, and Time providers.
# Two subscriptions are used through provider aliases (see providers.tf);
# the required providers are declared once here.
terraform {
  required_providers {
    azapi = {
      source  = "azure/azapi"
      version = "~> 2.5"
    }
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.37"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.13"
    }
  }
  required_version = ">= 1.10.0, < 2.0.0"

  # Uncomment to store state in Azure Storage. Remote state is strongly
  # recommended for this sample because provider API keys enter state
  # during apply.
  # backend "azurerm" {}
}
