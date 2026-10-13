# Optional private App Service host for the .NET studio server, run as a Linux
# container pulled from the studio registry. Inbound is only its private
# endpoint; outbound VNet integration reaches the studio's private endpoints
# (RFC1918 only; Entra/ARM stay on platform outbound).
# The customer owns the delegated subnet, its NSG/routes and DNS forwarding.
locals {
  portal_enabled = var.portal != null
  # Cognitive Services OpenAI User
  openai_user_role = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"
  # Key Vault Secrets User
  key_vault_secrets_user_role = "4633458b-17de-408a-b874-0445c86b69e6"
  portal_agent                = local.portal_enabled ? var.agent : null
  portal_grants = local.portal_enabled ? merge(
    {
      storage_studio = { scope = azapi_resource.container.id, role = local.role_ids.blob_contributor }
      workspace      = { scope = azapi_resource.workspace.id, role = one(azurerm_role_definition.portal_job_submitter[*].role_definition_id) }
      registry       = { scope = azurerm_container_registry.studio.id, role = local.role_ids.acr_pull }
    },
    try(local.portal_agent.resource_id, null) == null ? {} : {
      agent = { scope = local.portal_agent.resource_id, role = local.openai_user_role }
    },
    var.key_vault_id == null ? {} : {
      key_vault = { scope = var.key_vault_id, role = local.key_vault_secrets_user_role }
    }
  ) : {}

  # Flat resource-connection settings for the server (docs/configuration.md). The site cannot
  # reference its own hostname, so Publish sets Auth__PublicOrigin; Publish also sets
  # AzureMl__Release__* and Auth__ClientId, and Start/Stop own AzureMl__Armed.
  portal_connection_settings = {
    AzureMl__SubscriptionId          = lower(var.subscription_id)
    AzureMl__TenantId                = lower(var.tenant_id)
    AzureMl__ResourceGroup           = azurerm_resource_group.studio.name
    AzureMl__Workspace               = azapi_resource.workspace.name
    AzureMl__Compute                 = local.names.compute
    AzureMl__ComputeIdentityClientId = azurerm_user_assigned_identity.compute.client_id
    AzureMl__StorageAccount          = azapi_resource.storage.name
    AzureMl__Container               = azapi_resource.container.name
    AzureMl__Datastore               = azapi_resource.datastore.name
    AzureMl__ReleaseContainer        = azapi_resource.release_container.name
    AzureMl__ReleaseDatastore        = azapi_resource.release_datastore.name
    Auth__TenantId                   = lower(var.tenant_id)
  }
  portal_agent_settings = local.portal_agent == null ? {} : merge(
    {
      Agent__Endpoint = local.portal_agent.endpoint
      Agent__Model    = local.portal_agent.model
      Agent__Auth     = local.portal_agent.auth
    },
    local.portal_agent.auth == "UserAssignedManagedIdentity" ? { Agent__ManagedIdentityClientId = azurerm_user_assigned_identity.portal[0].client_id } : {},
    local.portal_agent.auth == "ApiKey" ? { Agent__ApiKey = "@Microsoft.KeyVault(SecretUri=${local.portal_agent.api_key_secret_uri})" } : {},
    local.portal_agent.wire_api == null ? {} : { Agent__WireApi = local.portal_agent.wire_api },
    local.portal_agent.vision == null ? {} : { Agent__Vision = tostring(local.portal_agent.vision) }
  )
}

data "azapi_resource" "portal_subnet" {
  count                  = local.portal_enabled ? 1 : 0
  type                   = "Microsoft.Network/virtualNetworks/subnets@2024-05-01"
  resource_id            = var.portal.subnet_id
  response_export_values = ["properties"]
}

resource "azurerm_user_assigned_identity" "portal" {
  count               = local.portal_enabled ? 1 : 0
  name                = local.names.portal_identity
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  tags                = local.ownership_tags
}

# Least-privilege replacement for the built-in AzureML Data Scientist: submit/read/cancel jobs only.
# No datastore, environment, model, data or code asset writes, so the portal cannot repoint
# the release datastore or register its own assets. Mirrors the built-in notActions for compute/keys.
resource "azurerm_role_definition" "portal_job_submitter" {
  count              = local.portal_enabled ? 1 : 0
  name               = "wan-portal-job-submitter"
  role_definition_id = uuidv5("url", lower("${azapi_resource.workspace.id}/wan-portal-job-submitter"))
  scope              = azapi_resource.workspace.id
  description        = "Submit, read and cancel Azure ML jobs on the WAN Safety Studio workspace. Cannot modify datastores, environments, models, data or code assets."

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
      "Microsoft.MachineLearningServices/workspaces/experiments/*",
      "Microsoft.MachineLearningServices/workspaces/metadata/*",
      "Microsoft.MachineLearningServices/workspaces/datasets/read",
       "Microsoft.MachineLearningServices/workspaces/datasets/registered/read",
       "Microsoft.MachineLearningServices/workspaces/datasets/registered/write",
       "Microsoft.MachineLearningServices/workspaces/datasets/unregistered/read",
       "Microsoft.MachineLearningServices/workspaces/datasets/unregistered/write",
    ]
    not_actions = [
      "Microsoft.MachineLearningServices/workspaces/write",
      "Microsoft.MachineLearningServices/workspaces/delete",
      "Microsoft.MachineLearningServices/workspaces/listKeys/action",
      "Microsoft.MachineLearningServices/workspaces/computes/*/write",
      "Microsoft.MachineLearningServices/workspaces/computes/*/delete",
      "Microsoft.MachineLearningServices/workspaces/computes/listKeys/action",
      "Microsoft.MachineLearningServices/workspaces/datastores/write",
      "Microsoft.MachineLearningServices/workspaces/datastores/delete",
      "Microsoft.MachineLearningServices/workspaces/datastores/listSecrets/action",
      "Microsoft.MachineLearningServices/workspaces/environments/write",
      "Microsoft.MachineLearningServices/workspaces/environments/versions/write",
      "Microsoft.MachineLearningServices/workspaces/environments/versions/delete",
      "Microsoft.MachineLearningServices/workspaces/models/write",
      "Microsoft.MachineLearningServices/workspaces/models/delete",
      "Microsoft.MachineLearningServices/workspaces/models/versions/write",
      "Microsoft.MachineLearningServices/workspaces/models/versions/delete",
      "Microsoft.MachineLearningServices/workspaces/data/write",
      "Microsoft.MachineLearningServices/workspaces/data/delete",
      "Microsoft.MachineLearningServices/workspaces/codes/write",
      "Microsoft.MachineLearningServices/workspaces/codes/delete",
      "Microsoft.MachineLearningServices/workspaces/metadata/listsecrets/action",
    ]
  }

  assignable_scopes = [azapi_resource.workspace.id]
}

