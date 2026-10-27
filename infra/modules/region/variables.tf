variable "deployment_name" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,14}[a-z0-9]$", var.deployment_name)) && !strcontains(var.deployment_name, "--")
    error_message = "deployment_name must be a 3-16 character lowercase DNS-safe slug."
  }
}
variable "subscription_id" {
  type = string
}
variable "tenant_id" {
  type = string
}
variable "operator_principal_id" {
  type = string
}
variable "publisher_principal_id" {
  type    = string
  default = null
}
variable "legacy_names" {
  type     = bool
  default  = false
  nullable = false
}
variable "location" {
  type = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9]+$", var.location))
    error_message = "location must be a canonical Azure region."
  }
}
variable "gpu_subnet_id" {
  type = string
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.gpu_subnet_id))
    error_message = "gpu_subnet_id must be a full subnet ID in the selected subscription."
  }
}
variable "gpu_subnet_cidr" {
  type = string
  validation {
    condition     = can(cidrnetmask(var.gpu_subnet_cidr)) && try("${cidrhost(var.gpu_subnet_cidr, 0)}/${split("/", var.gpu_subnet_cidr)[1]}" == var.gpu_subnet_cidr, false)
    error_message = "gpu_subnet_cidr must be a canonical IPv4 network."
  }
}
variable "gpu_subnet_dedicated" {
  type = bool
  validation {
    condition     = var.gpu_subnet_dedicated
    error_message = "A dedicated GPU subnet must be explicitly acknowledged."
  }
}
variable "existing_gpu_nsg_id" {
  type    = string
  default = null
  validation {
    condition     = var.existing_gpu_nsg_id == null || can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/networkSecurityGroups/[^/]+$", var.existing_gpu_nsg_id))
    error_message = "existing_gpu_nsg_id must be null or an NSG ID in the selected subscription."
  }
}
variable "private_endpoint_subnet_id" {
  type = string
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.private_endpoint_subnet_id)) && lower(var.private_endpoint_subnet_id) != lower(var.gpu_subnet_id)
    error_message = "private_endpoint_subnet_id must be a different same-subscription subnet."
  }
}
variable "private_dns_zone_ids" {
  type = object({
    blob = string, file = string, vault = string, registry = string, api = string, notebooks = string
  })
  validation {
    condition = alltrue([for key, id in var.private_dns_zone_ids : can(regex(
      "(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/privateDnsZones/${replace({
        blob  = "privatelink.blob.core.windows.net", file = "privatelink.file.core.windows.net",
        vault = "privatelink.vaultcore.azure.net", registry = "privatelink.azurecr.io",
        api   = "privatelink.api.azureml.ms", notebooks = "privatelink.notebooks.azure.net"
    }[key], ".", "\\.")}$", id))])
    error_message = "All six existing private DNS zones must have the expected service names and subscription."
  }
}
variable "log_analytics_workspace_id" {
  type = string
  validation {
    condition     = can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.OperationalInsights/workspaces/[^/]+$", var.log_analytics_workspace_id))
    error_message = "Logging must be a supplied workspace in the selected subscription."
  }
}
variable "max_nodes" {
  type    = number
  default = 1
  validation {
    condition     = var.max_nodes >= 1 && floor(var.max_nodes) == var.max_nodes && can(jsonencode(var.max_nodes))
    error_message = "max_nodes must be a finite positive whole-number runtime ceiling."
  }
}
variable "max_job_minutes" {
  type    = number
  default = 120
  validation {
    condition     = var.max_job_minutes >= 1 && var.max_job_minutes <= 120 && floor(var.max_job_minutes) == var.max_job_minutes
    error_message = "max_job_minutes must be a whole number from 1 to 120."
  }
}
variable "max_payg_hourly_usd" {
  type    = number
  default = 4
  validation {
    condition     = var.max_payg_hourly_usd > 0 && can(jsonencode(var.max_payg_hourly_usd))
    error_message = "max_payg_hourly_usd must be finite and positive."
  }
}
variable "manage_compute_egress" {
  type    = bool
  default = false
}
variable "egress_public_ip_tags" {
  type    = map(string)
  default = {}
  validation {
    condition     = alltrue([for key, value in var.egress_public_ip_tags : length(trimspace(key)) > 0 && length(trimspace(value)) > 0])
    error_message = "Egress policy tags must have nonempty keys and values."
  }
}
