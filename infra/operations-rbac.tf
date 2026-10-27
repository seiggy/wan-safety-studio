locals {
  # Application StudioAdmin checks remain mandatory: Azure grants authorize the host identity, not a browser session.
  operators = merge({ operator = var.operator_principal_id },
  local.portal_enabled ? { host = azurerm_user_assigned_identity.portal[0].principal_id } : {})
  operator_permissions = merge(
    {
      eligibility = {
        scope   = "/subscriptions/${var.subscription_id}"
        actions = ["Microsoft.MachineLearningServices/locations/usages/read", "Microsoft.Compute/skus/read"]
      }
    },
    merge([for region, foundation in module.region : {
      "${region}:workspace" = {
        scope = foundation.resources.workspaceId
        actions = [
          "Microsoft.MachineLearningServices/workspaces/read",
          "Microsoft.MachineLearningServices/workspaces/computes/read",
          "Microsoft.MachineLearningServices/workspaces/computes/write",
          "Microsoft.MachineLearningServices/workspaces/computes/delete",
          "Microsoft.MachineLearningServices/workspaces/jobs/read",
          "Microsoft.MachineLearningServices/workspaces/jobs/cancel/action",
        ]
      }
      "${region}:identity" = {
        scope = foundation.resources.computeIdentityId
        actions = [
          "Microsoft.ManagedIdentity/userAssignedIdentities/read",
          "Microsoft.ManagedIdentity/userAssignedIdentities/assign/action",
        ]
      }
      "${region}:vnet" = {
        scope   = join("/", slice(split("/", var.regions[region].gpu_subnet_id), 0, 9))
        actions = ["Microsoft.Network/virtualNetworks/read", "Microsoft.Network/virtualNetworks/join/action"]
      }
      "${region}:subnet" = {
        scope = var.regions[region].gpu_subnet_id
        actions = concat(["Microsoft.Network/virtualNetworks/subnets/read", "Microsoft.Network/virtualNetworks/subnets/join/action"],
        var.regions[region].manage_compute_egress ? ["Microsoft.Network/virtualNetworks/subnets/write"] : [])
      }
    }]...),
    { for region, foundation in module.region : "${region}:egress" => {
      scope = foundation.inventory.resourceGroupId
      actions = [
        "Microsoft.Network/natGateways/read", "Microsoft.Network/natGateways/write", "Microsoft.Network/natGateways/delete", "Microsoft.Network/natGateways/join/action",
        "Microsoft.Network/publicIPAddresses/read", "Microsoft.Network/publicIPAddresses/write", "Microsoft.Network/publicIPAddresses/delete", "Microsoft.Network/publicIPAddresses/join/action",
      ]
    } if var.regions[region].manage_compute_egress }
  )
  operator_assignments = merge([for key, permission in local.operator_permissions : {
    for name, principal in local.operators : "${key}:${name}" => { key = key, principal = principal, scope = permission.scope }
  }]...)
  control_assignments = { for name, principal in local.operators : name => principal }
}

resource "azurerm_role_definition" "studio_operator" {
  for_each           = local.operator_permissions
  name               = "wan-operator-${var.deployment_name}-${substr(sha256("${var.subscription_id}:${each.key}"), 0, 12)}"
  role_definition_id = uuidv5("url", lower("${each.value.scope}/${var.deployment_name}/operator/${each.key}"))
  scope              = each.value.scope
  assignable_scopes  = [each.value.scope]
  description        = "Explicit WAN lifecycle scope; no workspace, DNS, logging, customer NSG, release-asset or role-assignment mutation."
  permissions {
    actions = each.value.actions
  }
  depends_on = [module.region]
}

resource "azurerm_role_assignment" "studio_operator" {
  for_each                         = local.operator_assignments
  name                             = uuidv5("url", lower("${each.value.scope}/${each.value.principal}/${azurerm_role_definition.studio_operator[each.value.key].role_definition_id}"))
  scope                            = each.value.scope
  role_definition_id               = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${azurerm_role_definition.studio_operator[each.value.key].role_definition_id}"
  principal_id                     = each.value.principal
  principal_type                   = endswith(each.key, ":host") ? "ServicePrincipal" : null
  skip_service_principal_aad_check = endswith(each.key, ":host")
}

resource "azurerm_role_assignment" "control" {
  for_each                         = local.control_assignments
  name                             = uuidv5("url", lower("${local.ids.storage}/blobServices/default/containers/wan-control/${each.value}/${local.role_ids.blob_contributor}"))
  scope                            = "${local.ids.storage}/blobServices/default/containers/wan-control"
  role_definition_id               = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${local.role_ids.blob_contributor}"
  principal_id                     = each.value
  principal_type                   = each.key == "host" ? "ServicePrincipal" : null
  skip_service_principal_aad_check = each.key == "host"
  depends_on                       = [module.region]
}

resource "azurerm_role_definition" "regional_job_submitter" {
  for_each           = local.portal_enabled ? { for region, foundation in module.region : region => foundation if region != var.control_region } : {}
  name               = "wan-submitter-${each.value.inventory.names.workspace}"
  role_definition_id = uuidv5("url", lower("${each.value.resources.workspaceId}/wan-portal-job-submitter"))
  scope              = each.value.resources.workspaceId
  assignable_scopes  = [each.value.resources.workspaceId]
  permissions {
    actions = [
      "Microsoft.MachineLearningServices/workspaces/read",
      "Microsoft.MachineLearningServices/workspaces/computes/read",
      "Microsoft.MachineLearningServices/workspaces/datastores/read",
      "Microsoft.MachineLearningServices/workspaces/environments/read",
      "Microsoft.MachineLearningServices/workspaces/environments/versions/read",
      "Microsoft.MachineLearningServices/workspaces/models/read",
      "Microsoft.MachineLearningServices/workspaces/models/versions/read",
      "Microsoft.MachineLearningServices/workspaces/jobs/read",
      "Microsoft.MachineLearningServices/workspaces/jobs/write",
      "Microsoft.MachineLearningServices/workspaces/jobs/cancel/action",
    ]
  }
  depends_on = [module.region]
}

locals {
  regional_portal_grants = local.portal_enabled ? merge(
    { for region, foundation in module.region : "${region}:release_read" => {
      scope = "${foundation.resources.storageAccountId}/blobServices/default/containers/wan-release", role = local.role_ids.blob_reader
    } },
    merge([for region, foundation in module.region : {
      "${region}:storage_studio" = { scope = "${foundation.resources.storageAccountId}/blobServices/default/containers/wan-studio", role = local.role_ids.blob_contributor }
      "${region}:registry"       = { scope = foundation.resources.registryId, role = local.role_ids.acr_pull }
      "${region}:workspace"      = { scope = foundation.resources.workspaceId, role = azurerm_role_definition.regional_job_submitter[region].role_definition_id }
    } if region != var.control_region]...)
  ) : {}
}

resource "azurerm_role_assignment" "regional_portal" {
  for_each                         = local.regional_portal_grants
  name                             = uuidv5("url", lower("${each.value.scope}/${azurerm_user_assigned_identity.portal[0].principal_id}/${each.value.role}"))
  scope                            = each.value.scope
  role_definition_id               = "/subscriptions/${var.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${each.value.role}"
  principal_id                     = azurerm_user_assigned_identity.portal[0].principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}
