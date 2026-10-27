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
  target          = azapi_resource.release_container
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233/blobServices/default/containers/wan-release"
  }
}

override_resource {
  override_during = plan
  target          = azapi_resource.release_datastore
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/datastores/wan_release"
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
  legacy_names               = true
  gpu_subnet_id              = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  gpu_subnet_cidr            = "10.42.1.0/26"
  existing_gpu_nsg_id        = null
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

run "private_regional_foundation" {
  command = plan
  module { source = "./modules/region" }
  assert {
    condition     = output.inventory.names.resource_group == "rg-wan-demo-14173233" && output.resources.workspaceId == jsondecode(file("tests/fixtures/studio-off.json")).workspaceId && output.resources.computeIdentityId == jsondecode(file("tests/fixtures/studio-off.json")).computeIdentityId
    error_message = "Legacy RG/workspace/UAMI identities must remain unchanged by module extraction."
  }
  assert {
    condition     = output.resources.policy.owner == "studio" && output.resources.policy.minNodes == 0 && output.resources.policy.maxNodes == 1 && output.resources.policy.instanceCount == 1 && output.resources.policy.idleSeconds == 120 && output.resources.policy.maxJobMinutes == 120 && output.resources.policy.vmPriority == "LowPriority" && output.resources.policy.vmSize == "Standard_NC24ads_A100_v4"
    error_message = "The output is min-zero/single-instance private Spot policy, never a Terraform allocation."
  }
  assert {
    condition     = !azapi_resource.storage.body.properties.allowSharedKeyAccess && !azapi_resource.storage.body.properties.allowBlobPublicAccess && azapi_resource.storage.body.properties.publicNetworkAccess == "Disabled" && azapi_resource.storage.body.properties.networkAcls.defaultAction == "Deny" && azapi_resource.storage.body.properties.minimumTlsVersion == "TLS1_2"
    error_message = "Storage remains keyless, private and TLS-only."
  }
  assert {
    condition     = azurerm_key_vault.studio.rbac_authorization_enabled && azurerm_key_vault.studio.purge_protection_enabled && !azurerm_key_vault.studio.public_network_access_enabled && azurerm_key_vault.studio.soft_delete_retention_days == 90
    error_message = "Vault RBAC, privacy and normal protected deletion must remain."
  }
  assert {
    condition     = azurerm_container_registry.studio.sku == "Premium" && !azurerm_container_registry.studio.admin_enabled && !azurerm_container_registry.studio.anonymous_pull_enabled && !azurerm_container_registry.studio.public_network_access_enabled && azurerm_container_registry.studio.network_rule_set[0].default_action == "Deny"
    error_message = "ACR remains private Premium with no admin or anonymous fallback."
  }
  assert {
    condition     = azurerm_application_insights.studio.workspace_id == var.log_analytics_workspace_id && azapi_resource.workspace.body.properties.publicNetworkAccess == "Disabled" && !azapi_resource.workspace.body.properties.allowPublicAccessWhenBehindVnet && azapi_resource.workspace.body.properties.systemDatastoresAuthMode == "Identity" && azapi_resource.workspace.body.properties.managedNetwork.isolationMode == "Disabled" && !azapi_resource.workspace.body.properties.provisionNetworkNow && !contains(keys(azapi_resource.workspace.body.properties), "serverlessComputeSettings") && !contains(keys(azapi_resource.workspace.body.properties), "imageBuildCompute")
    error_message = "Use supplied logging, private identity datastores, no managed VNet/build/serverless compute."
  }
  assert {
    condition     = azapi_resource.workspace.identity[0].identity_ids == tolist([output.resources.workspaceIdentityId]) && output.resources.workspaceIdentityId != output.resources.computeIdentityId && output.resources.computeIdentityClientId == "77777777-7777-7777-7777-777777777777"
    error_message = "Workspace and GPU identities remain separate."
  }
  assert {
    condition     = length(azurerm_private_endpoint.service) == 4 && length(azurerm_private_endpoint.workspace.private_dns_zone_group[0].private_dns_zone_ids) == 2 && alltrue([for key, endpoint in azurerm_private_endpoint.service : endpoint.subnet_id == var.private_endpoint_subnet_id && endpoint.private_service_connection[0].subresource_names == tolist([key]) && endpoint.private_dns_zone_group[0].private_dns_zone_ids == tolist([var.private_dns_zone_ids[key]])])
    error_message = "Keep blob/file/vault/ACR/AML private endpoints and supplied zone groups."
  }
  assert {
    condition     = azapi_resource.datastore.body.properties.credentials.credentialsType == "None" && azapi_resource.release_datastore.body.properties.credentials.credentialsType == "None" && azapi_resource.datastore.body.properties.serviceDataAccessAuthIdentity == "WorkspaceUserAssignedIdentity" && azapi_resource.release_datastore.body.properties.serviceDataAccessAuthIdentity == "WorkspaceUserAssignedIdentity" && output.resources.controlContainer == "wan-control" && azapi_resource.control_container.body.properties.publicAccess == "None"
    error_message = "Run/release/control stores stay separate; AML datastores are credential-less."
  }
  assert {
    condition     = length(azurerm_role_assignment.service) == 6 && length(azurerm_role_assignment.operator) == 4 && endswith(azurerm_role_assignment.service["compute_storage"].scope, "/containers/wan-studio") && endswith(azurerm_role_assignment.service["compute_release_reader"].scope, "/containers/wan-release") && !anytrue([for grant in values(azurerm_role_assignment.service) : grant.principal_id == "66666666-6666-6666-6666-666666666666" && (strcontains(grant.scope, "wan-control") || strcontains(lower(grant.scope), "vaults/") || grant.scope == azapi_resource.storage.id)])
    error_message = "GPU must have no control, vault or account-wide data access. Unchanged legacy grants remain."
  }
  assert {
    condition     = azurerm_role_assignment.service["workspace_registry"].name == uuidv5("url", lower("${azurerm_container_registry.studio.id}/44444444-4444-4444-4444-444444444444/7f951dda-4ed3-4680-a7ca-43fe172d538d")) && output.resources.ownershipTags.region == var.location && output.resources.ownershipTags.managedBy == "terraform"
    error_message = "Stable grant scopes retain their deterministic UUIDs; durable ownership gains the region tag."
  }
  assert {
    condition     = length(azurerm_network_security_group.compute) == 1 && length(azurerm_subnet_network_security_group_association.compute) == 1 && alltrue([for rule in azurerm_network_security_group.compute[0].security_rule : rule.direction != "Inbound" || rule.access != "Allow" || rule.source_address_prefix == var.gpu_subnet_cidr]) && alltrue([for rule in output.gpu_nsg_rules.rules : rule.source_ports == ["*"]])
    error_message = "Retain the owned dedicated NSG and full customer rule handoff, without public ingress."
  }
}

run "customer_managed_nsg_handoff" {
  command = plan
  module { source = "./modules/region" }
  variables {
    existing_gpu_nsg_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
  }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = { properties = {
        addressPrefix        = "10.42.1.0/26", defaultOutboundAccess = false, delegations = [],
        networkSecurityGroup = { id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg" }
      } }
    }
  }
  assert {
    condition     = length(azurerm_network_security_group.compute) == 0 && length(azurerm_subnet_network_security_group_association.compute) == 0 && output.gpu_nsg_rules.customerManaged && output.resources.networkSecurityGroupId == var.existing_gpu_nsg_id
    error_message = "Never manage, attach or replace the customer's existing NSG/association."
  }
}

