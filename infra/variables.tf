variable "deployment_name" {
  type     = string
  nullable = false
  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,14}[a-z0-9]$", var.deployment_name)) && !strcontains(var.deployment_name, "--")
    error_message = "deployment_name must be a stable 3-16 character lowercase DNS-safe slug."
  }
}
variable "subscription_id" {
  type     = string
  nullable = false
  validation {
    condition     = can(regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.subscription_id)) && var.subscription_id != "00000000-0000-0000-0000-000000000000"
    error_message = "subscription_id must be a canonical nonempty lowercase UUID."
  }
}
variable "tenant_id" {
  type     = string
  nullable = false
  validation {
    condition     = can(regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.tenant_id)) && var.tenant_id != "00000000-0000-0000-0000-000000000000"
    error_message = "tenant_id must be a canonical nonempty lowercase UUID."
  }
}
variable "operator_principal_id" {
  type     = string
  nullable = false
  validation {
    condition     = can(regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.operator_principal_id)) && var.operator_principal_id != "00000000-0000-0000-0000-000000000000"
    error_message = "operator_principal_id must be an explicit nonempty lowercase principal UUID."
  }
}
variable "publisher_principal_id" {
  type    = string
  default = null
  validation {
    condition     = var.publisher_principal_id == null || (can(regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$", var.publisher_principal_id)) && var.publisher_principal_id != "00000000-0000-0000-0000-000000000000")
    error_message = "publisher_principal_id must be null or an explicit nonempty lowercase principal UUID."
  }
}
variable "control_region" {
  type        = string
  nullable    = false
  description = "One shared host/control-storage region. Existing deployments retain their legacy region."
  validation {
    condition     = contains(keys(var.regions), var.control_region)
    error_message = "control_region must be one of the configured regions."
  }
}
variable "legacy_region" {
  type        = string
  default     = null
  description = "The one existing deployment retaining its exact pre-regional names. Requires an explicit verified-OFF state migration, not a greenfield naming shortcut."
  validation {
    condition     = var.legacy_region == null ? true : contains(keys(var.regions), var.legacy_region) && var.legacy_region == var.control_region
    error_message = "The legacy region must exist and remain the shared control/host region during migration."
  }
}
variable "regions" {
  type = map(object({
    gpu_subnet_id              = string
    gpu_subnet_cidr            = string
    gpu_subnet_dedicated       = bool
    private_endpoint_subnet_id = string
    existing_gpu_nsg_id        = optional(string)
    private_dns_zone_ids = object({
      blob = string, file = string, vault = string, registry = string, api = string, notebooks = string
    })
    log_analytics_workspace_id = string
    max_nodes                  = optional(number, 1)
    max_job_minutes            = optional(number, 120)
    max_payg_hourly_usd        = optional(number, 4)
    manage_compute_egress      = optional(bool, false)
    egress_public_ip_tags      = optional(map(string), {})
    compute_enabled            = optional(bool, false)
  }))
  nullable    = false
  description = "N independent regional foundations on supplied landing zones. All compute settings are runtime policy; no compute/NAT/PIP is Terraform-managed."
  validation {
    condition     = length(var.regions) > 0 && alltrue([for region, config in var.regions : can(regex("^[a-z][a-z0-9]+$", region)) && !config.compute_enabled])
    error_message = "Supply canonical region keys and compute_enabled=false. Terraform cannot start compute."
  }
  validation {
    condition     = length(distinct([for config in values(var.regions) : lower(config.gpu_subnet_id)])) == length(var.regions)
    error_message = "Regions must use different dedicated GPU subnets."
  }
  validation {
    condition = alltrue([for config in values(var.regions) :
    !contains([for other in values(var.regions) : lower(other.private_endpoint_subnet_id)], lower(config.gpu_subnet_id))])
    error_message = "No GPU subnet may also serve as any configured region's private endpoint subnet."
  }
}
variable "portal" {
  type = object({
    subnet_id   = string
    dns_zone_id = string
    sku         = optional(string, "B1")
  })
  default     = null
  description = "Optional one shared private App Service host in control_region. Customer owns its delegated subnet, NSG/routes, private DNS and inter-region connectivity."
  validation {
    condition = var.portal == null ? true : (
      can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/virtualNetworks/[^/]+/subnets/[^/]+$", var.portal.subnet_id)) &&
      can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.Network/privateDnsZones/privatelink\\.azurewebsites\\.net$", var.portal.dns_zone_id)) &&
      contains(["B1", "B2", "B3", "S1", "S2", "S3", "P0v3", "P1v3", "P2v3"], var.portal.sku) &&
      alltrue([for config in values(var.regions) : !contains([lower(config.gpu_subnet_id), lower(config.private_endpoint_subnet_id)], lower(var.portal.subnet_id))])
    )
    error_message = "portal requires a distinct delegated same-subscription subnet, supplied web private DNS and a supported private-endpoint SKU."
  }
}
variable "agent" {
  type = object({
    endpoint           = string
    model              = string
    auth               = optional(string, "UserAssignedManagedIdentity")
    resource_id        = optional(string)
    wire_api           = optional(string)
    vision             = optional(bool)
    api_key_secret_uri = optional(string)
  })
  default = null
  validation {
    condition     = var.agent == null ? true : can(regex("^https?://", var.agent.endpoint)) && length(trimspace(var.agent.model)) > 0 && contains(["UserAssignedManagedIdentity", "ApiKey"], var.agent.auth)
    error_message = "agent requires an HTTP(S) endpoint, model and supported auth."
  }
  validation {
    condition     = var.agent == null ? true : var.agent.auth != "ApiKey" || can(regex("(?i)^https://[^/]+/secrets/[^/]+(/[^/]+)?$", coalesce(var.agent.api_key_secret_uri, "-")))
    error_message = "Agent API keys must use a Key Vault secret reference."
  }
  validation {
    condition     = var.agent == null ? true : var.agent.resource_id == null || can(regex("(?i)^/subscriptions/[0-9a-f-]{36}/resourceGroups/[^/]+/providers/Microsoft\\.(CognitiveServices|MachineLearningServices)/.+$", var.agent.resource_id))
    error_message = "agent.resource_id must be a Foundry/Azure OpenAI resource ID."
  }
  validation {
    condition     = var.agent == null ? true : var.agent.wire_api == null || contains(["completions", "responses"], coalesce(var.agent.wire_api, "-"))
    error_message = "agent.wire_api must be completions or responses."
  }
}
variable "key_vault_id" {
  type    = string
  default = null
  validation {
    condition     = var.key_vault_id == null ? true : can(regex("(?i)^/subscriptions/${var.subscription_id}/resourceGroups/[^/]+/providers/Microsoft\\.KeyVault/vaults/[^/]+$", var.key_vault_id))
    error_message = "key_vault_id must be a same-subscription vault ID."
  }
}
