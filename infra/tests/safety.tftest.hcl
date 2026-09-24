# All providers are native Terraform mocks; no CLI credential or Azure call is
# permitted/needed. Fixture IDs correspond exactly to wan-demo's naming hash.
mock_provider "azurerm" {
  override_during = plan

  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233"
    }
  }
  mock_resource "azurerm_key_vault" {
    defaults = {
      id        = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.KeyVault/vaults/kv-wan-demo-14173233"
      vault_uri = "https://kv-wan-demo-14173233.vault.azure.net/"
    }
  }
  mock_resource "azurerm_container_registry" {
    defaults = {
      id                       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ContainerRegistry/registries/crwandemo14173233"
      login_server             = "crwandemo14173233.azurecr.io"
      data_endpoint_host_names = ["crwandemo14173233.eastus2.data.azurecr.io"]
    }
  }
  mock_resource "azurerm_application_insights" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Insights/components/appi-wan-demo-14173233"
    }
  }
  mock_resource "azurerm_network_security_group" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/networkSecurityGroups/nsg-wan-demo-14173233-gpu"
    }
  }
  mock_resource "azurerm_nat_gateway" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/natGateways/nat-wan-demo-14173233"
    }
  }
  mock_resource "azurerm_public_ip" {
    defaults = {
      id         = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/publicIPAddresses/pip-wan-demo-14173233-egress"
      ip_address = "192.0.2.1"
    }
  }
  mock_resource "azurerm_nat_gateway_public_ip_association" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/natGateways/nat-wan-demo-14173233|/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/publicIPAddresses/pip-wan-demo-14173233-egress"
    }
  }
  mock_resource "azurerm_subnet_nat_gateway_association" {
    defaults = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
    }
  }
  mock_resource "azurerm_private_endpoint" {
    defaults = {
      custom_dns_configs       = []
      private_dns_zone_configs = []
    }
  }
}

mock_provider "azapi" {
  override_during = plan
  mock_data "azapi_resource" {
    defaults = {
      location = "eastus2"
      output   = {}
    }
  }
}

override_data {
  target = data.azapi_resource.gpu_subnet
  values = {
    output = {
      properties = {
        addressPrefix         = "10.42.1.0/26"
        defaultOutboundAccess = false
        delegations           = []
      }
    }
  }
}

override_data {
  target = data.azapi_resource.private_endpoint_subnet
  values = {
    output = {
      properties = {
        addressPrefixes = ["10.42.2.0/26"]
      }
    }
  }
}

override_resource {
  override_during = plan
  target          = azurerm_user_assigned_identity.workspace
  values = {
    id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-14173233-workspace"
    principal_id = "44444444-4444-4444-4444-444444444444"
    client_id    = "55555555-5555-5555-5555-555555555555"
    tenant_id    = "22222222-2222-2222-2222-222222222222"
  }
}

override_resource {
  override_during = plan
  target          = azurerm_user_assigned_identity.compute
  values = {
    id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-14173233-gpu"
    principal_id = "66666666-6666-6666-6666-666666666666"
    client_id    = "77777777-7777-7777-7777-777777777777"
    tenant_id    = "22222222-2222-2222-2222-222222222222"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.storage
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.container
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233/blobServices/default/containers/wan-studio"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.workspace
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.datastore
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/datastores/wan_blob"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.compute
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/computes/wan-gpu"
  }
}

override_resource {
  override_during = plan
  target          = azurerm_private_endpoint.workspace
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/privateEndpoints/pe-wan-demo-14173233-workspace"
    private_dns_zone_configs = [
      {
        id                  = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/privateEndpoints/pe-wan-demo-14173233-workspace/privateDnsZoneGroups/default"
        name                = "api"
        private_dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.api.azureml.ms"
        record_sets = [{
          fqdn         = "88888888-8888-8888-8888-888888888888.workspace.eastus2.privatelink.api.azureml.ms."
          ip_addresses = ["10.42.2.5"]
          name         = "88888888-8888-8888-8888-888888888888.workspace.eastus2"
          ttl          = 10
          type         = "A"
        }]
      },
      {
        id                  = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/privateEndpoints/pe-wan-demo-14173233-workspace/privateDnsZoneGroups/default"
        name                = "notebooks"
        private_dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.notebooks.azure.net"
        record_sets = [{
          fqdn         = "mlw-wan-demo-14173233-eastus2-88888888.privatelink.notebooks.azure.net."
          ip_addresses = ["10.42.2.6"]
          name         = "mlw-wan-demo-14173233-eastus2-88888888"
          ttl          = 10
          type         = "A"
        }]
      }
    ]
  }
}

