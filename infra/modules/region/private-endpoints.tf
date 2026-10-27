locals {
  service_endpoints = {
    blob     = { id = azapi_resource.storage.id, group = "blob", zone = var.private_dns_zone_ids.blob }
    file     = { id = azapi_resource.storage.id, group = "file", zone = var.private_dns_zone_ids.file }
    vault    = { id = azurerm_key_vault.studio.id, group = "vault", zone = var.private_dns_zone_ids.vault }
    registry = { id = azurerm_container_registry.studio.id, group = "registry", zone = var.private_dns_zone_ids.registry }
  }
}

resource "azurerm_private_endpoint" "service" {
  for_each            = local.service_endpoints
  name                = "pe-${local.stem}-${each.key}"
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  subnet_id           = var.private_endpoint_subnet_id
  tags                = local.ownership_tags
  private_service_connection {
    name                           = "psc-${each.key}"
    private_connection_resource_id = each.value.id
    subresource_names              = [each.value.group]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [each.value.zone]
  }
}

resource "azurerm_private_endpoint" "workspace" {
  name                = "pe-${local.stem}-workspace"
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  subnet_id           = var.private_endpoint_subnet_id
  tags                = local.ownership_tags
  private_service_connection {
    name                           = "psc-workspace"
    private_connection_resource_id = azapi_resource.workspace.id
    subresource_names              = ["amlworkspace"]
    is_manual_connection           = false
  }
  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [var.private_dns_zone_ids.api, var.private_dns_zone_ids.notebooks]
  }
}
