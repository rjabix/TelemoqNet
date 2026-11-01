terraform {
  required_version = ">= 1.9"

  required_providers {
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.0"
    }
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Remote state: copy backend.hcl.example to backend.hcl, then
  #   terraform init -backend-config=backend.hcl
  # backend "azurerm" {}
}

provider "azurerm" {
  features {
    storage {
      # The storage account has no public network access, so the provider must
      # not call the data plane while managing it.
      data_plane_available = false
    }
  }

  subscription_id     = var.subscription_id
  storage_use_azuread = true
}

provider "azapi" {
  subscription_id = var.subscription_id
}