variables {
  deployment_name            = "wan-demo"
  subscription_id            = "11111111-1111-1111-1111-111111111111"
  tenant_id                  = "22222222-2222-2222-2222-222222222222"
  operator_principal_id      = "33333333-3333-3333-3333-333333333333"
  location                   = "eastus2"
  gpu_subnet_id              = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  gpu_subnet_cidr            = "10.42.1.0/26"
  existing_gpu_nsg_id        = null
  compute_enabled            = false
  manage_compute_egress      = false
  egress_public_ip_tags      = {}
  private_endpoint_subnet_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/private-endpoints"
  private_dns_zone_ids = {
    blob      = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"
    file      = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.file.core.windows.net"
    vault     = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.vaultcore.azure.net"
    registry  = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurecr.io"
    api       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.api.azureml.ms"
    notebooks = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.notebooks.azure.net"
  }
  log_analytics_workspace_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-monitoring/providers/Microsoft.OperationalInsights/workspaces/customer-law"
  gpu_subnet_dedicated       = true
}

run "default_off_private_foundation" {
  command = plan

  assert {
    condition     = output.studio.portal == null && length(azurerm_linux_web_app.portal) == 0 && length(azurerm_service_plan.portal) == 0 && length(azurerm_private_endpoint.portal) == 0 && length(azurerm_role_assignment.portal) == 0
    error_message = "The App Service portal is opt-in; the default creates no web resources."
  }

  assert {
    condition     = !output.studio.computeEnabled && !output.studio.manageComputeEgress && length(azapi_resource.compute) == 0
    error_message = "Compute must default OFF."
  }
  assert {
    condition     = length(azurerm_nat_gateway.compute) == 0 && length(azurerm_public_ip.compute) == 0 && length(azurerm_subnet_nat_gateway_association.compute) == 0 && length(azurerm_nat_gateway_public_ip_association.compute) == 0
    error_message = "OFF must create no NAT, public IP or egress associations."
  }
  assert {
    condition     = output.studio.maxJobSeconds == 7200 && output.studio.instanceCount == 1 && output.studio.maxPaygHourlyUsd == 4
    error_message = "Runtime must receive the 7200-second / one-instance / default $4 admission contract."
  }
  assert {
    condition = tomap(output.studio.ownershipTags) == tomap({
      application = "wan-safety-studio"
      deployment  = var.deployment_name
      managedBy   = "terraform"
    })
    error_message = "The cached foundation receipt must contain exactly the three agreed ownership tags."
  }
  assert {
    condition     = output.studio.resourceGroupName == "rg-wan-demo-14173233" && output.studio.workspaceName == "mlw-wan-demo-14173233" && output.studio.storageAccountName == "stwandemo14173233" && output.studio.registryName == "crwandemo14173233" && output.studio.keyVaultName == "kv-wan-demo-14173233"
    error_message = "Resource naming must follow the portable stable deployment/subscription hash contract."
  }
  assert {
    condition     = output.studio.computeId == "${output.studio.workspaceId}/computes/wan-gpu" && endswith(output.studio.natGatewayId, "/natGateways/nat-wan-demo-14173233") && endswith(output.studio.publicIpId, "/publicIPAddresses/pip-wan-demo-14173233-egress")
    error_message = "Prospective IDs must remain usable for emergency Stop while compute is OFF."
  }
  assert {
    condition     = !azapi_resource.storage.body.properties.allowSharedKeyAccess && !azapi_resource.storage.body.properties.allowBlobPublicAccess && azapi_resource.storage.body.properties.publicNetworkAccess == "Disabled" && azapi_resource.storage.body.properties.networkAcls.defaultAction == "Deny" && azapi_resource.storage.body.properties.minimumTlsVersion == "TLS1_2" && azapi_resource.container.body.properties.publicAccess == "None"
    error_message = "Storage must remain keyless, TLS-only and private."
  }
  assert {
    condition     = azurerm_key_vault.studio.rbac_authorization_enabled && azurerm_key_vault.studio.purge_protection_enabled && !azurerm_key_vault.studio.public_network_access_enabled && azurerm_key_vault.studio.soft_delete_retention_days == 90 && azurerm_key_vault.studio.network_acls[0].default_action == "Deny"
    error_message = "Vault must retain private RBAC and purge protection."
  }
  assert {
    condition     = azurerm_container_registry.studio.sku == "Premium" && !azurerm_container_registry.studio.admin_enabled && !azurerm_container_registry.studio.anonymous_pull_enabled && !azurerm_container_registry.studio.public_network_access_enabled && azurerm_container_registry.studio.network_rule_set[0].default_action == "Deny"
    error_message = "Registry must be private Premium without admin credentials or anonymous pulls."
  }
  assert {
    condition     = azurerm_application_insights.studio.workspace_id == var.log_analytics_workspace_id
    error_message = "Required Application Insights must use the supplied customer log store."
  }
  assert {
    condition     = azapi_resource.workspace.body.properties.publicNetworkAccess == "Disabled" && !azapi_resource.workspace.body.properties.allowPublicAccessWhenBehindVnet && azapi_resource.workspace.body.properties.systemDatastoresAuthMode == "Identity" && azapi_resource.workspace.body.properties.managedNetwork.isolationMode == "Disabled" && !azapi_resource.workspace.body.properties.provisionNetworkNow && !contains(keys(azapi_resource.workspace.body.properties), "serverlessComputeSettings") && !contains(keys(azapi_resource.workspace.body.properties), "imageBuildCompute")
    error_message = "Workspace must use identity/private connectivity, with no managed VNet, image-build or serverless compute fallback."
  }
  assert {
    condition     = azapi_resource.workspace.identity[0].type == "UserAssigned" && azapi_resource.workspace.identity[0].identity_ids == tolist([output.studio.workspaceIdentityId]) && output.studio.workspaceIdentityId != output.studio.computeIdentityId && output.studio.computeIdentityClientId == "77777777-7777-7777-7777-777777777777"
    error_message = "Workspace and GPU must have separate precreated UAMIs and the correct client ID contract."
  }
  assert {
    condition     = azapi_resource.datastore.body.properties.serviceDataAccessAuthIdentity == "WorkspaceUserAssignedIdentity" && azapi_resource.datastore.body.properties.credentials.credentialsType == "None" && length(keys(azapi_resource.datastore.body.properties.credentials)) == 1 && azapi_resource.datastore.body.properties.protocol == "https" && azapi_resource.datastore.body.properties.subscriptionId == var.subscription_id && azapi_resource.datastore.body.properties.resourceGroup == output.studio.resourceGroupName
    error_message = "The ARM datastore must have explicit credential-less identity, HTTPS and customer scope."
  }
  assert {
    condition     = length(azurerm_private_endpoint.service) == 4 && azurerm_private_endpoint.workspace.private_service_connection[0].subresource_names == tolist(["amlworkspace"]) && length(azurerm_private_endpoint.workspace.private_dns_zone_group[0].private_dns_zone_ids) == 2 && alltrue([for key, endpoint in azurerm_private_endpoint.service : endpoint.subnet_id == var.private_endpoint_subnet_id && endpoint.private_service_connection[0].subresource_names == tolist([key]) && endpoint.private_dns_zone_group[0].private_dns_zone_ids == tolist([var.private_dns_zone_ids[key]])]) && azurerm_private_endpoint.workspace.private_dns_zone_group[0].private_dns_zone_ids == tolist([var.private_dns_zone_ids.api, var.private_dns_zone_ids.notebooks])
    error_message = "Exactly the tested blob/file/vault/registry/amlworkspace groups and existing zone bindings must be used."
  }
  assert {
    condition     = length(azurerm_role_assignment.service) == 5 && length(azurerm_role_assignment.operator) == 3 && alltrue([for grant in concat(values(azurerm_role_assignment.service), values(azurerm_role_assignment.operator)) : startswith(lower(grant.scope), lower("/subscriptions/${var.subscription_id}/resourceGroups/${output.studio.resourceGroupName}/providers/"))])
    error_message = "Exactly eight resource-scoped roles are permitted; no subscription or customer landing-zone grants."
  }
  assert {
    condition     = azurerm_role_assignment.service["compute_storage"].principal_id == "66666666-6666-6666-6666-666666666666" && endswith(azurerm_role_assignment.service["compute_storage"].role_definition_id, "/ba92f5b4-2d11-453d-a403-e96b0029c9fe") && endswith(azurerm_role_assignment.service["compute_registry"].role_definition_id, "/7f951dda-4ed3-4680-a7ca-43fe172d538d") && length([for grant in values(azurerm_role_assignment.service) : grant if grant.principal_id == "66666666-6666-6666-6666-666666666666"]) == 2
    error_message = "GPU identity must have only storage blob contributor and AcrPull; never vault permissions."
  }
  assert {
    condition     = length(azurerm_network_security_group.compute) == 1 && length(azurerm_subnet_network_security_group_association.compute) == 1 && !output.gpu_nsg_rules.customerManaged
    error_message = "Without an external NSG, retain the existing owned NSG and association."
  }
  assert {
    condition     = alltrue([for rule in azurerm_network_security_group.compute[0].security_rule : rule.direction != "Inbound" || rule.access != "Allow" || rule.source_address_prefix == var.gpu_subnet_cidr]) && length([for rule in azurerm_network_security_group.compute[0].security_rule : rule if rule.direction == "Inbound" && rule.access == "Deny" && rule.priority == 4096 && rule.destination_port_range == "*"]) == 1
    error_message = "No public/VPN application or SSH ingress: only node-internal inbound followed by deny all."
  }
  assert {
    condition     = alltrue([for rule in azurerm_network_security_group.compute[0].security_rule : (try(length(rule.destination_port_range), 0) > 0) != (try(length(rule.destination_port_ranges), 0) > 0)]) && length([for rule in azurerm_network_security_group.compute[0].security_rule : rule if rule.destination_address_prefix == "AzureMachineLearning" && rule.protocol == "Udp" && rule.destination_port_range == "5831"]) == 1
    error_message = "NSG single and augmented port fields must be mutually exclusive, including AML UDP 5831."
  }
  assert {
    condition = toset([
      for rule in output.gpu_nsg_rules.rules : jsonencode({
        name   = rule.name, priority = rule.priority, direction = rule.direction, protocol = rule.protocol,
        source = rule.source, destination = rule.destination, ports = sort(rule.ports), access = rule.access
      })
      ]) == toset([
      for rule in azurerm_network_security_group.compute[0].security_rule : jsonencode({
        name   = rule.name, priority = rule.priority, direction = rule.direction, protocol = rule.protocol,
        source = rule.source_address_prefix, destination = rule.destination_address_prefix,
        ports  = sort(try(length(rule.destination_port_ranges), 0) > 0 ? tolist(rule.destination_port_ranges) : [rule.destination_port_range]),
        access = rule.access
      })
    ]) && alltrue([for rule in output.gpu_nsg_rules.rules : rule.source_ports == ["*"]])
    error_message = "The customer handoff must match every owned NSG rule, not a separately maintained approximation."
  }
  assert {
    condition = alltrue([
      for host in [
        "stwandemo14173233.blob.core.windows.net",
        "stwandemo14173233.file.core.windows.net",
        "kv-wan-demo-14173233.vault.azure.net",
        "crwandemo14173233.azurecr.io",
        "crwandemo14173233.eastus2.data.azurecr.io",
        "88888888-8888-8888-8888-888888888888.workspace.eastus2.privatelink.api.azureml.ms",
        "mlw-wan-demo-14173233-eastus2-88888888.privatelink.notebooks.azure.net"
      ] : contains(output.studio.privateConnectivityHosts, host)
    ])
    error_message = "OFF receipts must include blob/file/vault/registry login and data plus service-returned AML API/notebook FQDNs."
  }
}

