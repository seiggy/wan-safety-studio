output "studio" {
  # maxJobSeconds is a runtime admission/server deadline, not a compute idle
  # timeout or billing guarantee. The job launcher/server enforces it separately.
  description = "Lifecycle/runtime contract. Optional resource IDs are prospective even when OFF; presence must be checked against ARM, not inferred from these strings."
  value = {
    subscriptionId          = lower(var.subscription_id)
    tenantId                = lower(var.tenant_id)
    location                = var.location
    deploymentName          = var.deployment_name
    resourceGroupName       = azurerm_resource_group.studio.name
    workspaceName           = azapi_resource.workspace.name
    workspaceId             = azapi_resource.workspace.id
    computeName             = local.names.compute
    computeId               = local.ids.compute
    storageAccountName      = azapi_resource.storage.name
    storageAccountId        = azapi_resource.storage.id
    registryName            = azurerm_container_registry.studio.name
    registryLoginServer     = azurerm_container_registry.studio.login_server
    keyVaultName            = azurerm_key_vault.studio.name
    keyVaultId              = azurerm_key_vault.studio.id
    containerName           = azapi_resource.container.name
    datastoreName           = azapi_resource.datastore.name
    workspaceIdentityId     = azurerm_user_assigned_identity.workspace.id
    computeIdentityId       = azurerm_user_assigned_identity.compute.id
    computeIdentityClientId = azurerm_user_assigned_identity.compute.client_id
    gpuSubnetId             = var.gpu_subnet_id
    gpuSubnetCidr           = var.gpu_subnet_cidr
    privateEndpointSubnetId = var.private_endpoint_subnet_id
    natGatewayId            = local.ids.nat
    publicIpId              = local.ids.pip
    networkSecurityGroupId  = local.gpu_nsg_id
    ownershipTags           = local.ownership_tags
    privateConnectivityHosts = distinct(concat(
      [
        "${local.names.storage}.blob.core.windows.net",
        "${local.names.storage}.file.core.windows.net",
        "${local.names.vault}.vault.azure.net",
        azurerm_container_registry.studio.login_server
      ],
      tolist(azurerm_container_registry.studio.data_endpoint_host_names),
      flatten([
        for endpoint in concat(values(azurerm_private_endpoint.service), [azurerm_private_endpoint.workspace]) : [
          for zone in endpoint.private_dns_zone_configs : [
            for record in zone.record_sets : trimsuffix(record.fqdn, ".")
          ]
        ]
      ])
    ))
    computeEnabled      = var.compute_enabled
    manageComputeEgress = var.manage_compute_egress
    maxPaygHourlyUsd    = var.max_payg_hourly_usd
    maxJobSeconds       = 7200
    instanceCount       = 1
    # length() guards refresh-only runs after the web app failed to create (e.g. App Service quota).
    portal = local.portal_enabled && length(azurerm_linux_web_app.portal) == 1 ? {
      id                  = azurerm_linux_web_app.portal[0].id
      name                = azurerm_linux_web_app.portal[0].name
      hostname            = azurerm_linux_web_app.portal[0].default_hostname
      scmHostname         = replace(azurerm_linux_web_app.portal[0].default_hostname, "/^([^.]+)\\./", "$1.scm.")
      identityClientId    = azurerm_user_assigned_identity.portal[0].client_id
      identityPrincipalId = azurerm_user_assigned_identity.portal[0].principal_id
    } : null
  }
}

output "gpu_nsg_rules" {
  description = "Security-team handoff, not proof of live rules: apply/reconcile these baseline rules before GPU use. Source ports are always '*'. Review existing rules and priorities; never overwrite customer policy blindly."
  value = {
    networkSecurityGroupId = local.gpu_nsg_id
    customerManaged        = var.existing_gpu_nsg_id != null
    gpuSubnetId            = var.gpu_subnet_id
    rules                  = [for rule in local.gpu_rules : merge(rule, { source_ports = ["*"] })]
  }
}