resource "azurerm_role_assignment" "portal" {
  for_each                         = local.portal_grants
  name                             = uuidv5("url", lower("${each.value.scope}/${azurerm_user_assigned_identity.portal[0].principal_id}/${each.value.role}"))
  scope                            = each.value.scope
  role_definition_id               = "/subscriptions/${lower(var.subscription_id)}/providers/Microsoft.Authorization/roleDefinitions/${each.value.role}"
  principal_id                     = azurerm_user_assigned_identity.portal[0].principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

resource "azurerm_service_plan" "portal" {
  count               = local.portal_enabled ? 1 : 0
  name                = local.names.portal_plan
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.portal.sku
  # ponytail: sessions are process-local; one instance until a shared store exists.
  worker_count = 1
  tags         = local.ownership_tags

  lifecycle {
    precondition {
      condition     = anytrue([for delegation in try(data.azapi_resource.portal_subnet[0].output.properties.delegations, []) : delegation.properties.serviceName == "Microsoft.Web/serverFarms"])
      error_message = "portal.subnet_id must already be delegated to Microsoft.Web/serverFarms by the network owner. This module never modifies customer subnets."
    }
  }
}

resource "azurerm_linux_web_app" "portal" {
  count                                          = local.portal_enabled ? 1 : 0
  name                                           = local.names.portal
  resource_group_name                            = azurerm_resource_group.studio.name
  location                                       = var.location
  service_plan_id                                = azurerm_service_plan.portal[0].id
  https_only                                     = true
  public_network_access_enabled                  = false
  virtual_network_subnet_id                      = var.portal.subnet_id
  client_affinity_enabled                        = false
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false
  vnet_image_pull_enabled                        = true
  key_vault_reference_identity_id                = azurerm_user_assigned_identity.portal[0].id
  tags                                           = local.ownership_tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.portal[0].id]
  }

  site_config {
    # The registry is private: pull over the VNet with the portal identity (AcrPull).
    container_registry_use_managed_identity       = true
    container_registry_managed_identity_client_id = azurerm_user_assigned_identity.portal[0].client_id
    always_on                                     = true
    ftps_state                                    = "Disabled"
    minimum_tls_version                           = "1.2"
    scm_minimum_tls_version                       = "1.2"
    http2_enabled                                 = true
    health_check_path                             = "/healthz"
    health_check_eviction_time_in_min             = 2
    vnet_route_all_enabled                        = false
    worker_count                                  = 1
    # The placeholder tag never exists: Publish pushes the real image and points the site at it.
    application_stack {
      docker_image_name   = "wan-safety-studio-server:pending"
      docker_registry_url = "https://${azurerm_container_registry.studio.login_server}"
    }
  }

  # No secrets: sign-in uses a federated credential from this managed identity.
  # Start/Stop own AzureMl__Armed; Publish owns AzureMl__Release__*, Auth__ClientId,
  # Auth__PublicOrigin and the image through ARM; Terraform must not disarm/rearm or revert them.
  app_settings = merge(
    {
      WEBSITES_PORT                       = "8080"
      WEBSITES_ENABLE_APP_SERVICE_STORAGE = "true"
      ASPNETCORE_ENVIRONMENT              = "Production"
      Generation__Provider                = "azureml"
      AZURE_CLIENT_ID                     = azurerm_user_assigned_identity.portal[0].client_id
      FEATURE_DATASET_BUILDER_ENABLED     = tostring(var.feature_dataset_builder_enabled)
    },
    local.portal_connection_settings,
    var.key_vault_uri == null ? {} : { KeyVault__Uri = var.key_vault_uri },
    local.portal_agent_settings
  )

  lifecycle {
    ignore_changes = [
      app_settings["AzureMl__Armed"],
      app_settings["AzureMl__Release__EnvironmentId"],
      app_settings["AzureMl__Release__ModelsRef"],
      app_settings["AzureMl__Release__CodeUri"],
      app_settings["Auth__ClientId"],
      app_settings["Auth__PublicOrigin"],
      site_config[0].application_stack,
    ]
  }
  depends_on = [azurerm_role_assignment.portal]
}

resource "azurerm_private_endpoint" "portal" {
  count               = local.portal_enabled ? 1 : 0
  name                = "pe-${local.stem}-portal"
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  subnet_id           = var.private_endpoint_subnet_id
  tags                = local.ownership_tags
  private_service_connection {
    name                           = "psc-portal"
    private_connection_resource_id = azurerm_linux_web_app.portal[0].id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [var.portal.dns_zone_id]
  }
}