run "customer_managed_nsg_handoff" {
  command = plan
  variables {
    existing_gpu_nsg_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
  }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          delegations           = []
          networkSecurityGroup = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
          }
        }
      }
    }
  }
  assert {
    condition     = length(azurerm_network_security_group.compute) == 0 && length(azurerm_subnet_network_security_group_association.compute) == 0 && length(azapi_resource.compute) == 0
    error_message = "Foundation deployment must not manage the customer's NSG/association or arm compute."
  }
  assert {
    condition     = output.studio.networkSecurityGroupId == var.existing_gpu_nsg_id && output.gpu_nsg_rules.networkSecurityGroupId == var.existing_gpu_nsg_id && output.gpu_nsg_rules.customerManaged && output.gpu_nsg_rules.rules == run.default_off_private_foundation.gpu_nsg_rules.rules
    error_message = "The external NSG must reach the receipt, with the same complete rule handoff as owned mode."
  }
}

run "restricted_platform_egress" {
  command = plan
  assert {
    condition = alltrue([
      for rule in output.gpu_nsg_rules.rules : rule.direction != "Outbound" || rule.access != "Allow" ||
      !contains(["Internet", "AzureCloud", "*"], rule.destination)
    ])
    error_message = "GPU egress must not include a blanket Internet, AzureCloud or wildcard allow."
  }
  assert {
    condition = alltrue([
      for entry in [
        { name = "allow-batch", destination = "BatchNodeManagement.eastus2" },
        { name = "allow-storage", destination = "Storage.eastus2" },
        { name = "allow-monitor", destination = "AzureMonitor" },
        { name = "allow-microsoft-registry", destination = "MicrosoftContainerRegistry" },
        { name = "allow-microsoft-registry-cdn", destination = "AzureFrontDoor.FirstParty" }
        ] : length([for rule in output.gpu_nsg_rules.rules : rule if rule.name == entry.name &&
      rule.destination == entry.destination && rule.source == var.gpu_subnet_cidr && rule.ports == ["443"]]) == 1
    ])
    error_message = "Regional Batch/Storage and explicit Microsoft runtime/monitoring destinations must be present on port 443 only."
  }
}

