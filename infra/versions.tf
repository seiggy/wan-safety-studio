# Verified against the installed provider schemas and the matching release docs:
# https://github.com/hashicorp/terraform-provider-azurerm/tree/v5.6.0/website/docs
# https://github.com/Azure/terraform-provider-azapi/tree/v2.12.0/docs
# API request shapes: learn.microsoft.com/azure/templates/microsoft.machinelearningservices
# (workspaces 2025-09-01; computes/datastores 2024-10-01).
terraform {
  required_version = ">= 1.9.0, < 2.0.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "= 5.6.0"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "= 2.12.0"
    }
  }
}
