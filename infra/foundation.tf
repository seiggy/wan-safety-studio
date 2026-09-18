resource "azurerm_resource_group" "studio" {
  name       = local.names.resource_group
  location   = var.location
  tags       = local.ownership_tags
  depends_on = [terraform_data.landing_zone]

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_user_assigned_identity" "workspace" {
  name                = local.names.workspace_identity
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  tags                = local.ownership_tags
}

resource "azurerm_user_assigned_identity" "compute" {
  name                = local.names.compute_identity
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  tags                = local.ownership_tags
}

# ARM-only storage management avoids shared-key lookups and private data-plane
# bootstrap access. No storage key, SAS or connection string enters this state.
resource "azapi_resource" "storage" {
  type      = "Microsoft.Storage/storageAccounts@2023-05-01"
  name      = local.names.storage
  parent_id = azurerm_resource_group.studio.id
  location  = var.location
  tags      = local.ownership_tags
  body = {
    kind = "StorageV2"
    sku  = { name = "Standard_LRS" }
    properties = {
      minimumTlsVersion            = "TLS1_2"
      supportsHttpsTrafficOnly     = true
      allowBlobPublicAccess        = false
      allowSharedKeyAccess         = false
      allowCrossTenantReplication  = false
      defaultToOAuthAuthentication = true
      publicNetworkAccess          = "Disabled"
      networkAcls = {
        defaultAction       = "Deny"
        bypass              = "AzureServices"
        ipRules             = []
        virtualNetworkRules = []
      }
    }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azapi_resource" "container" {
  type      = "Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01"
  name      = local.names.container
  parent_id = "${azapi_resource.storage.id}/blobServices/default"
  body = {
    properties = { publicAccess = "None" }
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_key_vault" "studio" {
  name                          = local.names.vault
  resource_group_name           = azurerm_resource_group.studio.name
  location                      = var.location
  tenant_id                     = var.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 90
  public_network_access_enabled = false
  tags                          = local.ownership_tags

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
  }

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_container_registry" "studio" {
  name                          = local.names.registry
  resource_group_name           = azurerm_resource_group.studio.name
  location                      = var.location
  sku                           = "Premium"
  admin_enabled                 = false
  anonymous_pull_enabled        = false
  public_network_access_enabled = false
  data_endpoint_enabled         = true
  network_rule_bypass_option    = "AzureServices"
  role_assignment_mode          = "LegacyRegistryPermissions"
  tags                          = local.ownership_tags

  network_rule_set {
    default_action = "Deny"
  }
}

resource "azurerm_application_insights" "studio" {
  name                = local.names.insights
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  application_type    = "web"
  workspace_id        = var.log_analytics_workspace_id
  tags                = local.ownership_tags
}

locals {
  role_ids = {
    blob_contributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe"
    secrets_officer  = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7"
    acr_pull         = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
    acr_push         = "8311e382-0749-4cb8-b61a-304f252e45ec"
    ml_scientist     = "f6c7c914-8db3-469d-8ca1-694a8f32e121"
  }
  service_grants = {
    workspace_storage = {
      scope = azapi_resource.storage.id, principal = azurerm_user_assigned_identity.workspace.principal_id, role = local.role_ids.blob_contributor
    }
    workspace_vault = {
      scope = azurerm_key_vault.studio.id, principal = azurerm_user_assigned_identity.workspace.principal_id, role = local.role_ids.secrets_officer
    }
    workspace_registry = {
      scope = azurerm_container_registry.studio.id, principal = azurerm_user_assigned_identity.workspace.principal_id, role = local.role_ids.acr_pull
    }
    compute_storage = {
      scope = azapi_resource.storage.id, principal = azurerm_user_assigned_identity.compute.principal_id, role = local.role_ids.blob_contributor
    }
    compute_registry = {
      scope = azurerm_container_registry.studio.id, principal = azurerm_user_assigned_identity.compute.principal_id, role = local.role_ids.acr_pull
    }
  }
  operator_grants = {
    storage   = { scope = azapi_resource.storage.id, role = local.role_ids.blob_contributor }
    registry  = { scope = azurerm_container_registry.studio.id, role = local.role_ids.acr_push }
    workspace = { scope = azapi_resource.workspace.id, role = local.role_ids.ml_scientist }
  }
}

resource "azurerm_role_assignment" "service" {
  for_each                         = local.service_grants
  name                             = uuidv5("url", lower("${each.value.scope}/${each.value.principal}/${each.value.role}"))
  scope                            = each.value.scope
  role_definition_id               = "/subscriptions/${lower(var.subscription_id)}/providers/Microsoft.Authorization/roleDefinitions/${each.value.role}"
  principal_id                     = each.value.principal
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "operator" {
  for_each           = local.operator_grants
  name               = uuidv5("url", lower("${each.value.scope}/${var.operator_principal_id}/${each.value.role}"))
  scope              = each.value.scope
  role_definition_id = "/subscriptions/${lower(var.subscription_id)}/providers/Microsoft.Authorization/roleDefinitions/${each.value.role}"
  principal_id       = var.operator_principal_id
}

# AzAPI owns the complete workspace so all privacy flags, including the legacy
# VNet flag, are explicit; there is no competing AzureRM owner or ignored body.
resource "azapi_resource" "workspace" {
  type      = "Microsoft.MachineLearningServices/workspaces@2025-09-01"
  name      = local.names.workspace
  parent_id = azurerm_resource_group.studio.id
  location  = var.location
  tags      = local.ownership_tags
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.workspace.id]
  }
  body = {
    kind = "Default"
    sku  = { name = "Basic", tier = "Basic" }
    properties = {
      friendlyName                    = "Private WAN Safety Studio"
      description                     = "Explicitly armed single Spot A100; existing customer landing zone only."
      publicNetworkAccess             = "Disabled"
      allowPublicAccessWhenBehindVnet = false
      systemDatastoresAuthMode        = "Identity"
      primaryUserAssignedIdentity     = azurerm_user_assigned_identity.workspace.id
      storageAccount                  = azapi_resource.storage.id
      keyVault                        = azurerm_key_vault.studio.id
      containerRegistry               = azurerm_container_registry.studio.id
      applicationInsights             = azurerm_application_insights.studio.id
      managedNetwork                  = { isolationMode = "Disabled" }
      provisionNetworkNow             = false
      v1LegacyMode                    = false
    }
  }
  depends_on = [azurerm_role_assignment.service, azurerm_private_endpoint.service]

  lifecycle {
    prevent_destroy = true
  }
}

# Explicit credential-less ARM fields include the owning subscription and RG;
# AzureRM's datastore resource does not expose those fields or HTTPS protocol.
resource "azapi_resource" "datastore" {
  type      = "Microsoft.MachineLearningServices/workspaces/datastores@2024-10-01"
  name      = local.names.datastore
  parent_id = azapi_resource.workspace.id
  body = {
    properties = {
      datastoreType                 = "AzureBlob"
      accountName                   = local.names.storage
      containerName                 = azapi_resource.container.name
      endpoint                      = "core.windows.net"
      protocol                      = "https"
      resourceGroup                 = local.names.resource_group
      subscriptionId                = lower(var.subscription_id)
      serviceDataAccessAuthIdentity = "WorkspaceUserAssignedIdentity"
      credentials                   = { credentialsType = "None" }
    }
  }
  depends_on = [azurerm_role_assignment.service, azurerm_private_endpoint.workspace]
}
