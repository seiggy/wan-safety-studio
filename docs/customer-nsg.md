# Customer-managed GPU NSG handoff

Use this path when the customer's landing zone or policy already attaches an NSG to the dedicated GPU subnet. The customer security team owns that NSG, its association, its rules and reconciliation. The accelerator does not need access to their automation source and never adds or replaces rules on that NSG.

## CSA: select, deploy, and export

1. In `infra\terraform.tfvars.json`, set `existing_gpu_nsg_id` to the **full resource ID of the NSG already attached** to `gpu_subnet_id`. Keep `compute_enabled=false`. A missing/different association fails deployment instead of being replaced. Omit this field or use JSON `null` only when Terraform should create and associate its own NSG.
2. Deploy the foundation with `.\scripts\Invoke-WanSafetyStudio.ps1 -Action Deploy -ApprovePersistentCosts`. This creates persistent-cost resources, but no GPU compute or NAT/public IP. It does not validate or change the customer NSG's rules.
3. From the repository root in PowerShell 7, export the rules and send the resulting file to the security team:

```powershell
$handoffJson = terraform -chdir=infra output -json gpu_nsg_rules
if ($LASTEXITCODE -ne 0) { throw 'NSG rule export failed. Deploy the foundation first.' }
New-Item -ItemType Directory -Path .\handoff -Force | Out-Null
$handoffJson | Set-Content -Encoding utf8 .\handoff\gpu-nsg-rules.json
$handoff = $handoffJson | ConvertFrom-Json
$handoff.networkSecurityGroupId
$handoff.rules | Format-Table name,priority,direction,access,protocol,source,destination,
    @{Name='Destination ports';Expression={$_.ports -join ','}}

# CSV for a change request or environment-management project.
$handoff.rules | Select-Object `
    @{Name='networkSecurityGroupId';Expression={$handoff.networkSecurityGroupId}},
    name,priority,direction,access,protocol,source,destination,
    @{Name='sourcePorts';Expression={$_.source_ports -join ','}},
    @{Name='destinationPorts';Expression={$_.ports -join ','}} |
    Export-Csv -NoTypeInformation -Encoding utf8 .\handoff\gpu-nsg-rules.csv
```

Open the `handoff` folder at the repository root in your file explorer. Send `gpu-nsg-rules.json` (machine-readable) and `gpu-nsg-rules.csv` (review-friendly), together with the security-team instructions below. These are exports from the deployed foundation, not the earlier `gpu-nsg-rules.preview.json`.

The `handoff` directory is ignored by Git, so it will not appear in the Git changes list; the files still exist on disk. Keep customer resource IDs out of committed examples. The export comes from the **same Terraform list used to create the default owned NSG**; it includes actual subnet CIDRs, every rule, and source ports. It describes required configuration, not observed live rules.

## Confirm the foundation before handoff

Use the same Azure CLI tenant/subscription and customer JSON as deployment:

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Status
$studioJson = terraform -chdir=infra output -json studio
if ($LASTEXITCODE -ne 0) { throw 'Foundation output is unavailable; deployment may be incomplete.' }
$studio = $studioJson | ConvertFrom-Json
az resource list --resource-group $studio.resourceGroupName `
    --query "[].{name:name,type:type,location:location}" --output table
az network private-endpoint list --resource-group $studio.resourceGroupName `
    --query "[].{name:name,state:provisioningState}" --output table
az network nsg show --ids $studio.networkSecurityGroupId `
    --query "{id:id,etag:etag,rules:securityRules}" --output json
az resource show --ids $studio.workspaceId --api-version 2025-09-01 `
    --query "{state:properties.provisioningState,publicNetworkAccess:properties.publicNetworkAccess,datastoreAuth:properties.systemDatastoresAuthMode,v1LegacyMode:properties.v1LegacyMode}" `
    --output json