run "customer_managed_nsg_with_compute" {
  command = plan
  variables {
    existing_gpu_nsg_id   = run.customer_managed_nsg_handoff.studio.networkSecurityGroupId
    compute_enabled       = true
    manage_compute_egress = true
  }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          networkSecurityGroup = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
          }
        }
      }
    }
  }
  assert {
    condition     = length(azapi_resource.compute) == 1 && length(azurerm_subnet_nat_gateway_association.compute) == 1 && length(azurerm_network_security_group.compute) == 0 && length(azurerm_subnet_network_security_group_association.compute) == 0
    error_message = "Start must preserve external NSG ownership even when owned NAT is enabled."
  }
}

run "reject_unattached_customer_nsg" {
  command = plan
  variables {
    existing_gpu_nsg_id = run.customer_managed_nsg_handoff.studio.networkSecurityGroupId
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_different_customer_nsg" {
  command = plan
  variables {
    existing_gpu_nsg_id = run.customer_managed_nsg_handoff.studio.networkSecurityGroupId
  }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          networkSecurityGroup = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/unselected-nsg"
          }
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_cross_subscription_customer_nsg" {
  command = plan
  variables {
    existing_gpu_nsg_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
  }
  expect_failures = [var.existing_gpu_nsg_id]
}

run "reject_empty_customer_nsg" {
  command = plan
  variables {
    existing_gpu_nsg_id = ""
  }
  expect_failures = [var.existing_gpu_nsg_id]
}

