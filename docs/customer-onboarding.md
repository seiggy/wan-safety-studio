# Customer onboarding guide

Use this guide before deploying WAN Safety Studio into a customer subscription. It is written for the CSA coordinating the customer network, identity, security, and Azure ML owners.

Do not run `Deploy`, `Prepare`, or `Start` while this information is incomplete. The deployment intentionally refuses to guess network, DNS, tenant, or subscription settings.

## 1. Explain what the accelerator creates

The accelerator creates a private Azure Machine Learning workspace, private Storage, Key Vault, Premium Container Registry, Application Insights, managed identities, private endpoints, and a single-node Spot A100 compute target. GPU compute is absent until an operator explicitly runs `Start`.

The customer supplies the subscription, tenant, Log Analytics workspace, network subnets, private DNS zones, private DNS links/resolution, and GPU quota. The accelerator never creates or takes ownership of a customer VNet, subnet, private DNS zone, DNS link, VPN, firewall, or Log Analytics workspace.

Tell the customer that Premium ACR, private endpoints, retained models/videos, and logging can incur charges even while GPU compute is stopped. The current default GPU admission ceiling is $4 USD/hour; it is a safety check, not a billing cap.

## 2. Identify the people who must approve the work

| Customer owner | What they approve or provide |
| --- | --- |
| Subscription owner | Contributor-level deployment permissions and registered resource providers. |
| Network owner | A dedicated GPU subnet, private endpoint subnet, private DNS resolution, and GPU egress path. |
| Azure ML owner | Low-priority A100 quota and an approved region with capacity. |
| Security / identity owner | The customer Entra tenant, operator identity, and later the App Service sign-in application and authorized user group. |
| Monitoring owner | Existing Log Analytics workspace ID. |
| Model license owner | Approval and access for the pinned WAN model repositories. |

## 3. Gather the current Azure identity values

Run these commands in a standard PowerShell terminal with the Azure CLI installed. They work in Windows PowerShell, PowerShell 7, Azure Cloud Shell, and an unscoped developer terminal; no profile scripts or environment-switching tools are required.

```powershell
az version
az login --tenant <customer-tenant-id>
az account set --subscription <customer-subscription-id>
az account show --query "{subscription_id:id,tenant_id:tenantId,subscription_name:name}" --output json
az ad signed-in-user show --query id --output tsv
```

Record:

- `subscription_id`: `az account show` → `id`
- `tenant_id`: `az account show` → `tenantId`
- `operator_principal_id`: object ID from `az ad signed-in-user show`; this is **not** an application/client ID
- `location`: an Azure region that passes every region check below, for example `centralus`. A100 access and App Service quota differ per subscription, so a region that works for one customer can fail for another.
- `deployment_name`: a unique 3–16-character lowercase name such as `contoso-wan`

### Check the region before choosing it

Run these read-only checks for each candidate region. Replace `<sub>` and `<region>`. A region must pass all of them.

```powershell
# 1. The A100 size is offered to this subscription. Expect: []  (an empty restrictions list)
#    Keep --location: without it the restrictions are not reported per region.
az vm list-skus --location <region> --size Standard_NC24ads_A100_v4 --all --query "[].restrictions" --output json

# 2. Azure ML Spot quota. TotalLowPriorityCores needs 24 free cores; the A100 family row must be -1 (shared pool) or have 24 free.
az rest --method get `
  --url "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.MachineLearningServices/locations/<region>/usages?api-version=2024-04-01" `
  --query "value[?name.value=='TotalLowPriorityCores' || (name.value=='standardNCADSA100v4Family' && contains(type,'lowPriority'))].{quota:name.value,used:currentValue,limit:limit}" `
  --output table

# 3. Linux pay-as-you-go price. It must be at or below max_payg_hourly_usd (default 4), or Start refuses to arm.
#    If the region costs more, raise max_payg_hourly_usd with the customer's approval.
(Invoke-RestMethod ("https://prices.azure.com/api/retail/prices?`$filter=" + [uri]::EscapeDataString(
    "armSkuName eq 'Standard_NC24ads_A100_v4' and armRegionName eq '<region>' and priceType eq 'Consumption'"))).Items |
  Where-Object { $_.productName -notmatch 'Windows' -and $_.skuName -notmatch 'Spot|Low Priority' } |
  Select-Object productName, retailPrice
