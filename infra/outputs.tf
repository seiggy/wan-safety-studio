output "regions" {
  description = "Informational, per region: resource IDs, names and effective ceilings of what Terraform created. For an operator to read and copy by hand; not a machine contract. Prospective compute/NAT/PIP IDs do not imply the resource exists."
  value       = { for region, foundation in module.region : region => foundation.resources }
  depends_on  = [terraform_data.legacy_handoff]
}

output "portal" {
  description = "Informational: the one shared host's ID, host names and identity IDs."
  value = local.portal_enabled ? {
    id                  = azurerm_linux_web_app.portal[0].id
    name                = azurerm_linux_web_app.portal[0].name
    hostname            = azurerm_linux_web_app.portal[0].default_hostname
    scmHostname         = replace(azurerm_linux_web_app.portal[0].default_hostname, "/^([^.]+)\\./", "$1.scm.")
    identityClientId    = azurerm_user_assigned_identity.portal[0].client_id
    identityPrincipalId = azurerm_user_assigned_identity.portal[0].principal_id
  } : null
}

output "gpu_nsg_rules" {
  description = "Per-region security-team handoff, not proof of live NSG rules. Customer-owned NSGs/associations are never managed."
  value       = { for region, foundation in module.region : region => foundation.gpu_nsg_rules }
}
