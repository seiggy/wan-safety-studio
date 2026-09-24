# Optional private App Service host for the same Entra-gated portal. Inbound is
# only its private endpoint; outbound VNet integration reaches the studio's
# private endpoints (RFC1918 only; Entra/ARM stay on platform outbound).
# The customer owns the delegated subnet, its NSG/routes and DNS forwarding.
locals {
  portal_enabled = var.portal != null
  portal_grants = local.portal_enabled ? {
    storage   = { scope = azapi_resource.storage.id, role = local.role_ids.blob_contributor }
    workspace = { scope = azapi_resource.workspace.id, role = local.role_ids.ml_scientist }
  } : {}
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
  tags                                           = local.ownership_tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.portal[0].id]
  }

  site_config {
    always_on                         = true
    ftps_state                        = "Disabled"
    minimum_tls_version               = "1.2"
    scm_minimum_tls_version           = "1.2"
    http2_enabled                     = true
    health_check_path                 = "/healthz"
    health_check_eviction_time_in_min = 2
    vnet_route_all_enabled            = false
    worker_count                      = 1
    app_command_line                  = "python /home/site/wwwroot/runtime.py --cache /tmp/wan-studio portal --profile wan --hosted"
    application_stack {
      python_version = "3.12"
    }
  }

  # No secrets: MSAL uses a federated credential from this managed identity.
  # Start/Stop own WAN_STUDIO_ARMED through ARM; Terraform must not disarm/rearm it.
  app_settings = {
    SCM_DO_BUILD_DURING_DEPLOYMENT        = "false"
    ENABLE_ORYX_BUILD                     = "false"
    PYTHONPATH                            = "/home/site/wwwroot/packages"
    WAN_STUDIO_CONFIG                     = "/home/site/wwwroot/release/config.json"
    WAN_STUDIO_FOUNDATION                 = "/home/site/wwwroot/release/foundation.json"
    WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID = azurerm_user_assigned_identity.portal[0].client_id
  }

  lifecycle {
    ignore_changes = [app_settings["WAN_STUDIO_ARMED"]]
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
