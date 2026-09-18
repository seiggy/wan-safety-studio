locals {
  suffix = substr(sha256("${lower(var.subscription_id)}:${var.deployment_name}"), 0, 8)
  stem   = "${var.deployment_name}-${local.suffix}"
  # Azure vault/storage limits are tighter than the shared deployment slug.
  # The hash always uses the full slug; previously valid <=12-char names stay unchanged.
  short_slug = trimsuffix(substr(var.deployment_name, 0, 12), "-")
  names = {
    resource_group     = "rg-${local.stem}"
    workspace          = "mlw-${local.stem}"
    storage            = "st${replace(local.short_slug, "-", "")}${local.suffix}"
    registry           = "cr${replace(var.deployment_name, "-", "")}${local.suffix}"
    vault              = "kv-${local.short_slug}-${local.suffix}"
    insights           = "appi-${local.stem}"
    workspace_identity = "id-${local.stem}-workspace"
    compute_identity   = "id-${local.stem}-gpu"
    nsg                = "nsg-${local.stem}-gpu"
    nat                = "nat-${local.stem}"
    pip                = "pip-${local.stem}-egress"
    compute            = "wan-gpu"
    container          = "wan-studio"
    datastore          = "wan_blob"
  }
  resource_group_id = "/subscriptions/${lower(var.subscription_id)}/resourceGroups/${local.names.resource_group}"
  resource_prefix   = "${local.resource_group_id}/providers"
  ids = {
    workspace = "${local.resource_prefix}/Microsoft.MachineLearningServices/workspaces/${local.names.workspace}"
    compute   = "${local.resource_prefix}/Microsoft.MachineLearningServices/workspaces/${local.names.workspace}/computes/${local.names.compute}"
    storage   = "${local.resource_prefix}/Microsoft.Storage/storageAccounts/${local.names.storage}"
    registry  = "${local.resource_prefix}/Microsoft.ContainerRegistry/registries/${local.names.registry}"
    vault     = "${local.resource_prefix}/Microsoft.KeyVault/vaults/${local.names.vault}"
    nsg       = "${local.resource_prefix}/Microsoft.Network/networkSecurityGroups/${local.names.nsg}"
    nat       = "${local.resource_prefix}/Microsoft.Network/natGateways/${local.names.nat}"
    pip       = "${local.resource_prefix}/Microsoft.Network/publicIPAddresses/${local.names.pip}"
  }
  ownership_tags = {
    application = "wan-safety-studio"
    deployment  = var.deployment_name
    managedBy   = "terraform"
  }
  manage_egress = var.compute_enabled && var.manage_compute_egress
}
