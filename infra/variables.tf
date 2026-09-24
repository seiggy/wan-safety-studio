variable "deployment_name" {
  type        = string
  nullable    = false
  description = "Stable 3-16 character lowercase DNS-safe slug. Changing it changes resource identities."
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,14}[a-z0-9]$", var.deployment_name)) && !strcontains(var.deployment_name, "--")
    error_message = "deployment_name must be 3-16 lowercase letters, digits or single hyphens, starting with a letter and ending with a letter/digit."
  }
}

variable "subscription_id" {
  type        = string
  nullable    = false
  description = "Customer subscription UUID; all supplied Azure resources must belong to this subscription."
  validation {
    condition     = can(regex("(?i)^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.subscription_id))
    error_message = "subscription_id must be a UUID."
  }
}

variable "tenant_id" {
  type        = string
  nullable    = false
  description = "Customer Microsoft Entra tenant UUID."
  validation {
    condition     = can(regex("(?i)^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.tenant_id))
    error_message = "tenant_id must be a UUID."
  }
}

variable "operator_principal_id" {
  type        = string
  nullable    = false
  description = "Object ID (not application/client ID) of the operator user or service principal."
  validation {
    condition     = can(regex("(?i)^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.operator_principal_id))
    error_message = "operator_principal_id must be an Entra object UUID."
  }
}

variable "location" {
  type        = string
  nullable    = false
  description = "Canonical Azure public-cloud region, e.g. eastus2. A100 Spot quota/availability must be checked separately."
  validation {
    condition     = can(regex("^[a-z][a-z0-9]+$", var.location))
    error_message = "location must be a canonical lowercase Azure region without spaces."
  }
}

variable "gpu_subnet_id" {
  type        = string
  nullable    = false
  description = "Existing dedicated GPU subnet ARM ID. Terraform never owns or replaces the subnet."
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.gpu_subnet_id))
    error_message = "gpu_subnet_id must be a complete subnet ARM ID in subscription_id, without a trailing slash."
  }
}

variable "gpu_subnet_cidr" {
  type        = string
  nullable    = false
  description = "Expected single canonical IPv4 CIDR of the existing GPU subnet; actual configuration must match exactly."
  validation {
    condition     = can(cidrnetmask(var.gpu_subnet_cidr)) && try("${cidrhost(var.gpu_subnet_cidr, 0)}/${split("/", var.gpu_subnet_cidr)[1]}" == var.gpu_subnet_cidr, false)
    error_message = "gpu_subnet_cidr must be a canonical IPv4 network CIDR (not a host address or IPv6)."
  }
}

variable "existing_gpu_nsg_id" {
  type        = string
  default     = null
  description = "Optional customer-owned NSG already attached to the dedicated GPU subnet. Terraform never manages this NSG, its rules or its association; the security team must apply the gpu_nsg_rules output before Start. Null creates an owned NSG."
  validation {
    condition     = var.existing_gpu_nsg_id == null || can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/networkSecurityGroups/[^/]+$", var.existing_gpu_nsg_id))
    error_message = "existing_gpu_nsg_id must be null or a complete NSG ARM ID in subscription_id."
  }
}

variable "private_endpoint_subnet_id" {
  type        = string
  nullable    = false
  description = "Existing private endpoint subnet ARM ID; must differ from the dedicated GPU subnet."
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.private_endpoint_subnet_id))
    error_message = "private_endpoint_subnet_id must be a complete subnet ARM ID in subscription_id."
  }
  validation {
    condition     = lower(var.private_endpoint_subnet_id) != lower(var.gpu_subnet_id)
    error_message = "The GPU and private endpoint subnets must be distinct."
  }
}

variable "private_dns_zone_ids" {
  type = object({
    blob      = string
    file      = string
    vault     = string
    registry  = string
    api       = string
    notebooks = string
  })
  nullable    = false
  description = "Existing private DNS zone IDs, already linked/resolvable from the GPU and operator networks. No zones or links are created."
  validation {
    condition = alltrue([
      for key, zone in var.private_dns_zone_ids : can(regex(
        "(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft.Network/privateDnsZones/${replace({
          blob      = "privatelink.blob.core.windows.net"
          file      = "privatelink.file.core.windows.net"
          vault     = "privatelink.vaultcore.azure.net"
          registry  = "privatelink.azurecr.io"
          api       = "privatelink.api.azureml.ms"
          notebooks = "privatelink.notebooks.azure.net"
        }[key], ".", "\\.")}$", zone
      ))
    ])
    error_message = "All six private DNS zones must have the expected Azure public-cloud names and be in subscription_id."
  }
}

variable "log_analytics_workspace_id" {
  type        = string
  nullable    = false
  description = "Existing customer Log Analytics workspace ARM ID. No log store is created."
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft.OperationalInsights/workspaces/[^/]+$", var.log_analytics_workspace_id))
    error_message = "log_analytics_workspace_id must identify an existing workspace in subscription_id."
  }
}

variable "gpu_subnet_dedicated" {
  type        = bool
  nullable    = false
  description = "Required explicit acknowledgment that only this studio's compute may use the GPU subnet and its NSG/NAT associations."
  validation {
    condition     = var.gpu_subnet_dedicated
    error_message = "gpu_subnet_dedicated must be explicitly true; shared customer compute subnets are not supported."
  }
}

variable "compute_enabled" {
  type        = bool
  default     = false
  nullable    = false
  description = "Provision the private min-zero/max-one Spot A100 target. Default OFF; this flag does not arm local job submission."
}

variable "manage_compute_egress" {
  type        = bool
  default     = false
  nullable    = false
  description = "Consent to create an owned NAT/PIP and associate only the dedicated GPU subnet while compute is enabled. Otherwise use existing customer-routed egress."
}

variable "egress_public_ip_tags" {
  type        = map(string)
  default     = {}
  nullable    = false
  description = "Azure public-IP ip_tags required by customer policy (not resource tags). Obtain exact values from the network owner; preserve policy-appended values to avoid replacement."
}

variable "portal" {
  type = object({
    subnet_id   = string
    dns_zone_id = string
    sku         = optional(string, "B1")
  })
  default     = null
  description = "Optional private App Service host for the Entra portal. subnet_id: existing empty subnet delegated to Microsoft.Web/serverFarms in location (outbound VNet integration). dns_zone_id: existing privatelink.azurewebsites.net zone. Null creates no App Service."
  validation {
    condition = var.portal == null ? true : (
      can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.portal.subnet_id)) &&
      can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/privateDnsZones/privatelink\\.azurewebsites\\.net$", var.portal.dns_zone_id)) &&
      contains(["B1", "B2", "B3", "S1", "S2", "S3", "P0v3", "P1v3", "P2v3"], var.portal.sku) &&
      !contains([lower(var.gpu_subnet_id), lower(var.private_endpoint_subnet_id)], lower(var.portal.subnet_id))
    )
    error_message = "portal needs a distinct same-subscription subnet ID, the privatelink.azurewebsites.net zone ID and a Basic/Standard/PremiumV3 SKU that supports private endpoints."
  }
}

variable "max_payg_hourly_usd" {
  type        = number
  default     = 4
  nullable    = false
  description = "Configurable finite positive runtime retail-price admission ceiling, default $4/hour; not an Azure spend cap or Spot bid. Terraform does not query prices."
  validation {
    condition     = var.max_payg_hourly_usd > 0 && can(jsonencode(var.max_payg_hourly_usd))
    error_message = "max_payg_hourly_usd must be a finite number greater than zero."
  }
}