run "on_with_explicit_owned_egress" {
  command = plan
  variables {
    compute_enabled       = true
    manage_compute_egress = true
    egress_public_ip_tags = { FirstPartyUsage = "/Unprivileged" }
  }
  assert {
    condition     = length(azapi_resource.compute) == 1 && length(azurerm_nat_gateway.compute) == 1 && length(azurerm_public_ip.compute) == 1 && length(azurerm_subnet_nat_gateway_association.compute) == 1 && length(azurerm_nat_gateway_public_ip_association.compute) == 1
    error_message = "Only explicit ON plus managed-egress consent creates compute/NAT/PIP and owned associations."
  }
  assert {
    condition     = azapi_resource.compute[0].body.properties.computeType == "AmlCompute" && azapi_resource.compute[0].body.properties.disableLocalAuth && azapi_resource.compute[0].body.properties.properties.vmSize == "Standard_NC24ads_A100_v4" && azapi_resource.compute[0].body.properties.properties.vmPriority == "LowPriority" && azapi_resource.compute[0].body.properties.properties.osType == "Linux"
    error_message = "Compute must be exactly Linux LowPriority A100 with local auth disabled."
  }
  assert {
    condition     = azapi_resource.compute[0].body.properties.properties.scaleSettings.minNodeCount == 0 && azapi_resource.compute[0].body.properties.properties.scaleSettings.maxNodeCount == 1 && azapi_resource.compute[0].body.properties.properties.scaleSettings.nodeIdleTimeBeforeScaleDown == "PT120S"
    error_message = "The fixed single-node min0/max1/120-second idle contract must not drift."
  }
  assert {
    condition     = !azapi_resource.compute[0].body.properties.properties.enableNodePublicIp && azapi_resource.compute[0].body.properties.properties.remoteLoginPortPublicAccess == "Disabled" && !contains(keys(azapi_resource.compute[0].body.properties.properties), "userAccountCredentials") && azapi_resource.compute[0].body.properties.properties.subnet.id == var.gpu_subnet_id && azapi_resource.compute[0].identity[0].identity_ids == tolist([output.studio.computeIdentityId])
    error_message = "No node public IP, SSH credentials or workspace identity on compute."
  }
  assert {
    condition     = azapi_resource.compute[0].id == output.studio.computeId && azurerm_nat_gateway.compute[0].id == output.studio.natGatewayId && azurerm_public_ip.compute[0].id == output.studio.publicIpId && azurerm_subnet_nat_gateway_association.compute[0].subnet_id == var.gpu_subnet_id && azurerm_subnet_nat_gateway_association.compute[0].nat_gateway_id == output.studio.natGatewayId
    error_message = "Live mocked resources and prospective emergency IDs must agree; NAT may attach only to the dedicated GPU subnet."
  }
  assert {
    condition     = azurerm_nat_gateway_public_ip_association.compute[0].id == "${output.studio.natGatewayId}|${output.studio.publicIpId}" && azurerm_nat_gateway_public_ip_association.compute[0].nat_gateway_id == output.studio.natGatewayId && azurerm_nat_gateway_public_ip_association.compute[0].public_ip_address_id == output.studio.publicIpId && azurerm_subnet_nat_gateway_association.compute[0].id == output.studio.gpuSubnetId
    error_message = "Emergency Stop must use the provider's NAT|PIP composite association ID and subnet ID without treating either association as a deletable ARM resource."
  }
  assert {
    condition     = azurerm_public_ip.compute[0].sku == "Standard" && azurerm_public_ip.compute[0].allocation_method == "Static" && azurerm_nat_gateway.compute[0].idle_timeout_in_minutes == 4 && azurerm_public_ip.compute[0].tags == tomap(output.studio.ownershipTags) && azurerm_nat_gateway.compute[0].tags == tomap(output.studio.ownershipTags)
    error_message = "Owned egress must have deterministic ownership, Standard static IPv4 and four-minute idle timeout."
  }
  assert {
    condition     = azurerm_public_ip.compute[0].ip_tags == var.egress_public_ip_tags
    error_message = "Customer-required public-IP policy tags must be preserved in desired configuration."
  }
}

