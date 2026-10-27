# A legacy-named foundation is migration-only, and must remain OFF for infrastructure
# changes. Lists are native read-only ARM calls. A partial/paginated response fails closed.
locals {
  legacy_suffix    = substr(sha256("${var.subscription_id}:${var.deployment_name}"), 0, 8)
  legacy_group     = "/subscriptions/${var.subscription_id}/resourceGroups/rg-${var.deployment_name}-${local.legacy_suffix}"
  legacy_prefix    = "${local.legacy_group}/providers"
  legacy_workspace = "${local.legacy_prefix}/Microsoft.MachineLearningServices/workspaces/mlw-${var.deployment_name}-${local.legacy_suffix}"
  legacy_compute   = "${local.legacy_workspace}/computes/wan-gpu"
}

data "azapi_resource_list" "legacy_computes" {
  count                  = var.legacy_region == null ? 0 : 1
  type                   = "Microsoft.MachineLearningServices/workspaces/computes@2024-10-01"
  parent_id              = local.legacy_workspace
  response_export_values = ["value", "nextLink"]
}
data "azapi_resource_list" "legacy_jobs" {
  count                  = var.legacy_region == null ? 0 : 1
  type                   = "Microsoft.MachineLearningServices/workspaces/jobs@2024-10-01"
  parent_id              = local.legacy_workspace
  response_export_values = ["value", "nextLink"]
}
data "azapi_resource_list" "legacy_nat" {
  count                  = var.legacy_region == null ? 0 : 1
  type                   = "Microsoft.Network/natGateways@2024-05-01"
  parent_id              = local.legacy_group
  response_export_values = ["value", "nextLink"]
}
data "azapi_resource_list" "legacy_pip" {
  count                  = var.legacy_region == null ? 0 : 1
  type                   = "Microsoft.Network/publicIPAddresses@2024-05-01"
  parent_id              = local.legacy_group
  response_export_values = ["value", "nextLink"]
}

data "azapi_resource_list" "legacy_sites" {
  count                  = var.legacy_region != null && var.portal != null ? 1 : 0
  type                   = "Microsoft.Web/sites@2023-12-01"
  parent_id              = local.legacy_group
  response_export_values = ["value", "nextLink"]
}

resource "terraform_data" "legacy_handoff" {
  count = var.legacy_region == null ? 0 : 1
  input = { workspace = local.legacy_workspace, region = var.legacy_region }
  lifecycle {
    precondition {
      condition = alltrue([
        for result in concat([data.azapi_resource_list.legacy_computes[0].output,
          data.azapi_resource_list.legacy_jobs[0].output, data.azapi_resource_list.legacy_nat[0].output,
        data.azapi_resource_list.legacy_pip[0].output], [for sites in data.azapi_resource_list.legacy_sites : sites.output]) :
        try(startswith(jsonencode(result.value), "["), false) && try(result.nextLink == null || result.nextLink == "", true)
      ])
      error_message = "Legacy migration requires complete read-only ARM inventory; unknown or paginated results are not verified OFF."
    }
    precondition {
      condition = var.portal == null ? true : try(alltrue([for site in data.azapi_resource_list.legacy_sites[0].output.value :
        site.properties.state == "Stopped"
      if lower(site.id) == lower("${local.legacy_prefix}/Microsoft.Web/sites/app-${var.deployment_name}-${local.legacy_suffix}")]), false)
      error_message = "An existing shared host must be stopped during legacy foundation handoff; a config flag alone does not fence in-flight submissions."
    }
    precondition {
      condition = try(length([for resource in data.azapi_resource_list.legacy_computes[0].output.value :
      resource if lower(resource.id) == lower(local.legacy_compute)]) == 0, false)
      error_message = "Legacy compute must be verified absent before state ownership handoff or durable infrastructure changes."
    }
    precondition {
      condition = try(alltrue([for job in data.azapi_resource_list.legacy_jobs[0].output.value :
        contains(["Completed", "Failed", "Canceled", "Cancelled"], job.properties.status)
      if contains([lower(local.legacy_compute), "wan-gpu"], lower(job.properties.computeId))]), false)
      error_message = "Relevant legacy jobs must all be terminal; queued/running/unknown work blocks migration."
    }
    precondition {
      condition = try(length([for resource in data.azapi_resource_list.legacy_nat[0].output.value :
        resource if lower(resource.id) == lower("${local.legacy_prefix}/Microsoft.Network/natGateways/nat-${var.deployment_name}-${local.legacy_suffix}")]) == 0 &&
        length([for resource in data.azapi_resource_list.legacy_pip[0].output.value :
      resource if lower(resource.id) == lower("${local.legacy_prefix}/Microsoft.Network/publicIPAddresses/pip-${var.deployment_name}-${local.legacy_suffix}-egress")]) == 0, false)
      error_message = "Owned legacy NAT and public IP must be verified absent before handoff."
    }
  }
}