run "runtime_policy_does_not_provision_compute_or_egress" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_nodes             = 3
    max_job_minutes       = 30
    manage_compute_egress = true
    egress_public_ip_tags = { FirstPartyUsage = "/Unprivileged" }
  }
  assert {
    condition     = output.resources.policy.maxNodes == 3 && output.resources.policy.maxJobMinutes == 30 && output.resources.policy.manageComputeEgress && output.resources.policy.owner == "studio" && output.resources.policy.egressPublicIpTags == var.egress_public_ip_tags
    error_message = "Configured ceilings/egress consent must reach runtime policy without adding ephemeral resources."
  }
}

run "publisher_cannot_submit_or_provision" {
  command = plan
  module { source = "./modules/region" }
  variables { publisher_principal_id = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee" }
  assert {
    condition = (length(azurerm_role_assignment.publisher) == 4 &&
      toset(azurerm_role_definition.publisher_storage_verifier[0].permissions[0].actions) == toset(["Microsoft.Storage/storageAccounts/read"]) && alltrue([
        for action in azurerm_role_definition.publisher[0].permissions[0].actions :
        !strcontains(action, "/jobs/") && !strcontains(action, "/computes/") && !strcontains(action, "roleAssignments") && !strcontains(action, "listKeys")
    ]))
    error_message = "Publisher gets release-container, registry push, asset registration and one storage metadata read; no jobs, compute, keys or IAM writes."
  }
}

run "long_deployment_names_stay_legal" {
  command = plan
  module { source = "./modules/region" }
  variables {
    deployment_name = "customer-studio1"
    legacy_names    = false
  }
  assert {
    condition = (length(output.inventory.names.vault) <= 24 && !strcontains(output.inventory.names.vault, "--") &&
      length(output.inventory.names.storage) <= 24 && can(regex("^[a-z0-9]+$", output.inventory.names.storage)) &&
    startswith(output.inventory.names.workspace, "mlw-customer-studio1-"))
    error_message = "The regional naming hash must preserve the full deployment identity and bounded storage/vault names."
  }
}

run "reject_fractional_node_ceiling" {
  command = plan
  module { source = "./modules/region" }
  variables { max_nodes = 1.5 }
  expect_failures = [var.max_nodes]
}

run "reject_legacy_nat_still_attached" {
  command = plan
  module { source = "./modules/region" }
  override_data {
    target = data.azapi_resource.gpu_subnet
    values = {
      output = { properties = {
        addressPrefix = "10.42.1.0/26", defaultOutboundAccess = false,
        natGateway    = { id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/natGateways/nat-wan-demo-14173233" }
      } }
    }
  }
  expect_failures = [terraform_data.landing_zone]
}

run "restricted_platform_egress" {
  command = plan
  module { source = "./modules/region" }
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

run "reject_unattached_customer_nsg" {
  command = plan
  module { source = "./modules/region" }
  variables {
    existing_gpu_nsg_id = run.customer_managed_nsg_handoff.resources.networkSecurityGroupId
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_different_customer_nsg" {
  command = plan
  module { source = "./modules/region" }
  variables {
    existing_gpu_nsg_id = run.customer_managed_nsg_handoff.resources.networkSecurityGroupId
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
  module { source = "./modules/region" }
  variables {
    existing_gpu_nsg_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/networkSecurityGroups/customer-nsg"
  }
  expect_failures = [var.existing_gpu_nsg_id]
}

run "reject_empty_customer_nsg" {
  command = plan
  module { source = "./modules/region" }
  variables {
    existing_gpu_nsg_id = ""
  }
  expect_failures = [var.existing_gpu_nsg_id]
}

run "reject_shared_subnet_acknowledgment" {
  command = plan
  module { source = "./modules/region" }
  variables {
    gpu_subnet_dedicated = false
  }
  expect_failures = [var.gpu_subnet_dedicated]
}

run "reject_same_gpu_and_pe_subnet" {
  command = plan
  module { source = "./modules/region" }
  variables {
    private_endpoint_subnet_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  }
  expect_failures = [var.private_endpoint_subnet_id]
}

run "reject_cross_subscription_gpu" {
  command = plan
  module { source = "./modules/region" }
  variables {
    gpu_subnet_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/wan-gpu"
  }
  expect_failures = [var.gpu_subnet_id]
}

run "reject_cross_subscription_logging" {
  command = plan
  module { source = "./modules/region" }
  variables {
    log_analytics_workspace_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-monitoring/providers/Microsoft.OperationalInsights/workspaces/customer-law"
  }
  expect_failures = [var.log_analytics_workspace_id]
}

run "reject_cross_subscription_pe" {
  command = plan
  module { source = "./modules/region" }
  variables {
    private_endpoint_subnet_id = "/subscriptions/99999999-9999-9999-9999-999999999999/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/private-endpoints"
  }
  expect_failures = [var.private_endpoint_subnet_id]
}

run "reject_cross_subscription_dns" {
  command = plan
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
  variables {
    gpu_subnet_cidr = "10.42.9.0/26"
  }
  expect_failures = [terraform_data.landing_zone]
}

run "reject_noncanonical_cidr" {
  command = plan
  module { source = "./modules/region" }
  variables {
    gpu_subnet_cidr = "10.42.1.9/26"
  }
  expect_failures = [var.gpu_subnet_cidr]
}

run "reject_implicit_outbound" {
  command = plan
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
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

run "reject_foreign_nsg" {
  command = plan
  module { source = "./modules/region" }
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

run "reject_cross_region_network" {
  command = plan
  module { source = "./modules/region" }
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
  module { source = "./modules/region" }
  variables {
    deployment_name = "not--dns"
  }
  expect_failures = [var.deployment_name]
}

run "reject_seventeen_character_deployment_name" {
  command = plan
  module { source = "./modules/region" }
  variables {
    deployment_name = "customer-studio12"
  }
  expect_failures = [var.deployment_name]
}

run "reject_nonpositive_price_limit" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_payg_hourly_usd = 0
  }
  expect_failures = [var.max_payg_hourly_usd]
}

run "accept_configurable_price_above_default" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_payg_hourly_usd = 4.01
  }
  assert {
    condition     = output.resources.policy.maxPaygHourlyUsd == 4.01
    error_message = "The $4 default is not a fixed upper bound; a finite positive configured threshold must reach the effective policy output unchanged."
  }
}

run "reject_job_timeout_above_guard" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_job_minutes = 121
  }
  expect_failures = [var.max_job_minutes]
}

run "reject_fractional_job_timeout" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_job_minutes = 1.5
  }
  expect_failures = [var.max_job_minutes]
}

run "reject_nonfinite_price_limit" {
  command = plan
  module { source = "./modules/region" }
  variables {
    max_payg_hourly_usd = pow(10, 400)
  }
  expect_failures = [var.max_payg_hourly_usd]
}
