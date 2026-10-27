module "region" {
  source                     = "./modules/region"
  for_each                   = var.regions
  deployment_name            = var.deployment_name
  subscription_id            = var.subscription_id
  tenant_id                  = var.tenant_id
  operator_principal_id      = var.operator_principal_id
  publisher_principal_id     = var.publisher_principal_id
  legacy_names               = each.key == var.legacy_region
  location                   = each.key
  gpu_subnet_id              = each.value.gpu_subnet_id
  gpu_subnet_cidr            = each.value.gpu_subnet_cidr
  gpu_subnet_dedicated       = each.value.gpu_subnet_dedicated
  private_endpoint_subnet_id = each.value.private_endpoint_subnet_id
  existing_gpu_nsg_id        = each.value.existing_gpu_nsg_id
  private_dns_zone_ids       = each.value.private_dns_zone_ids
  log_analytics_workspace_id = each.value.log_analytics_workspace_id
  max_nodes                  = each.value.max_nodes
  max_job_minutes            = each.value.max_job_minutes
  max_payg_hourly_usd        = each.value.max_payg_hourly_usd
  manage_compute_egress      = each.value.manage_compute_egress
  egress_public_ip_tags      = each.value.egress_public_ip_tags
}

locals {
  control        = module.region[var.control_region]
  names          = local.control.inventory.names
  stem           = trimprefix(local.names.resource_group, "rg-")
  ids            = local.control.inventory.ids
  ownership_tags = local.control.resources.ownershipTags
  role_ids       = local.control.inventory.roleIds
}