run "on_uses_customer_routed_egress_by_default" {
  command = plan
  variables {
    compute_enabled = true
  }
  assert {
    condition     = length(azapi_resource.compute) == 1 && length(azurerm_nat_gateway.compute) == 0 && length(azurerm_public_ip.compute) == 0 && length(azurerm_subnet_nat_gateway_association.compute) == 0
    error_message = "Compute alone must not opt in to managed egress costs or customer NAT changes."
  }
}

run "egress_consent_does_not_arm_compute" {
  command = plan
  variables {
    manage_compute_egress = true
  }
  assert {
    condition     = length(azapi_resource.compute) == 0 && length(azurerm_nat_gateway.compute) == 0 && length(azurerm_public_ip.compute) == 0
    error_message = "Egress consent cannot arm compute implicitly."
  }
}

run "reject_shared_subnet_acknowledgment" {
  command = plan
  variables {
    gpu_subnet_dedicated = false
  }
  expect_failures = [var.gpu_subnet_dedicated]
}

run "reject_same_gpu_and_pe_subnet" {
  command = plan
  variables {
    private_endpoint_subnet_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  }
  expect_failures = [var.private_endpoint_subnet_id]
}

run "reject_cross_subscription_gpu" {
  command = plan
  variables {
    gpu_subnet_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  }
  expect_failures = [var.gpu_subnet_id]
}

run "reject_cross_subscription_logging" {
  command = plan
  variables {
    log_analytics_workspace_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-monitoring/providers/Microsoft.OperationalInsights/workspaces/customer-law"
  }
  expect_failures = [var.log_analytics_workspace_id]
}

run "reject_cross_subscription_pe" {
  command = plan
  variables {
    private_endpoint_subnet_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/private-endpoints"
  }
  expect_failures = [var.private_endpoint_subnet_id]
}

run "reject_cross_subscription_dns" {
  command = plan
  variables {
    private_dns_zone_ids = {
      blob      = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net"
      file      = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.file.core.windows.net"
      vault     = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.vaultcore.azure.net"
      registry  = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurecr.io"
      api       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.api.azureml.ms"
      notebooks = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.notebooks.azure.net"
    }
  }
  expect_failures = [var.private_dns_zone_ids]
}

run "reject_incorrect_private_dns_zone_name" {
  command = plan
  variables {
    private_dns_zone_ids = {
      blob      = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/blob.core.windows.net"
      file      = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.file.core.windows.net"
      vault     = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.vaultcore.azure.net"
      registry  = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurecr.io"
      api       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.api.azureml.ms"
      notebooks = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.notebooks.azure.net"
    }
  }
  expect_failures = [var.private_dns_zone_ids]
}

run "reject_unexpected_cidr" {
  command = plan
  variables {
    gpu_subnet_cidr = "10.42.9.0/26"
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_noncanonical_cidr" {
  command = plan
  variables {
    gpu_subnet_cidr = "10.42.1.9/26"
  }
  expect_failures = [var.gpu_subnet_cidr]
}

run "reject_implicit_outbound" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = true
          delegations           = []
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_delegated_gpu_subnet" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          delegations           = [{ name = "other-service" }]
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_unspecified_outbound_access" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix = "10.42.1.0/26"
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_multiple_gpu_prefixes" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefixes       = ["10.42.1.0/26", "10.42.3.0/26"]
          defaultOutboundAccess = false
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_foreign_nat_even_when_off" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          natGateway = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/natGateways/customer-nat"
          }
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_foreign_nsg_even_when_on" {
  command = plan
  variables {
    compute_enabled       = true
    manage_compute_egress = true
  }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          networkSecurityGroup = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
          }
        }
      }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "allow_owned_associations_when_stopping" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = {
        properties = {
          addressPrefix         = "10.42.1.0/26"
          defaultOutboundAccess = false
          natGateway = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/natGateways/nat-wan-demo-14173233"
          }
          networkSecurityGroup = {
            id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/networkSecurityGroups/nsg-wan-demo-14173233-gpu"
          }
        }
      }
    }
  }
  assert {
    condition     = !output.studio.computeEnabled && length(azurerm_nat_gateway.compute) == 0
    error_message = "Known owned NAT/NSG must not prevent the OFF plan."
  }
}

