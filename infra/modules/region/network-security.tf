locals {
  gpu_rules = concat([
    { name = "allow-node-internal-inbound", priority = 100, direction = "Inbound", protocol = "*", source = var.gpu_subnet_cidr, destination = var.gpu_subnet_cidr, ports = ["*"], access = "Allow" },
    { name = "deny-other-inbound", priority = 4096, direction = "Inbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
    ], [
    for index, cidr in local.pe_cidrs : { name = "allow-private-services-${index}", priority = 100 + index, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = cidr, ports = ["443", "445"], access = "Allow" }
    ], [
    { name = "allow-aml", priority = 200, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureMachineLearning", ports = ["443", "8787", "18881"], access = "Allow" },
    { name = "allow-aml-tundra", priority = 210, direction = "Outbound", protocol = "Udp", source = var.gpu_subnet_cidr, destination = "AzureMachineLearning", ports = ["5831"], access = "Allow" },
    { name = "allow-batch", priority = 220, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = "BatchNodeManagement.${var.location}", ports = ["443"], access = "Allow" },
    { name = "allow-entra", priority = 230, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureActiveDirectory", ports = ["80", "443"], access = "Allow" },
    { name = "allow-storage", priority = 240, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "Storage.${var.location}", ports = ["443"], access = "Allow" },
    { name = "allow-vault", priority = 250, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureKeyVault", ports = ["443"], access = "Allow" },
    { name = "allow-registry", priority = 260, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureContainerRegistry", ports = ["443"], access = "Allow" },
    { name = "allow-arm", priority = 270, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureResourceManager", ports = ["443"], access = "Allow" },
    { name = "allow-monitor", priority = 280, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureMonitor", ports = ["443"], access = "Allow" },
    { name = "allow-microsoft-registry", priority = 281, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "MicrosoftContainerRegistry", ports = ["443"], access = "Allow" },
    { name = "allow-microsoft-registry-cdn", priority = 282, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "AzureFrontDoor.FirstParty", ports = ["443"], access = "Allow" },
    { name = "allow-managed-identity", priority = 290, direction = "Outbound", protocol = "Tcp", source = var.gpu_subnet_cidr, destination = "169.254.169.254/32", ports = ["80"], access = "Allow" },
    { name = "allow-private-dns", priority = 300, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = "VirtualNetwork", ports = ["53"], access = "Allow" },
    { name = "allow-node-internal-outbound", priority = 310, direction = "Outbound", protocol = "*", source = var.gpu_subnet_cidr, destination = var.gpu_subnet_cidr, ports = ["*"], access = "Allow" },
    { name = "deny-other-outbound", priority = 4096, direction = "Outbound", protocol = "*", source = "*", destination = "*", ports = ["*"], access = "Deny" }
  ])
}

resource "azurerm_network_security_group" "compute" {
  count               = var.existing_gpu_nsg_id == null ? 1 : 0
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
  count                     = var.existing_gpu_nsg_id == null ? 1 : 0
  subnet_id                 = var.gpu_subnet_id
  network_security_group_id = azurerm_network_security_group.compute[0].id
  depends_on                = [terraform_data.landing_zone]
}

moved {
  from = azurerm_network_security_group.compute
  to   = azurerm_network_security_group.compute[0]
}

moved {
  from = azurerm_subnet_network_security_group_association.compute
  to   = azurerm_subnet_network_security_group_association.compute[0]
}
