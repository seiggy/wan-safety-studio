# Customer onboarding guide

Use this guide before deploying WAN Safety Studio into a customer subscription. It is written for the CSA coordinating the customer network, identity, security, and Azure ML owners.

Do not dispatch the foundation, release, promotion or Start workflows or routes while this information is incomplete. The tooling intentionally refuses to guess network, DNS, tenant, or subscription settings. The retired PowerShell `Deploy`, `Prepare`, `Portal`, `Publish` and `Start` actions and `Initialize-PortalAuth.ps1` no longer exist; see [private pipelines](operations/private-pipelines.md) and [hosted lifecycle](operations/hosted-lifecycle.md) for their replacements. Nothing in the resulting process has been run against a live customer Azure environment.

## 1. Explain what the accelerator creates

The accelerator creates, per configured region, a private Azure Machine Learning workspace, private Storage, Key Vault, Premium Container Registry, Application Insights, managed identities and private endpoints. One shared App Service host is optional. Terraform does not create the GPU compute; a single-node Spot A100 compute target exists only while a region is started. GPU compute is absent until a `StudioAdmin` explicitly starts the region with spend approval.

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
- `region`: an Azure region that passes every region check below. It becomes a key of the `regions` map (and `control_region`); there is no fixed region and any number of regions can be listed. A100 access and App Service quota differ per subscription, so a region that works for one customer can fail for another.
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
#    The threshold is a safety check, not a billing cap. If the region costs more, raise the region's
#    max_payg_hourly_usd only with the customer's approval.
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

Create the customer configuration now and fill the top-level values below. Start from the example; its IDs and its `eastus2` region are synthetic and labeled examples only:

```powershell
Copy-Item .\infra\terraform.tfvars.json.example .\infra\terraform.tfvars.json
```

| Value collected above | `terraform.tfvars.json` field |
| --- | --- |
| Unique deployment name | `deployment_name` |
| Azure subscription ID | `subscription_id` |
| Entra tenant ID | `tenant_id` |
| Operator object ID | `operator_principal_id` |
| Azure region | A key under `regions`, and `control_region` (must be one of the configured keys) |

Each `regions.<region>` entry carries that region's landing-zone references and runtime policy. [infra\README.md](../infra/README.md) is the schema reference. For a pipeline, the reviewed JSON is stored as the protected `STUDIO_TFVARS_JSON` secret instead ([private pipelines](operations/private-pipelines.md#explicit-configuration-and-identities)).

## 4. Ask the network owner for dedicated subnets

The current Terraform creates five private endpoints per region and, by default, a GPU NSG and its subnet association. If policy already attaches an NSG, select it with `existing_gpu_nsg_id` instead: Terraform leaves that NSG, its rules and its association under customer ownership. VNets, subnet addressing, DNS links and firewall routes must already exist. Only explicitly authorized NSG/NAT associations are managed by the accelerator. Terraform no longer owns GPU compute or the NAT/public IP; with `manage_compute_egress` consent the hosted Start creates and Stop removes them.

The accelerator requires the first two subnets. The third is needed only for the optional Entra-protected [App Service studio](app-service.md) (a container running the .NET server with the Azure ML provider) that submits bounded Azure ML jobs.

| Subnet | Required state | Why |
| --- | --- | --- |
| GPU subnet | Dedicated to this accelerator; one IPv4 CIDR; `defaultOutboundAccess=false`; no service delegation or unrelated NAT; either no NSG or an explicitly selected customer NSG | Use the default owned NSG, or leave a policy-managed NSG with the customer security team. Temporary NAT requires separate explicit consent. |
| Private endpoint subnet | Separate from the GPU subnet; IPv4 address space available for at least five private endpoints | Hosts private endpoints for Blob, File, Key Vault, ACR, and Azure ML. |
| App Service integration subnet | Separate, delegated to `Microsoft.Web/serverFarms`, and in the same region as the workload | Optional. Lets the [private App Service studio](app-service.md) reach the private Azure ML, Storage and container registry endpoints. It is not a private endpoint subnet. |

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

Copy only these values into the region's entry under `regions.<region>`:

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

The foundation can be applied with the existing NSG before the security team finishes its rules, with `compute_enabled=false`. Send them the [NSG rule handoff](customer-nsg.md), including the export command that resolves this deployment's actual CIDRs. Their rules and any firewall/egress changes must be ready **before Start**, not after the first generation attempt. After sign-off, each Start in customer-NSG mode carries the `approveCustomerNsgRules` acknowledgment; this is your acknowledgment, not an automated security assessment.

The App Service `portal` object is optional; leave it `null` until the integration subnet and `privatelink.azurewebsites.net` zone are ready. [Host the studio on a private App Service](app-service.md) explains how to look up both IDs and where they go in the JSON. Two more optional fields: `agent` enables the [video helper](video-helper.md) against your Foundry/Azure OpenAI resource (the portal identity gets the `Cognitive Services OpenAI User` role when you supply `agent.resource_id`; a key is only ever a Key Vault reference, never stored in Terraform), and `feature_dataset_builder_enabled` turns on the vision dataset builder (the example keeps it false; see [infra\README.md](../infra/README.md)). The site runs as a container: `release.yml` (component `server`) builds the image and `deploy-server.yml` deploys its digest, so no Docker is needed on an operator workstation; the private runner needs Docker.