run "reject_cross_region_network" {
  command = plan
  override_data {
    target = data.azapi_resource.gpu_vnet
    values = {
      location = "westus2"
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_invalid_deployment_name" {
  command = plan
  variables {
    deployment_name = "not--dns"
  }
  expect_failures = [var.deployment_name]
}

run "accept_sixteen_character_deployment_name" {
  command = plan
  variables {
    deployment_name = "customer-studio1"
  }
  override_resource {
    target          = azurerm_resource_group.studio
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328"
    }
  }
  override_resource {
    target          = azapi_resource.storage
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.Storage/storageAccounts/stcustomerstu58ff7328"
    }
  }
  override_resource {
    target          = azapi_resource.container
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.Storage/storageAccounts/stcustomerstu58ff7328/blobServices/default/containers/wan-studio"
    }
  }
  override_resource {
    target          = azapi_resource.workspace
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.MachineLearningServices/workspaces/mlw-customer-studio1-58ff7328"
    }
  }
  override_resource {
    target          = azapi_resource.datastore
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.MachineLearningServices/workspaces/mlw-customer-studio1-58ff7328/datastores/wan_blob"
    }
  }
  override_resource {
    target          = azurerm_key_vault.studio
    override_during = plan
    values = {
      id        = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.KeyVault/vaults/kv-customer-stu-58ff7328"
      vault_uri = "https://kv-customer-stu-58ff7328.vault.azure.net/"
    }
  }
  override_resource {
    target          = azurerm_container_registry.studio
    override_during = plan
    values = {
      id                       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.ContainerRegistry/registries/crcustomerstudio158ff7328"
      login_server             = "crcustomerstudio158ff7328.azurecr.io"
      data_endpoint_host_names = ["crcustomerstudio158ff7328.eastus2.data.azurecr.io"]
    }
  }
  override_resource {
    target          = azurerm_application_insights.studio
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.Insights/components/appi-customer-studio1-58ff7328"
    }
  }
  override_resource {
    target          = azurerm_network_security_group.compute
    override_during = plan
    values = {
      id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.Network/networkSecurityGroups/nsg-customer-studio1-58ff7328-gpu"
    }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.workspace
    override_during = plan
    values = {
      id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-customer-studio1-58ff7328-workspace"
      principal_id = "44444444-4444-4444-4444-444444444444"
      client_id    = "55555555-5555-5555-5555-555555555555"
      tenant_id    = "22222222-2222-2222-2222-222222222222"
    }
  }
  override_resource {
    target          = azurerm_user_assigned_identity.compute
    override_during = plan
    values = {
      id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-customer-studio1-58ff7328-gpu"
      principal_id = "66666666-6666-6666-6666-666666666666"
      client_id    = "77777777-7777-7777-7777-777777777777"
      tenant_id    = "22222222-2222-2222-2222-222222222222"
    }
  }
  override_resource {
    target          = azurerm_private_endpoint.workspace
    override_during = plan
    values = {
      id                       = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-customer-studio1-58ff7328/providers/Microsoft.Network/privateEndpoints/pe-customer-studio1-58ff7328-workspace"
      private_dns_zone_configs = []
    }
  }
  assert {
    condition     = output.studio.deploymentName == "customer-studio1" && length(output.studio.deploymentName) == 16 && startswith(output.studio.resourceGroupName, "rg-customer-studio1-") && startswith(output.studio.workspaceName, "mlw-customer-studio1-")
    error_message = "The shared configuration must accept a 16-character deployment slug and preserve it in RG/workspace names."
  }
  assert {
    condition     = length(output.studio.keyVaultName) <= 24 && !strcontains(output.studio.keyVaultName, "--") && length(output.studio.storageAccountName) <= 24 && can(regex("^[a-z0-9]+$", output.studio.storageAccountName))
    error_message = "Long deployment slugs must still produce legal bounded vault/storage names."
  }
  assert {
    condition = alltrue([
      for id in [
        output.studio.workspaceId, output.studio.computeId, output.studio.storageAccountId, output.studio.keyVaultId,
        output.studio.workspaceIdentityId, output.studio.computeIdentityId, output.studio.natGatewayId,
        output.studio.publicIpId, output.studio.networkSecurityGroupId
      ] : startswith(id, "/subscriptions/${var.subscription_id}/resourceGroups/${output.studio.resourceGroupName}/providers/")
    ])
    error_message = "Every owned receipt ID must remain within the same deterministic resource group, including long-slug names."
  }
}

