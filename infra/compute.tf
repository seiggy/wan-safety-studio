locals {
  gpu_rules = concat([
    { name = "allow-node-internal-inbound", priority = 100, direction = "Inbound", protocol = "*", source = var.gpu_subnet_cidr, destination = var.gpu_subnet_cidr, ports = ["*"], access = "Allow" },
    { name = "deny-other-inbound", priority = 4096, direction = "Inbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
    ], [
    for index, cidr in local.pe_cidrs : { name = "allow-private-services-${index}", priority = 100 + index, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = cidr, ports = ["443", "445"], access = "Allow" }
    ], [
    { name = "allow-aml", priority = 200, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureMachineLearning", ports = ["443", "8787", "18881"], access = "Allow" },
    { name = "allow-aml-tundra", priority = 210, direction = "Outbound", protocol = "Udp", source = var.gpu_subnet_cidr, destination = "AzureMachineLearning", ports = ["5831"], access = "Allow" },
    { name = "allow-batch", priority = 220, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = "BatchNodeManagement", ports = ["443"], access = "Allow" },
    { name = "allow-entra", priority = 230, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureActiveDirectory", ports = ["80", "443"], access = "Allow" },
    { name = "allow-storage", priority = 240, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "Storage", ports = ["443"], access = "Allow" },
    { name = "allow-vault", priority = 250, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureKeyVault", ports = ["443"], access = "Allow" },
    { name = "allow-registry", priority = 260, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureContainerRegistry", ports = ["443"], access = "Allow" },
    { name = "allow-arm", priority = 270, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureResourceManager", ports = ["443"], access = "Allow" },
    { name = "allow-public-https", priority = 280, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "Internet", ports = ["443"], access = "Allow" },
    { name = "allow-managed-identity", priority = 290, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "169.254.169.254/32", ports = ["80"], access = "Allow" },
    { name = "allow-private-dns", priority = 300, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = "VirtualNetwork", ports = ["53"], access = "Allow" },
    { name = "allow-node-internal-outbound", priority = 310, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = var.gpu_subnet_cidr, ports = ["*"], access = "Allow" },
    { name = "deny-other-outbound", priority = 4096, direction = "Outbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
  ])
}

resource "azurerm_network_security_group" "compute" {
  name                = local.names.nsg
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  tags                = local.ownership_tags

  dynamic "security_rule" {
    for_each = { for rule in local.gpu_rules : rule.name => rule }
    content {
      name                       = security_rule.value.name
      priority                   = security_rule.value.priority
      direction                  = security_rule.value.direction
      protocol                   = security_rule.value.protocol
      access                     = security_rule.value.access
      source_address_prefix      = security_rule.value.source
      destination_address_prefix = security_rule.value.destination
      source_port_range          = "*"
      destination_port_range     = length(security_rule.value.ports) == 1 ? security_rule.value.ports[0] : null
      destination_port_ranges    = length(security_rule.value.ports) > 1 ? security_rule.value.ports : null
    }
  }
}

resource "azurerm_subnet_network_security_group_association" "compute" {
  subnet_id                 = var.gpu_subnet_id
  network_security_group_id = azurerm_network_security_group.compute.id
  depends_on                = [terraform_data.landing_zone]
}

resource "azurerm_public_ip" "compute" {
  count               = local.manage_egress ? 1 : 0
  name                = local.names.pip
  resource_group_name = azurerm_resource_group.studio.name
  location            = var.location
  sku                 = "Standard"
  allocation_method   = "Static"
  ip_version          = "IPv4"
  tags                = local.ownership_tags
}

resource "azurerm_nat_gateway" "compute" {
  count                   = local.manage_egress ? 1 : 0
  name                    = local.names.nat
  resource_group_name     = azurerm_resource_group.studio.name
  location                = var.location
  sku_name                = "Standard"
  idle_timeout_in_minutes = 4
  tags                    = local.ownership_tags
}

resource "azurerm_nat_gateway_public_ip_association" "compute" {
  count                = local.manage_egress ? 1 : 0
  nat_gateway_id       = azurerm_nat_gateway.compute[0].id
  public_ip_address_id = azurerm_public_ip.compute[0].id
}

resource "azurerm_subnet_nat_gateway_association" "compute" {
  count          = local.manage_egress ? 1 : 0
  subnet_id      = var.gpu_subnet_id
  nat_gateway_id = azurerm_nat_gateway.compute[0].id
  depends_on = [
    terraform_data.landing_zone,
    azurerm_nat_gateway_public_ip_association.compute,
    azurerm_subnet_network_security_group_association.compute
  ]
}

resource "azapi_resource" "compute" {
  count     = var.compute_enabled ? 1 : 0
  type      = "Microsoft.MachineLearningServices/workspaces/computes@2024-10-01"
  name      = local.names.compute
  parent_id = azapi_resource.workspace.id
  location  = var.location
  tags      = local.ownership_tags
  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.compute.id]
  }
  body = {
    properties = {
      computeType      = "AmlCompute"
      computeLocation  = var.location
      disableLocalAuth = true
      description      = "Single Spot A100; no dedicated/serverless fallback."
      properties = {
        vmSize                      = "Standard_NC24ads_A100_v4"
        vmPriority                  = "LowPriority"
        osType                      = "Linux"
        enableNodePublicIp          = false
        remoteLoginPortPublicAccess = "Disabled"
        subnet                      = { id = var.gpu_subnet_id }
        scaleSettings = {
          minNodeCount                = 0
          maxNodeCount                = 1
          nodeIdleTimeBeforeScaleDown = "PT120S"
        }
      }
    }
  }
  # Changing egress mode replaces compute, never the customer subnet. Change
  # egress configuration only while OFF. Normal OFF destroys compute before NAT.
  replace_triggers_external_values = [var.manage_compute_egress, var.gpu_subnet_id]
  depends_on = [
    azurerm_role_assignment.service,
    azapi_resource.datastore,
    azurerm_private_endpoint.workspace,
    azurerm_subnet_network_security_group_association.compute,
    azurerm_subnet_nat_gateway_association.compute
  ]
}