```

Expected: the five private endpoints are `Succeeded`, and operator Status reports `OFF`, no compute/NAT/public IP, and no active jobs. The selected customer NSG and its existing rules should be unchanged. Status is an ARM resource check, **not** a private-DNS reachability test or GPU-generation test.

For the workspace, expect `Succeeded`, `publicNetworkAccess=Disabled`, identity-based datastore authentication, and `v1LegacyMode=false`. In the first live deployment, ARM returned the datastore-auth value as lowercase `identity` and omitted the legacy `allowPublicAccessWhenBehindVnet` field even though it was explicitly configured. Do not treat an omitted legacy field as a verified `false`; check the documented [public-network-access control](https://learn.microsoft.com/azure/machine-learning/how-to-configure-private-link?view=azureml-api-2) and the approved private endpoint instead.

The deployment writes local Terraform state to `infra\terraform.tfstate` and the scoped operator receipt to the cache location documented in the README. Back up both in approved storage; do not commit them. If deployment fails partway, retain the state and error details, resolve the reported permission/policy/service issue with its owner, and rerun the same Deploy command with the same configuration/state. Do not delete state, recreate the deployment under another name, or enable public access to work around a failure.

Foundation completion does not install model weights, build the GPU image, enable compute, or configure Entra sign-in. The optional App Service host is created only when `portal` is set, and it stays unpublished until the [App Service steps](app-service.md). Standing ACR/private-endpoint/storage/logging costs continue while Status is OFF.

## Security team: review and apply before GPU use

Please apply the exported rules, or policy-approved equivalents preserving the required connectivity and isolation, through the system that owns your NSG. No ownership transfer or policy exemption is required by this accelerator. Confirm that subsequent reconciliation preserves the approved rules.

Do not blindly paste rules over existing rules. Check names and priorities for conflicts, and review all higher-priority matches (lower numbers take precedence). Existing broad allow rules can bypass the intended isolation; existing deny rules can block required traffic. Keep the final deny rules after the intended allows but before Azure's default NSG rules.

The following is the baseline for a dedicated GPU NSG. `GPU` means `gpu_subnet_cidr`; `PE` means each address prefix of the selected private-endpoint subnet. Source ports are `*` for every rule. Destination service names below are Azure service tags, not DNS names.

| Rule | Priority | Direction | Access | Protocol | Source | Destination | Destination ports |
| --- | --- | --- | --- | --- | --- | --- | --- |
| allow-node-internal-inbound | 100 | Inbound | Allow | Any | GPU | GPU | Any |
| deny-other-inbound | 4096 | Inbound | Deny | Any | Any | Any | Any |
| allow-private-services-0 | 100 + prefix index | Outbound | Allow | TCP | GPU | PE | 443, 445 |
| allow-aml | 200 | Outbound | Allow | TCP | GPU | AzureMachineLearning | 443, 8787, 18881 |
| allow-aml-tundra | 210 | Outbound | Allow | UDP | GPU | AzureMachineLearning | 5831 |
| allow-batch | 220 | Outbound | Allow | Any | GPU | BatchNodeManagement.&lt;location&gt; | 443 |
| allow-entra | 230 | Outbound | Allow | TCP | GPU | AzureActiveDirectory | 80, 443 |
| allow-storage | 240 | Outbound | Allow | TCP | GPU | Storage.&lt;location&gt; | 443 |
| allow-vault | 250 | Outbound | Allow | TCP | GPU | AzureKeyVault | 443 |
| allow-registry | 260 | Outbound | Allow | TCP | GPU | AzureContainerRegistry | 443 |
| allow-arm | 270 | Outbound | Allow | TCP | GPU | AzureResourceManager | 443 |
| allow-monitor | 280 | Outbound | Allow | TCP | GPU | AzureMonitor | 443 |
| allow-microsoft-registry | 281 | Outbound | Allow | TCP | GPU | MicrosoftContainerRegistry | 443 |
| allow-microsoft-registry-cdn | 282 | Outbound | Allow | TCP | GPU | AzureFrontDoor.FirstParty | 443 |
| allow-managed-identity | 290 | Outbound | Allow | TCP | GPU | 169.254.169.254/32 | 80 |
| allow-private-dns | 300 | Outbound | Allow | Any | GPU | VirtualNetwork | 53 |
| allow-node-internal-outbound | 310 | Outbound | Allow | Any | GPU | GPU | Any |
| deny-other-outbound | 4096 | Outbound | Deny | Any | Any | Any | Any |

For multiple PE prefixes, the export contains one `allow-private-services-<index>` rule per prefix. Names/priorities are the accelerator baseline, not an instruction to overwrite occupied customer priorities. **Do not apply the deny-all rules unchanged to an NSG shared with other workloads.** Have the security team scope or redesign the rules, or provide a dedicated NSG, without weakening the GPU subnet's isolation.

The current baseline has **18 rules for one PE prefix**, with no `Internet`, `AzureCloud` or wildcard outbound allow. Batch and Storage tags use the configured `location` (`BatchNodeManagement.<location>` and `Storage.<location>`). Azure Monitor carries platform telemetry; MicrosoftContainerRegistry and AzureFrontDoor.FirstParty permit Microsoft's runtime/support images as documented in the [Azure ML firewall requirements](https://learn.microsoft.com/azure/machine-learning/how-to-access-azureml-behind-firewall?view=azureml-api-2). Model weights and Python packages remain CPU-prepared; there are no GPU rules for PyPI, Hugging Face, GitHub or Docker Hub.

**Updating the earlier 16-rule handoff:** remove `allow-public-https` (Internet:443), narrow `allow-batch`/`allow-storage` to regional tags, and add the three monitor/Microsoft-registry rules above. Free priority 280 before creating `allow-monitor`. Make these changes in the NSG's owning IaC, not through a competing accelerator rule resource.

Service tags identify Azure service IP ranges, **not trusted customer accounts or tenant identities**. Regional Storage and CDN access can still reach other resources hosted by those services. This is restricted platform egress, not resource-level exfiltration protection. For stronger controls, use the customer's firewall and supported service endpoint policies rather than claiming the NSG alone enforces tenant isolation.

DNS, routing, firewall rules and SNAT remain separate requirements. With `defaultOutboundAccess=false`, NSG allows alone do not supply outbound connectivity: provide routed egress or explicitly approve `manage_compute_egress=true`. In the NAT option, the public IP belongs to the outbound gateway, never the GPU node. NAT/public-IP standing charges continue while provisioned, even when the cluster has zero nodes.

### Why a private GPU job still needs outbound platform access

The WAN application's images, weights, code and generated videos use the private ACR/Storage/AML paths. It does not need to download models or install Python packages from the internet on the paid GPU; preparation handles those first.

However, `AmlCompute` is a managed Azure Batch-backed cluster, not just an isolated VM running a container. Microsoft's no-public-IP compute requirements also include outbound Azure ML/Batch communication and access to **Batch's service-managed Storage**, which is different from the customer's private model/output storage account. Identity/control services are additional platform dependencies. The private endpoints created here do not privatize all of those services.

Required outbound access does **not** mean a public IP on the GPU, public inbound access, or unrestricted internet access. A customer-controlled firewall/routed egress path can restrict approved service tags/FQDNs; a NAT gateway provides connectivity/SNAT but not destination filtering. The NSG allowlist and final deny rule restrict which destinations can use that NAT.

No public inbound application/SSH rule is required on GPU compute. These are **GPU** rules, not App Service integration-subnet rules. The optional App Service host needs only outbound TCP 443 from its integration subnet to the private endpoint subnet; see [App Service prerequisites](app-service.md).

## Operator: proceed after sign-off

Obtain confirmation that the rules are applied, effective, and preserved by reconciliation; private DNS and approved egress must also be ready. Complete `Prepare`, then use:

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan `
  -ApproveGpuSpend -ApproveCustomerNsgRules
```

`-ApproveCustomerNsgRules` is required on every Start in customer-NSG mode. It is operator acknowledgment, **not** automated rule/effective-route validation. The regular prepared-assets, connectivity, price, quota and single-node checks still apply. Direct Terraform apply bypasses operator approvals; restrict deployment access accordingly.

Run the README's synthetic smoke generation, then Stop and verify release. Stop preserves the customer NSG and its rules; only owned compute/NAT/public IP are released. If generation fails, use Stop, retain the job error details, and have the relevant customer owner review connectivity or capacity. Do not disable the NSG or retry by opening public endpoints.

Choose NSG ownership before the first deployment. For an existing accelerator-owned NSG, changing this input is **not** an automatic handover: the infrastructure owner must review state/ownership migration before applying a plan that removes owned resources.