## 5. Confirm private DNS and egress

The customer must provide existing private DNS zone IDs for all six names below, and the network owner must link/resolve them from the build runner, GPU, and App Service integration networks (and the operator workstation for any manual checks):

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

For each zone, copy its complete resource ID into the matching `private_dns_zone_ids` entry of the region in `terraform.tfvars.json`:

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

Copy the chosen workspace's complete `id` into the region's `log_analytics_workspace_id`. Terraform creates Application Insights and connects it to this existing workspace; it does not create the Log Analytics workspace.

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

## 7. Plan Entra access

Sign-in uses a single-tenant app registration, two app roles (`VideoCreator`, `StudioAdmin`) and security groups. An **identity owner** creates them manually, following [Entra bootstrap](operations/entra-bootstrap.md). No script or workflow does this, no client secret is created, and the repository never receives tenant-admin rights. The retired `Initialize-PortalAuth.ps1` is gone. The runbook needs the site's managed identity principal ID, so it runs after the foundation exists when `portal` is configured.

Before anyone browses, confirm the machines that will use the studio and the private runner resolve every private hostname the deployment created to a private endpoint subnet address. If one resolves to a public IP, access fails with 403 or connection errors, because public access is disabled.

```powershell
Resolve-DnsName <storage-account>.blob.core.windows.net -Type A
Resolve-DnsName app-<stem>.azurewebsites.net -Type A      # only when portal is configured
```

Send the network owner any hostname that resolves publicly. Depending on their DNS design, they add a conditional forwarder or an exact-host rule to their private resolver.

Plan the admin-consent step with the customer's identity team. Sign-in shows **Need admin approval** until tenant-wide consent for `openid` and `profile` exists, because Entra never allows user self-consent for assignment-required apps. The runbook lists who can grant it. Do not change `operator_principal_id` merely to get past consent.

The hosted redirect URI is `https://<site host>/auth/callback`; add a `http://localhost:<port>/auth/callback` URI only for a developer machine that signs in. This is the studio's own sign-in, not App Service Easy Auth (`/.auth/login/aad/callback`). See [private App Service](app-service.md).

## 8. Create the customer configuration file

At this point every placeholder in `terraform.tfvars.json` should be replaced. In the regional schema `compute_enabled` can only be `false`; the field cannot allocate compute, and the example is already compute-disabled. If NAT is approved, set `manage_compute_egress=true` for that region; the hosted Start then creates the NAT/public IP, which has a standing charge while it exists (until Stop verifies removal). GPU nodes are zero until a job is queued. Start remains a separate selected-release and spend acknowledgment that arms submission.

`terraform.tfvars.json` is intentionally ignored by Git. Store it in the customer-approved secret/configuration location.

The studio itself is configured separately from Terraform. After the foundation exists, the operator records each region's resource IDs, the policy ceilings and the customer network IDs in one settings document (sample: [samples/studio-settings.json.example](samples/studio-settings.json.example)). Its `Studio` section becomes the App Service application settings of the hosted site ([app-service-settings.md](operations/app-service-settings.md); every setting is in the [configuration guide](configuration.md)), and the whole document is the pipelines' `STUDIO_SETTINGS_JSON`. Terraform sets none of these settings and emits no machine contract. If you set `manage_compute_egress=true` above, set `Compute:ManageEgress` to `true` for that region here as well, and supply its NAT gateway and public IP IDs. Concurrency, timeouts, priority and queue wait are not part of this file: administrators edit them in the Administration page.

## 9. Validate before approving cost

These offline checks need no customer configuration or Azure credentials:

```powershell
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts
python -m unittest discover -s build\gpu -p "test_*.py"
python -m unittest discover -s infra -p "test_*.py"
pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1
pwsh -NoProfile -File scripts\recovery\Test-StudioRecovery.ps1
```

CI runs these, plus the pipeline tests, on every change (`.github\workflows\ci.yml`). They are code/schema checks, not proof that your customer configuration or Azure permissions/networking are ready. The foundation workflow's plan job validates the selected configuration and Azure scope before an apply is approved ([private pipelines](operations/private-pipelines.md)); the live subnet checks are unverified until it has been run. In customer-NSG mode the association is checked, not the live rules; complete the security-team handoff before Start.

## CSA handoff checklist

Give the deployment owner:

- Completed `terraform.tfvars.json` stored outside Git (or the reviewed `STUDIO_TFVARS_JSON` value for the protected environment)
- The filled-in `Studio` settings document stored outside Git (the site's application settings and `STUDIO_SETTINGS_JSON`)
- Written approval for persistent Azure costs
- Confirmation that private DNS works from the private runner and, for the hosted studio, that creators resolve the App Service private endpoint
- For a customer-managed NSG, the `gpu_nsg_rules` export and security-team sign-off before Start
- Confirmation of model-license access (the GPU build verifies and snapshots licences; it does not accept them for you)
- Approved Spot quota/capacity evidence
- The Entra bootstrap result: the registration's client ID, admin consent granted, and the creators and administrators groups populated
- For the hosted portal: the delegated integration subnet ID, the `privatelink.azurewebsites.net` zone ID, and the network owner's agreement to add client DNS for the two App Service hostnames
