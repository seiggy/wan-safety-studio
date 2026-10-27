# All providers are native Terraform mocks; no CLI credential or Azure call is
# permitted/needed. Fixture IDs correspond exactly to wan-demo's naming hash.
mock_provider "azurerm" {
  mock_resource "azurerm_linux_web_app" {
    defaults = {
      id               = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Web/sites/app-wan-demo-14173233"
      default_hostname = "app-wan-demo-14173233.azurewebsites.net"
    }
  }
  mock_resource "azurerm_user_assigned_identity" { defaults = {
    id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-14173233-portal",
    principal_id = "88888888-8888-8888-8888-888888888888",
    client_id    = "99999999-9999-9999-9999-999999999999",
    tenant_id    = "22222222-2222-2222-2222-222222222222"
  } }
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
  mock_data "azapi_resource_list" { defaults = { output = { value = [], nextLink = null } } }
  override_during = plan
  mock_data "azapi_resource" {
    defaults = {
      location = "eastus2"
      output   = {}
    }
  }
}

override_data {
  target = module.region["eastus2"].data.azapi_resource.gpu_subnet
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
  target = module.region["eastus2"].data.azapi_resource.private_endpoint_subnet
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
  target          = module.region["eastus2"].azurerm_user_assigned_identity.workspace
  values = {
    id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-14173233-workspace"
    principal_id = "44444444-4444-4444-4444-444444444444"
    client_id    = "55555555-5555-5555-5555-555555555555"
    tenant_id    = "22222222-2222-2222-2222-222222222222"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azurerm_user_assigned_identity.compute
  values = {
    id           = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-14173233-gpu"
    principal_id = "66666666-6666-6666-6666-666666666666"
    client_id    = "77777777-7777-7777-7777-777777777777"
    tenant_id    = "22222222-2222-2222-2222-222222222222"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.storage
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.container
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233/blobServices/default/containers/wan-studio"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.release_container
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Storage/storageAccounts/stwandemo14173233/blobServices/default/containers/wan-release"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.release_datastore
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/datastores/wan_release"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.workspace
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233"
  }
}

override_resource {
  override_during = plan
  target          = module.region["eastus2"].azapi_resource.datastore
  values = {
    id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/datastores/wan_blob"
  }
}


override_resource {
  override_during = plan
  target          = module.region["eastus2"].azurerm_private_endpoint.workspace
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
  deployment_name       = "wan-demo"
  subscription_id       = "11111111-1111-1111-1111-111111111111"
  tenant_id             = "22222222-2222-2222-2222-222222222222"
  operator_principal_id = "33333333-3333-3333-3333-333333333333"
  control_region        = "eastus2"
  legacy_region         = "eastus2"
  regions               = { eastus2 = jsondecode(file("tests/fixtures/regional-inputs.json")).regions.eastus2 }
}

run "single_region_outputs" {
  command = plan
  assert {
    condition     = length(output.regions) == 1 && output.regions.eastus2.workspaceId == jsondecode(file("tests/fixtures/studio-off.json")).workspaceId && output.regions.eastus2.storageAccountId == jsondecode(file("tests/fixtures/studio-off.json")).storageAccountId
    error_message = "The informational outputs must preserve legacy names and ownership without replacing durable resources."
  }
  assert {
    condition     = output.portal == null && length(azurerm_linux_web_app.portal) == 0 && length(azurerm_service_plan.portal) == 0 && length(azurerm_role_definition.studio_operator) == 5 && length(azurerm_role_assignment.studio_operator) == 5
    error_message = "No host by default; operator grants must cover only explicit eligibility/workspace/identity/VNet/subnet scopes."
  }
  assert {
    condition     = alltrue([for region, value in output.regions : value.policy.owner == "studio" && value.policy.minNodes == 0 && value.policy.maxNodes == 1 && value.policy.maxJobMinutes == 120 && !value.policy.manageComputeEgress && value.controlContainer == "wan-control"])
    error_message = "Default runtime policies remain OFF-compatible, one node and two hours; control storage is separate."
  }
  assert {
    condition     = !anytrue(flatten([for role in values(azurerm_role_definition.studio_operator) : [for action in role.permissions[0].actions : strcontains(action, "networkSecurityGroups") || strcontains(action, "privateDnsZones") || strcontains(action, "roleAssignments") || action == "*" || endswith(action, "subnets/write")]]))
    error_message = "Default operator roles cannot modify customer NSG/DNS/RBAC/subnet properties."
  }
}

run "greenfield_names_include_region" {
  command = plan
  variables { legacy_region = null }
  assert {
    condition     = output.regions.eastus2.workspaceId != run.single_region_outputs.regions.eastus2.workspaceId && endswith(output.regions.eastus2.workspaceId, "/mlw-wan-demo-${substr(sha256("${var.subscription_id}:wan-demo:eastus2"), 0, 8)}") && length(data.azapi_resource_list.legacy_computes) == 0
    error_message = "New regions use the regional hash and must not accidentally reuse the legacy naming scope."
  }
}

run "managed_egress_is_only_policy_and_conditional_permission" {
  command = plan
  variables {
    regions = { eastus2 = merge(jsondecode(file("tests/fixtures/regional-inputs.json")).regions.eastus2, {
      manage_compute_egress = true, max_nodes = 2, max_job_minutes = 30,
      egress_public_ip_tags = { FirstPartyUsage = "/Unprivileged" }
    }) }
  }
  assert {
    condition     = output.regions.eastus2.policy.maxNodes == 2 && output.regions.eastus2.policy.maxJobMinutes == 30 && output.regions.eastus2.policy.manageComputeEgress && contains(azurerm_role_definition.studio_operator["eastus2:subnet"].permissions[0].actions, "Microsoft.Network/virtualNetworks/subnets/write") && azurerm_role_assignment.studio_operator["eastus2:subnet:operator"].scope == var.regions.eastus2.gpu_subnet_id
    error_message = "Only explicit owned-egress consent grants write on the exact dedicated subnet; ceilings do not allocate compute."
  }
}

run "reject_runtime_compute_in_terraform" {
  command = plan
  variables {
    regions = { eastus2 = merge(jsondecode(file("tests/fixtures/regional-inputs.json")).regions.eastus2, { compute_enabled = true }) }
  }
  expect_failures = [var.regions]
}
run "reject_unknown_control_region" {
  command = plan
  variables { control_region = "westeurope" }
  expect_failures = [var.control_region]
}
run "reject_legacy_host_relocation" {
  command = plan
  variables { legacy_region = "westeurope" }
  expect_failures = [var.legacy_region]
}
run "reject_legacy_compute_still_present" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_computes[0]
    values = { output = { value = [{ id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-14173233/computes/wan-gpu" }] } }
  }

  expect_failures = [terraform_data.legacy_handoff]
}

run "reject_running_legacy_host" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  override_data {
    target = data.azapi_resource.portal_subnet[0]
    values = { output = { properties = { delegations = [{ properties = { serviceName = "Microsoft.Web/serverFarms" } }] } } }
  }
  override_data {
    target = data.azapi_resource_list.legacy_sites[0]
    values = { output = { value = [{
      id         = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Web/sites/app-wan-demo-14173233"
      properties = { state = "Running" }
    }] } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_queued_legacy_job" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_jobs[0]
    values = { output = { value = [{ properties = { computeId = "wan-gpu", status = "Queued" } }] } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_unknown_legacy_job" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_jobs[0]
    values = { output = { value = [{ properties = { computeId = "wan-gpu" } }] } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_legacy_nat_still_present" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_nat[0]
    values = { output = { value = [{ id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/natGateways/nat-wan-demo-14173233" }] } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_legacy_pip_still_present" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_pip[0]
    values = { output = { value = [{ id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Network/publicIPAddresses/pip-wan-demo-14173233-egress" }] } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_incomplete_inventory" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_jobs[0]
    values = { output = { value = [], nextLink = "https://management.azure.com/next-page" } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "reject_malformed_inventory" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_computes[0]
    values = { output = { value = {} } }
  }
  expect_failures = [terraform_data.legacy_handoff]
}
run "terminal_jobs_allow_off_handoff" {
  command = plan
  override_data {
    target = data.azapi_resource_list.legacy_jobs[0]
    values = { output = { value = [{ properties = { computeId = "wan-gpu", status = "Completed" } }, { properties = { computeId = "wan-gpu", status = "Canceled" } }] } }
  }
  assert {
    condition     = output.regions.eastus2.policy.owner == "studio"
    error_message = "Terminal job history must not cause migration to invent or resubmit work."
  }
}

run "one_private_shared_host" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  override_data {
    target = data.azapi_resource.portal_subnet[0]
    values = { output = { properties = { delegations = [{ properties = { serviceName = "Microsoft.Web/serverFarms" } }] } } }
  }
  override_data {
    target = data.azapi_resource_list.legacy_sites[0]
    values = { output = { value = [{
      id         = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-14173233/providers/Microsoft.Web/sites/app-wan-demo-14173233"
      properties = { state = "Stopped" }
    }] } }
  }
  assert {
    condition     = length(azurerm_linux_web_app.portal) == 1 && length(azurerm_service_plan.portal) == 1 && azurerm_linux_web_app.portal[0].name == "app-wan-demo-14173233" && azurerm_linux_web_app.portal[0].resource_group_name == "rg-wan-demo-14173233" && !azurerm_linux_web_app.portal[0].public_network_access_enabled && azurerm_linux_web_app.portal[0].https_only && azurerm_linux_web_app.portal[0].virtual_network_subnet_id == var.portal.subnet_id
    error_message = "Keep the existing one shared private host and its address/name; do not recreate one per region."
  }
  assert {
    condition     = azurerm_linux_web_app.portal[0].site_config[0].container_registry_use_managed_identity && azurerm_service_plan.portal[0].worker_count == 1 && !azurerm_linux_web_app.portal[0].ftp_publish_basic_authentication_enabled && !azurerm_linux_web_app.portal[0].webdeploy_publish_basic_authentication_enabled
    error_message = "The host stays single-instance and managed-identity-only."
  }
  assert {
    condition     = toset(keys(azurerm_linux_web_app.portal[0].app_settings)) == toset(["WEBSITES_PORT", "WEBSITES_ENABLE_APP_SERVICE_STORAGE"]) && !anytrue(flatten([for key in keys(azurerm_linux_web_app.portal[0].app_settings) : [for prefix in ["Studio__", "AzureMl__", "Auth__", "Generation__", "FEATURE_", "Agent__", "KeyVault__", "AZURE_CLIENT_ID", "ASPNETCORE_"] : startswith(key, prefix)]]))
    error_message = "Terraform sets only App Service platform settings on the host, never any studio application setting."
  }
  assert {
    condition     = endswith(azurerm_role_assignment.portal["storage_studio"].scope, "/containers/wan-studio") && endswith(azurerm_role_assignment.regional_portal["eastus2:release_read"].role_definition_id, "/2a2b9908-6ea1-4ae2-8e65-a410df84e7d1") && endswith(azurerm_role_assignment.control["host"].scope, "/containers/wan-control") && azurerm_private_endpoint.portal[0].private_dns_zone_group[0].private_dns_zone_ids == tolist([var.portal.dns_zone_id])
    error_message = "Run writes, release reads, operator control writes and supplied App Service DNS stay separated."
  }
}

run "reject_undelegated_shared_host" {
  command = plan
  variables {
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  expect_failures = [azurerm_service_plan.portal]
}

run "two_region_outputs" {
  command = plan
  variables {
    regions                = jsondecode(file("tests/fixtures/regional-inputs.json")).regions
    publisher_principal_id = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
    portal = {
      subnet_id   = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/customer-spoke/subnets/portal"
      dns_zone_id = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/customer-dns/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
    }
  }
  override_data {
    target = data.azapi_resource.portal_subnet[0]
    values = { output = { properties = { delegations = [{ properties = { serviceName = "Microsoft.Web/serverFarms" } }] } } }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_resource_group.studio
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636" }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_container_registry.studio
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.ContainerRegistry/registries/crwandemoc9d87636", "login_server" : "crwandemoc9d87636.azurecr.io", "data_endpoint_host_names" : ["crwandemoc9d87636.westeurope.data.azurecr.io"] }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_key_vault.studio
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.KeyVault/vaults/kv-wan-demo-c9d87636", "vault_uri" : "https://kv-wan-demo-c9d87636.vault.azure.net/" }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_application_insights.studio
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.Insights/components/appi-wan-demo-c9d87636" }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_user_assigned_identity.workspace
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-c9d87636-workspace", "principal_id" : "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "client_id" : "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "tenant_id" : "22222222-2222-2222-2222-222222222222" }
  }
  override_resource {
    target          = module.region["westeurope"].azurerm_user_assigned_identity.compute
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-c9d87636-gpu", "principal_id" : "cccccccc-cccc-cccc-cccc-cccccccccccc", "client_id" : "dddddddd-dddd-dddd-dddd-dddddddddddd", "tenant_id" : "22222222-2222-2222-2222-222222222222" }
  }
  override_resource {
    target          = module.region["westeurope"].azapi_resource.storage
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.Storage/storageAccounts/stwandemoc9d87636" }
  }
  override_resource {
    target          = module.region["westeurope"].azapi_resource.container
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.Storage/storageAccounts/stwandemoc9d87636/blobServices/default/containers/wan-studio" }
  }
  override_resource {
    target          = module.region["westeurope"].azapi_resource.release_container
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.Storage/storageAccounts/stwandemoc9d87636/blobServices/default/containers/wan-release" }
  }
  override_resource {
    target          = module.region["westeurope"].azapi_resource.workspace
    override_during = plan
    values          = { "id" : "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-c9d87636/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-c9d87636" }
  }
  override_data {
    target = module.region["westeurope"].data.azapi_resource.gpu_subnet
    values = { "output" : { "properties" : { "addressPrefix" : "10.43.1.0/26", "defaultOutboundAccess" : false, "delegations" : [] } } }
  }
  override_data {
    target = module.region["westeurope"].data.azapi_resource.private_endpoint_subnet
    values = { "output" : { "properties" : { "addressPrefix" : "10.43.2.0/26" } } }
  }
  override_data {
    target = module.region["westeurope"].data.azapi_resource.gpu_vnet
    values = { "location" : "westeurope" }
  }
  override_data {
    target = module.region["westeurope"].data.azapi_resource.private_endpoint_vnet
    values = { "location" : "westeurope" }
  }
  assert {
    condition     = length(output.regions) == 2 && output.regions.eastus2.workspaceId != output.regions.westeurope.workspaceId && output.regions.westeurope.policy.maxJobMinutes == 30 && output.regions.westeurope.policy.maxNodes == 2 && endswith(output.regions.westeurope.workspaceId, "/mlw-wan-demo-c9d87636")
    error_message = "Independent regional names, timeouts, node ceilings and stores must reach the informational regional outputs."
  }
  assert {
    condition     = length(azurerm_role_definition.studio_operator) == 9 && length(azurerm_role_assignment.studio_operator) == 18 && length(azurerm_linux_web_app.portal) == 1 && length(azurerm_role_definition.regional_job_submitter) == 1 && length(azurerm_role_assignment.regional_portal) == 5 && alltrue([for region, value in output.regions : value.policy.owner == "studio" && value.policy.minNodes == 0 && value.controlContainer == "wan-control"])
    error_message = "Adding a region adds scoped policy/permissions, never compute or a second host."
  }
}
