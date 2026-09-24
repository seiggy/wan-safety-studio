data "azapi_resource" "gpu_subnet" {
  type                   = "Microsoft.Network/virtualNetworks/subnets@2024-05-01"
  resource_id            = var.gpu_subnet_id
  response_export_values = ["properties"]
}

data "azapi_resource" "private_endpoint_subnet" {
  type                   = "Microsoft.Network/virtualNetworks/subnets@2024-05-01"
  resource_id            = var.private_endpoint_subnet_id
  response_export_values = ["properties"]
}

data "azapi_resource" "gpu_vnet" {
  type                   = "Microsoft.Network/virtualNetworks@2024-05-01"
  resource_id            = join("/", slice(split("/", var.gpu_subnet_id), 0, 9))
  response_export_values = ["location"]
}

data "azapi_resource" "private_endpoint_vnet" {
  type                   = "Microsoft.Network/virtualNetworks@2024-05-01"
  resource_id            = join("/", slice(split("/", var.private_endpoint_subnet_id), 0, 9))
  response_export_values = ["location"]
}

locals {
  gpu_subnet_properties = data.azapi_resource.gpu_subnet.output.properties
  gpu_actual_cidrs = try(length(local.gpu_subnet_properties.addressPrefixes) > 0, false) ? local.gpu_subnet_properties.addressPrefixes : compact([
    try(local.gpu_subnet_properties.addressPrefix, null)
  ])
  pe_properties = data.azapi_resource.private_endpoint_subnet.output.properties
  pe_cidrs = try(length(local.pe_properties.addressPrefixes) > 0, false) ? local.pe_properties.addressPrefixes : compact([
    try(local.pe_properties.addressPrefix, null)
  ])
  existing_nat_id = try(coalesce(local.gpu_subnet_properties.natGateway.id, ""), "")
  existing_nsg_id = try(coalesce(local.gpu_subnet_properties.networkSecurityGroup.id, ""), "")
}

# An unconditional guard also runs when optional resources are being removed.
# Never import or manage the customer's subnet/VNet as a Terraform resource.
# Guard checks require a refreshed, untargeted plan. Do not reuse a saved plan
# after anyone changes subnet associations; ARM has no cross-provider lease.
resource "terraform_data" "landing_zone" {
  input = {
    gpu_subnet_id              = var.gpu_subnet_id
    private_endpoint_subnet_id = var.private_endpoint_subnet_id
    gpu_subnet_cidr            = var.gpu_subnet_cidr
  }

  lifecycle {
    precondition {
      condition     = length(local.gpu_actual_cidrs) == 1 && try(local.gpu_actual_cidrs[0] == var.gpu_subnet_cidr, false)
      error_message = "The existing GPU subnet does not have exactly the expected gpu_subnet_cidr. Customer subnet addressing will not be changed."
    }
    precondition {
      condition     = try(local.gpu_subnet_properties.defaultOutboundAccess == false, false)
      error_message = "The customer must explicitly disable defaultOutboundAccess on the dedicated GPU subnet before deployment."
    }
    precondition {
      condition     = try(length(local.gpu_subnet_properties.delegations), 0) == 0
      error_message = "The GPU subnet must not have service delegations."
    }
    precondition {
      condition     = local.existing_nat_id == "" || lower(local.existing_nat_id) == lower(local.ids.nat)
      error_message = "Refusing an unexpected existing GPU subnet NAT gateway. This module never removes/replaces a customer NAT."
    }
    precondition {
      condition = var.existing_gpu_nsg_id == null ? (
        local.existing_nsg_id == "" || lower(local.existing_nsg_id) == lower(local.ids.nsg)
      ) : lower(local.existing_nsg_id) == lower(local.gpu_nsg_id)
      error_message = "The GPU subnet NSG must match existing_gpu_nsg_id when supplied. Otherwise only an absent or accelerator-owned NSG is allowed. Customer NSGs are never attached, removed or replaced."
    }
    precondition {
      condition     = length(local.pe_cidrs) > 0 && alltrue([for cidr in local.pe_cidrs : can(cidrnetmask(cidr))])
      error_message = "The existing private endpoint subnet must expose IPv4 address prefixes."
    }
    precondition {
      condition     = lower(data.azapi_resource.gpu_vnet.location) == var.location && lower(data.azapi_resource.private_endpoint_vnet.location) == var.location
      error_message = "Both supplied subnets' VNets must be in location; cross-region compute/private endpoints are unsupported."
    }
  }
}
