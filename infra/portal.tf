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
  # Only the identity grants live here. The application's own settings are set by the operator, never by Terraform.
  portal_agent = local.portal_enabled ? var.agent : null
  portal_grants = local.portal_enabled ? merge(
    {
      storage_studio = { scope = "${local.ids.storage}/blobServices/default/containers/${local.names.container}", role = local.role_ids.blob_contributor }
      workspace      = { scope = local.ids.workspace, role = one(azurerm_role_definition.portal_job_submitter[*].role_definition_id) }
      registry       = { scope = local.ids.registry, role = local.role_ids.acr_pull }
    },
    try(local.portal_agent.resource_id, null) == null ? {} : {
      agent = { scope = local.portal_agent.resource_id, role = local.openai_user_role }
    },
    var.key_vault_id == null ? {} : {
      key_vault = { scope = var.key_vault_id, role = local.key_vault_secrets_user_role }
    }
  ) : {}
}

data "azapi_resource" "portal_subnet" {
  count                  = local.portal_enabled ? 1 : 0
  type                   = "Microsoft.Network/virtualNetworks/subnets@2024-05-01"
  resource_id            = var.portal.subnet_id
  response_export_values = ["properties"]
}

data "azapi_resource" "portal_vnet" {
  count                  = local.portal_enabled ? 1 : 0
  type                   = "Microsoft.Network/virtualNetworks@2024-05-01"
  resource_id            = join("/", slice(split("/", var.portal.subnet_id), 0, 9))
  response_export_values = ["location"]
}

resource "azurerm_user_assigned_identity" "portal" {
  count               = local.portal_enabled ? 1 : 0
  name                = local.names.portal_identity
  resource_group_name = local.names.resource_group
  location            = var.control_region
  tags                = local.ownership_tags
  depends_on          = [module.region]
  lifecycle {
    prevent_destroy = true
  }
}

# Least-privilege replacement for the built-in AzureML Data Scientist: submit/read/cancel jobs only.
# No datastore, environment, model, data or code asset writes, so the portal cannot repoint
# the release datastore or register its own assets. Mirrors the built-in notActions for compute/keys.
resource "azurerm_role_definition" "portal_job_submitter" {
  count              = local.portal_enabled ? 1 : 0
  name               = var.legacy_region == null ? "wan-submitter-${local.names.workspace}" : "wan-portal-job-submitter"
  role_definition_id = uuidv5("url", lower("${local.ids.workspace}/wan-portal-job-submitter"))
  scope              = local.ids.workspace
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

  assignable_scopes = [local.ids.workspace]
  depends_on        = [module.region]
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
  resource_group_name = local.names.resource_group
  location            = var.control_region
  os_type             = "Linux"
  sku_name            = var.portal.sku
  # ponytail: sessions are process-local; one instance until a shared store exists.
  worker_count = 1
  tags         = local.ownership_tags

  lifecycle {
    prevent_destroy = true
    precondition {
      condition     = anytrue([for delegation in try(data.azapi_resource.portal_subnet[0].output.properties.delegations, []) : delegation.properties.serviceName == "Microsoft.Web/serverFarms"])
      error_message = "portal.subnet_id must already be delegated to Microsoft.Web/serverFarms by the network owner. This module never modifies customer subnets."
    }
    precondition {
      condition     = lower(data.azapi_resource.portal_vnet[0].location) == var.control_region
      error_message = "The shared App Service integration subnet must be in control_region."
    }
  }
}

resource "azurerm_linux_web_app" "portal" {
  count                                          = local.portal_enabled ? 1 : 0
  name                                           = local.names.portal
  resource_group_name                            = local.names.resource_group
  location                                       = var.control_region
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
      docker_registry_url = "https://${local.names.registry}.azurecr.io"
    }
  }

  # Only platform/runtime settings the container needs. Everything the studio application reads is set by the
  # operator on the site; Terraform ignores app_settings so a later apply never overwrites or removes them.
  app_settings = {
    WEBSITES_PORT                       = "8080"
    WEBSITES_ENABLE_APP_SERVICE_STORAGE = "true"
  }

  lifecycle {
    prevent_destroy = true
    ignore_changes = [
      app_settings,
      site_config[0].application_stack,
    ]
  }
  depends_on = [azurerm_role_assignment.portal]
}

resource "azurerm_private_endpoint" "portal" {
  count               = local.portal_enabled ? 1 : 0
  name                = "pe-${local.stem}-portal"
  resource_group_name = local.names.resource_group
  location            = var.control_region
  subnet_id           = var.regions[var.control_region].private_endpoint_subnet_id
  tags                = local.ownership_tags
  lifecycle {
    prevent_destroy = true
  }
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