```

If you will host the [private App Service portal](app-service.md), also run its quota check in the same region. A `Basic` limit of `0` means you need a quota request or a different region.

The usages API does not show the per-SKU VM limit that App Service also enforces. The only definitive check is to create an empty B1 Linux plan and delete it right away. It holds no apps and costs well under USD 0.01:

```powershell
az appservice plan create -g <existing-resource-group> -n asp-quota-probe -l <region> --sku B1 --is-linux --only-show-errors --query provisioningState
az appservice plan delete -g <existing-resource-group> -n asp-quota-probe --yes
```

`"Succeeded"` confirms App Service capacity. `Current Limit (B1 VMs): 0` means the region cannot host the portal until quota is granted.

None of these checks guarantees Spot GPU capacity at run time. The network owner must provide all subnets in the chosen region.

Create the customer configuration now and fill these five values before continuing:

```powershell
Copy-Item .\infra\terraform.tfvars.json.example .\infra\terraform.tfvars.json
```

| Value collected above | `terraform.tfvars.json` field |
| --- | --- |
| Unique deployment name | `deployment_name` |
| Azure subscription ID | `subscription_id` |
| Entra tenant ID | `tenant_id` |
| Operator object ID | `operator_principal_id` |
| Azure region | `location` |

## 4. Ask the network owner for three dedicated subnets

The current Terraform creates five private endpoints and, by default, a GPU NSG and its subnet association. If policy already attaches an NSG, select it with `existing_gpu_nsg_id` instead: Terraform leaves that NSG, its rules and its association under customer ownership. VNets, subnet addressing, DNS links and firewall routes must already exist. Only the explicitly authorized NSG/NAT associations are managed by the accelerator.

The accelerator requires the first two subnets. The third is needed only for the optional Entra-protected [App Service portal](app-service.md) that submits bounded Azure ML jobs.

| Subnet | Required state | Why |
| --- | --- | --- |
| GPU subnet | Dedicated to this accelerator; one IPv4 CIDR; `defaultOutboundAccess=false`; no service delegation or unrelated NAT; either no NSG or an explicitly selected customer NSG | Use the default owned NSG, or leave a policy-managed NSG with the customer security team. Temporary NAT requires separate explicit consent. |
| Private endpoint subnet | Separate from the GPU subnet; IPv4 address space available for at least five private endpoints | Hosts private endpoints for Blob, File, Key Vault, ACR, and Azure ML. |
| App Service integration subnet | Separate, delegated to `Microsoft.Web/serverFarms`, and in the same region as the workload | Optional. Lets the [private App Service portal](app-service.md) reach the private Azure ML and Storage endpoints. It is not a private endpoint subnet. |

Ask the network owner to supply each full Azure resource ID and its address prefix. They can use:

```powershell
az network vnet subnet show `
  --resource-group <network-resource-group> `
  --vnet-name <vnet-name> `
  --name <subnet-name> `
  --query "{id:id,prefix:addressPrefix,prefixes:addressPrefixes,delegations:delegations[].serviceName,defaultOutboundAccess:defaultOutboundAccess,nsg:networkSecurityGroup.id,nat:natGateway.id}" `
  --output json
```

For the GPU subnet, the required result is one prefix, `defaultOutboundAccess` set to `false`, no delegations, and no unrelated NAT gateway. **Do not remove an NSG attached by customer policy.** Copy its `nsg` value into the new `existing_gpu_nsg_id` field below; use `null` when there is no NSG and the accelerator should create one.

Copy only these values into `terraform.tfvars.json`:

| Subnet | JSON field | What to copy |
| --- | --- | --- |
| GPU subnet | `gpu_subnet_id` | The complete `id` from the command. |
| GPU subnet | `gpu_subnet_cidr` | `addressPrefix`, or the one value in `addressPrefixes`, such as `10.42.1.0/26`. |
| Attached customer NSG | `existing_gpu_nsg_id` | The full `nsg` resource ID from the command, or JSON `null` for the default owned NSG. The selected customer NSG must already be attached to this subnet. |
| Private endpoint subnet | `private_endpoint_subnet_id` | The complete `id` from the command. |
| GPU subnet approval | `gpu_subnet_dedicated` | Set to `true` only after the network owner confirms the subnet is dedicated. |
| Temporary NAT consent | `manage_compute_egress` | Keep `false` unless the customer explicitly approves the accelerator-managed NAT/public IP. |
| Public-IP policy metadata | `egress_public_ip_tags` | An object supplied by the network owner, or `{}`. For MCAPS only, the required example is `{"FirstPartyUsage":"/Unprivileged"}`. These are Azure IP tags, not the resource's ordinary `tags`. |

