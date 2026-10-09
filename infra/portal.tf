# Optional private App Service host for the .NET studio server, run as a Linux
# container pulled from the studio registry. Inbound is only its private
# endpoint; outbound VNet integration reaches the studio's private endpoints
# (RFC1918 only; Entra/ARM stay on platform outbound).
# The customer owns the delegated subnet, its NSG/routes and DNS forwarding.
locals {
  portal_enabled = var.portal != null
  # Cognitive Services OpenAI User
  openai_user_role = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd"
  portal_agent     = local.portal_enabled ? var.agent : null
  portal_grants = local.portal_enabled ? merge(
    {
      storage   = { scope = azapi_resource.storage.id, role = local.role_ids.blob_contributor }
      workspace = { scope = azapi_resource.workspace.id, role = local.role_ids.ml_scientist }
      registry  = { scope = azurerm_container_registry.studio.id, role = local.role_ids.acr_pull }
    },
    try(local.portal_agent.resource_id, null) == null ? {} : {
      agent = { scope = local.portal_agent.resource_id, role = local.openai_user_role }
    }
  ) : {}

  # Foundation receipt subset the server needs. portal.hostname is omitted on
  # purpose (the site cannot reference its own hostname); Publish sets
  # WAN_STUDIO_PUBLIC_ORIGIN from the real default hostname instead.
  portal_foundation = {
    subscriptionId          = lower(var.subscription_id)
    tenantId                = lower(var.tenant_id)
    resourceGroupName       = azurerm_resource_group.studio.name
    workspaceName           = azapi_resource.workspace.name
    computeName             = local.names.compute
    computeId               = local.ids.compute
    computeIdentityClientId = azurerm_user_assigned_identity.compute.client_id
    storageAccountName      = azapi_resource.storage.name
    containerName           = azapi_resource.container.name
    datastoreName           = azapi_resource.datastore.name
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
  # Start/Stop own WAN_STUDIO_ARMED and Publish owns WAN_STUDIO_RELEASE_JSON,
  # WAN_STUDIO_PORTAL_CLIENT_ID, WAN_STUDIO_PUBLIC_ORIGIN and the image through ARM;
  # Terraform must not disarm/rearm or revert them.
  app_settings = merge(
    {
      WEBSITES_PORT                         = "8080"
      WEBSITES_ENABLE_APP_SERVICE_STORAGE   = "true"
      ASPNETCORE_ENVIRONMENT                = "Production"
      Generation__Provider                  = "azureml"
      WAN_STUDIO_FOUNDATION_JSON            = jsonencode(local.portal_foundation)
      WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID = azurerm_user_assigned_identity.portal[0].client_id
      AZURE_CLIENT_ID                       = azurerm_user_assigned_identity.portal[0].client_id
      FEATURE_DATASET_BUILDER_ENABLED       = tostring(var.feature_dataset_builder_enabled)
    },
    local.portal_agent_settings
  )

  lifecycle {
    ignore_changes = [
      app_settings["WAN_STUDIO_ARMED"],
      app_settings["WAN_STUDIO_RELEASE_JSON"],
      app_settings["WAN_STUDIO_PORTAL_CLIENT_ID"],
      app_settings["WAN_STUDIO_PUBLIC_ORIGIN"],
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
