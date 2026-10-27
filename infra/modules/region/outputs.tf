output "resources" {
  description = "Informational resource IDs, names and effective ceilings for an operator reading terraform output. Not a machine contract."
  value = {
    workspaceId             = local.ids.workspace
    computeId               = local.ids.compute
    workspaceIdentityId     = "${local.resource_prefix}/Microsoft.ManagedIdentity/userAssignedIdentities/${local.names.workspace_identity}"
    computeIdentityId       = "${local.resource_prefix}/Microsoft.ManagedIdentity/userAssignedIdentities/${local.names.compute_identity}"
    computeIdentityClientId = azurerm_user_assigned_identity.compute.client_id
    storageAccountId        = local.ids.storage
    registryId              = local.ids.registry
    keyVaultId              = local.ids.vault
    networkSecurityGroupId  = local.gpu_nsg_id
    natGatewayId            = local.ids.nat
    publicIpId              = local.ids.pip
    container               = local.names.container
    datastore               = local.names.datastore
    releaseContainer        = local.names.release_container
    releaseDatastore        = local.names.release_datastore
    controlContainer        = local.names.control_container
    policy = {
      vmSize              = "Standard_NC24ads_A100_v4", vmPriority = "LowPriority", minNodes = 0, maxNodes = var.max_nodes,
      idleSeconds         = 120, instanceCount = 1, maxJobMinutes = var.max_job_minutes, maxPaygHourlyUsd = var.max_payg_hourly_usd,
      manageComputeEgress = var.manage_compute_egress, egressPublicIpTags = var.egress_public_ip_tags, owner = "studio"
    }
    ownershipTags = local.ownership_tags
  }
}

output "inventory" {
  description = "Durable names/IDs needed by the one shared host and offline tests; informational only."
  value = {
    names                = local.names
    ids                  = local.ids
    resourceGroupId      = local.resource_group_id
    workspacePrincipalId = azurerm_user_assigned_identity.workspace.principal_id
    computePrincipalId   = azurerm_user_assigned_identity.compute.principal_id
    roleIds              = local.role_ids
  }
}

output "gpu_nsg_rules" {
  value = {
    networkSecurityGroupId = local.gpu_nsg_id
    customerManaged        = var.existing_gpu_nsg_id != null
    gpuSubnetId            = var.gpu_subnet_id
    rules                  = [for rule in local.gpu_rules : merge(rule, { source_ports = ["*"] })]
  }
}