run "reject_seventeen_character_deployment_name" {
  command = plan
  variables {
    deployment_name = "customer-studio12"
  }
  expect_failures = [var.deployment_name]
}

run "reject_nonpositive_price_limit" {
  command = plan
  variables {
    max_payg_hourly_usd = 0
  }
  expect_failures = [var.max_payg_hourly_usd]
}

run "accept_configurable_price_above_default" {
  command = plan
  variables {
    max_payg_hourly_usd = 4.01
  }
  assert {
    condition     = output.studio.maxPaygHourlyUsd == 4.01
    error_message = "The $4 default is not a fixed upper bound; a finite positive configured threshold must reach the receipt unchanged."
  }
}

run "reject_nonfinite_price_limit" {
  command = plan
  variables {
    max_payg_hourly_usd = pow(10, 400)
  }
  expect_failures = [var.max_payg_hourly_usd]
}

run "private_app_service_portal" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  override_data {
    target = data.azapi_resource.portal_subnet[0]
    values = {
      output = { properties = { delegations = [{ properties = { serviceName = "Microsoft.Web/serverFarms" } }] } }
    }
  }
  override_resource {
    target          = azurerm_linux_web_app.portal[0]
    override_during = plan
    values = {
      id               = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Web/sites/app-wan-demo-14173233"
      default_hostname = "app-wan-demo-14173233.azurewebsites.net"
    }
  }
  assert {
    condition     = !azurerm_linux_web_app.portal[0].public_network_access_enabled && azurerm_linux_web_app.portal[0].https_only && azurerm_linux_web_app.portal[0].virtual_network_subnet_id == var.portal.subnet_id && !azurerm_linux_web_app.portal[0].site_config[0].vnet_route_all_enabled && !azurerm_linux_web_app.portal[0].ftp_publish_basic_authentication_enabled && !azurerm_linux_web_app.portal[0].webdeploy_publish_basic_authentication_enabled
    error_message = "The App Service must be private-endpoint-only, HTTPS-only, VNet-integrated for RFC1918 traffic and without basic-auth publishing."
  }
  assert {
    condition     = azurerm_private_endpoint.portal[0].subnet_id == var.private_endpoint_subnet_id && azurerm_private_endpoint.portal[0].private_service_connection[0].subresource_names == tolist(["sites"]) && azurerm_private_endpoint.portal[0].private_dns_zone_group[0].private_dns_zone_ids == tolist([var.portal.dns_zone_id])
    error_message = "Portal inbound must use one private endpoint in the studio PE subnet and the supplied App Service zone."
  }
  assert {
    condition     = azurerm_service_plan.portal[0].sku_name == "B1" && azurerm_service_plan.portal[0].worker_count == 1 && azurerm_service_plan.portal[0].os_type == "Linux"
    error_message = "Default portal hosting is one Linux B1 instance (process-local sessions)."
  }
  assert {
    condition     = length(azurerm_role_assignment.portal) == 2 && endswith(azurerm_role_assignment.portal["storage"].role_definition_id, "/ba92f5b4-2d11-453d-a403-e96b0029c9fe") && endswith(azurerm_role_assignment.portal["workspace"].role_definition_id, "/f6c7c914-8db3-469d-8ca1-694a8f32e121") && length(azurerm_role_assignment.service) == 5
    error_message = "The portal identity receives only studio blob data and workspace data-scientist access."
  }
  assert {
    condition     = !anytrue([for key in keys(azurerm_linux_web_app.portal[0].app_settings) : can(regex("(?i)secret|password|key", key))]) && !contains(keys(azurerm_linux_web_app.portal[0].app_settings), "WAN_STUDIO_ARMED")
    error_message = "App settings must hold no secrets and Terraform must never arm submissions."
  }
  assert {
    condition     = output.studio.portal.hostname == "app-wan-demo-14173233.azurewebsites.net" && output.studio.portal.scmHostname == "app-wan-demo-14173233.scm.azurewebsites.net"
    error_message = "The receipt must expose the site and SCM hostnames used by Publish and Entra redirect setup."
  }
}

run "reject_undelegated_portal_subnet" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  expect_failures = [azurerm_service_plan.portal]
}

run "reject_portal_on_studio_subnet" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/private-endpoints"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  expect_failures = [var.portal]
}