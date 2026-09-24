# These switches enable CLI auth and disable MSI/OIDC/AKS fallbacks, but do not
# make either provider exclusively CLI-only: both pinned providers still accept
# ambient ARM_CLIENT_* secret/certificate credentials, including credential files.
# Direct Terraform must use a sanitized child environment; do not set empty secrets.
#
# Customer prerequisites (automatic registration remains disabled):
# Microsoft.Network, Microsoft.ManagedIdentity, Microsoft.Storage, Microsoft.KeyVault,
# Microsoft.ContainerRegistry, Microsoft.Insights, Microsoft.MachineLearningServices;
# Microsoft.OperationalInsights for the supplied LAW; Microsoft.Web only when portal is set. ARM also uses its built-in
# Microsoft.Resources and Microsoft.Authorization namespaces.
provider "azurerm" {
  environment                     = "public"
  subscription_id                 = var.subscription_id
  tenant_id                       = var.tenant_id
  resource_provider_registrations = "none"
  storage_use_azuread             = true
  use_cli                         = true
  use_msi                         = false
  use_oidc                        = false
  use_aks_workload_identity       = false

  features {
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
    machine_learning {
      purge_soft_deleted_workspace_on_destroy = false
    }
  }
}

provider "azapi" {
  environment                = "public"
  subscription_id            = var.subscription_id
  tenant_id                  = var.tenant_id
  skip_provider_registration = true
  enable_preflight           = false
  disable_default_output     = true
  use_cli                    = true
  use_msi                    = false
  use_oidc                   = false
  use_aks_workload_identity  = false
}