If you already started filling out the JSON, add `existing_gpu_nsg_id` beside `gpu_subnet_cidr`; do not copy the template over your completed values. No other field needs to change to select the existing NSG.

The foundation can be deployed with the existing NSG before the security team finishes its rules, with `compute_enabled=false`. Send them the [NSG rule handoff](customer-nsg.md), including the export command that resolves this deployment's actual CIDRs. Their rules and any firewall/egress changes must be ready **before Start**, not after the first generation attempt. After sign-off, include `-ApproveCustomerNsgRules` on each Start; this is your acknowledgment, not an automated security assessment.

The App Service `portal` object is optional; leave it `null` until the integration subnet and `privatelink.azurewebsites.net` zone are ready. [Host the studio on a private App Service](app-service.md) explains how to look up both IDs and where they go in the JSON.

## 5. Confirm private DNS and egress

The customer must provide existing private DNS zone IDs for all six names below, and the network owner must link/resolve them from the operator, GPU, and App Service integration networks:

```text
privatelink.blob.core.windows.net
privatelink.file.core.windows.net
privatelink.vaultcore.azure.net
privatelink.azurecr.io
privatelink.api.azureml.ms
privatelink.notebooks.azure.net
```

List candidate zones with:

```powershell
az network private-dns zone list `
  --query "[].{name:name,id:id,resource_group:resourceGroup}" `
  --output table
```

For each zone, copy its complete resource ID into the matching `private_dns_zone_ids` entry in `terraform.tfvars.json`:

| Private DNS zone | JSON entry |
| --- | --- |
| `privatelink.blob.core.windows.net` | `private_dns_zone_ids.blob` |
| `privatelink.file.core.windows.net` | `private_dns_zone_ids.file` |
| `privatelink.vaultcore.azure.net` | `private_dns_zone_ids.vault` |
| `privatelink.azurecr.io` | `private_dns_zone_ids.registry` |
| `privatelink.api.azureml.ms` | `private_dns_zone_ids.api` |
| `privatelink.notebooks.azure.net` | `private_dns_zone_ids.notebooks` |

The Terraform deployment creates private endpoints and connects them to these supplied zones. It does **not** create the zones or their VNet links. If a listed zone or link is missing, stop and ask the customer network/DNS owner to create it through their approved landing-zone process.

The GPU subnet needs approved outbound access for Azure Machine Learning requirements. The customer can provide controlled firewall/routing egress, or explicitly approve this accelerator's temporary NAT gateway. NAT provides connectivity only; it does not filter destinations.

## 6. Gather monitoring and quota evidence

Ask the monitoring owner for an existing Log Analytics workspace ID:

```powershell
az monitor log-analytics workspace list `
  --query "[].{name:name,id:id,location:location,resource_group:resourceGroup}" `
  --output table
```

Copy the chosen workspace's complete `id` into `log_analytics_workspace_id`. Terraform creates Application Insights and connects it to this existing workspace; it does not create the Log Analytics workspace.

Ask the Azure ML owner to confirm LowPriority quota for 24 vCPUs, A100 family quota, and regional Spot capacity for `Standard_NC24ads_A100_v4`. Quota approval does not guarantee Spot capacity.

The subscription must already register:

```text
Microsoft.Network
Microsoft.ManagedIdentity
Microsoft.Storage
Microsoft.KeyVault
Microsoft.ContainerRegistry
Microsoft.Insights
Microsoft.MachineLearningServices
Microsoft.OperationalInsights
Microsoft.Web   # only when the optional App Service portal is configured
```

## 7. Plan Entra access, then initialize it after Deploy

The local dashboard uses MSAL with a single-tenant web application and a creator security group. This setup is scripted, not a manual portal-only prerequisite. **Run it after the foundation Deploy step**, because it needs the deployed Key Vault and the operator's cached `foundation.json`.

First, confirm the workstation resolves every private hostname the deployment created. If any one resolves to a public IP, the script and Prepare fail with 403 or connection errors, because public access is disabled.

```powershell
$cache = Join-Path $env:LOCALAPPDATA 'wan-safety-studio\<subscription_id>-<deployment_name>'
(Get-Content "$cache\foundation.json" -Raw | ConvertFrom-Json).privateConnectivityHosts | ForEach-Object {
    [pscustomobject]@{ Host = $_; IP = (Resolve-DnsName $_ -Type A -ErrorAction SilentlyContinue |
        Where-Object Type -eq 'A' | Select-Object -First 1).IPAddress }
}
```

Every IP must be in the private endpoint subnet. Entries starting with `*.` are wildcards and do not resolve literally. Send the network owner any hostname that resolves publicly. Depending on their DNS design, they add a conditional forwarder or an exact-host rule to their private resolver.

```powershell
.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges
```

Get the identity/security owner's approval first. The signed-in user must be the configured `operator_principal_id` and have directory permission to create applications, security groups, memberships and enterprise-application role assignments. Group-based application assignment can require an Entra license. Azure subscription Owner alone is not directory administration.

The script creates or validates `WAN Safety Studio`, exposes the `VideoCreator` role, creates `WAN Safety Studio Creators`, adds the operator as its initial direct member, and assigns that group to the app role. It requires assignment on the enterprise application. It also grants the operator **Key Vault Secrets Officer only on this deployment's vault**, creates a 90-day client secret, and stores it in Key Vault as `wan-studio-msal`. It does not print the secret or put it in Git, command arguments or a temporary file.

Nothing from this step goes into `terraform.tfvars.json`. The non-secret app/client/group IDs and redirect URI go into `portal-auth.json` beside the operator's cached `foundation.json`. The identity owner adds other approved creators to that group. See [local dashboard setup](local-dashboard.md) for exact permissions, reruns, credential rotation and startup commands.

Sign-in shows **Need admin approval** until tenant-wide consent exists: the enterprise application requires creator-group assignment, and Entra never allows user self-consent for assignment-required apps. This one-time step is **required**; plan it with the customer's identity team. The operator (or the customer's identity admin in the Entra admin center) needs an **active** Entra role of Cloud Application Administrator, Application Administrator or Privileged Role Administrator; activate an eligible assignment in Privileged Identity Management (PIM) first. Azure subscription Owner and Global Reader cannot grant consent. With that role active, run `.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges -ApproveAdminConsent` under the configured operator identity. It grants only the two declared sign-in scopes. It does not request offline access, directory-reading permissions or Azure resource access, and creator-group assignment remains required. If the operator lacks the needed Entra role, the customer's identity admin must grant those two permissions to the application in the Entra admin center instead; do not change `operator_principal_id` merely to get past consent.

The local redirect URI is `http://localhost:51881/auth/callback`. When `portal` is configured and deployed, rerunning the same script also adds `https://app-<stem>.azurewebsites.net/auth/callback` and a federated credential that lets the App Service managed identity authenticate as the app without a secret. This is the studio's own MSAL sign-in, not App Service Easy Auth (`/.auth/login/aad/callback`). See [private App Service](app-service.md).

## 8. Create the customer configuration file

At this point every placeholder in `terraform.tfvars.json` should be replaced. **The example intentionally has `compute_enabled=true`. For the very first foundation deployment, temporarily change it to `false`; restore it to `true` after that deployment.** Once network rules/egress are ready, run the [approved compute-enabled Deploy](../README.md#lifecycle) to configure a min-zero/max-one cluster without submitting a job. If NAT is approved, set `manage_compute_egress=true`; the gateway/public IP has a standing charge even while GPU nodes are zero. Start remains a separate prepared-release/spend acknowledgment that arms submission.

`terraform.tfvars.json` is intentionally ignored by Git. Store it in the customer-approved secret/configuration location.

## 9. Validate before approving cost

From a workstation with private DNS/connectivity to the customer services:

```powershell
terraform -chdir=infra init -backend=false
terraform -chdir=infra validate
terraform -chdir=infra test
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Check
```

These are offline code/schema checks, not proof that your customer configuration or Azure permissions/networking are ready. In particular, `Check` runs synthetic tests; it does not read your customer JSON or contact Azure. `Deploy` validates the selected JSON and Azure CLI scope and checks the live subnet preconditions before creating the foundation. In customer-NSG mode it checks the association, not the live rules; complete the security-team handoff before Start.

## CSA handoff checklist

Give the deployment operator:

- Completed `terraform.tfvars.json` stored outside Git
- Written approval for persistent Azure costs
- Confirmation that private DNS works from the operator workstation and, for the hosted portal, that creators resolve the App Service and blob private endpoints
- For a customer-managed NSG, the `gpu_nsg_rules` export and security-team sign-off before Start
- Confirmation of model-license access
- Approved Spot quota/capacity evidence
- For the hosted portal: the delegated integration subnet ID, the `privatelink.azurewebsites.net` zone ID, and the network owner's agreement to add client DNS for the two App Service hostnames
