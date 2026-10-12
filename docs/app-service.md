# Host the studio on a private App Service

This optional step moves the studio (the .NET server and its React UI, in one container) from the operator's workstation into Azure App Service, so approved Video Creators can sign in from the corporate network without running anything locally. The GPU lifecycle does not change: the operator still runs Prepare, Start, and Stop. The hosted studio uses the **Azure ML generation provider** (`Generation__Provider=azureml`): it submits Wan 2.2 command jobs to the studio workspace, stores inputs and videos in the studio blob container, and shows them in the same gallery as a local ComfyUI run.

If your own infrastructure automation creates the App Service, skip this Terraform option and follow [Host the studio as a container](app-service-docker.md) instead.

```text
Creator browser (corporate network/VPN + private DNS)
  | HTTPS 443
  v
Private endpoint (customer PE subnet) --> App Service (Linux container, one instance)
                                            image: <registry>/wan-safety-studio-server:<tag>, port 8080
                                            public network access: disabled
                                            identity: id-<stem>-portal (user-assigned)
                                            | VNet integration (customer delegated subnet), RFC1918 only
                                            +--> Blob / Azure ML / container registry private endpoints
                                            | platform outbound
                                            +--> Microsoft Entra ID, Azure Resource Manager, Foundry (video helper)
Browser --> /api/media/... on the same host (the server streams videos from the private container)
```

What Terraform adds when `portal` is set:

| Resource | Name | Notes |
| --- | --- | --- |
| App Service plan | `asp-<stem>` | Linux, `B1` by default, **one worker** |
| Web app | `app-<stem>` | Container from the studio registry, pulled over the VNet with the portal identity. HTTPS only, TLS 1.2, FTP/WebDeploy basic authentication disabled, public access disabled, health check `/healthz` |
| Private endpoint | `pe-<stem>-portal` | In the existing `private_endpoint_subnet_id`, registered in your `privatelink.azurewebsites.net` zone |
| Managed identity | `id-<stem>-portal` | Storage Blob Data Contributor on the studio storage account, AzureML Data Scientist on the workspace, **AcrPull** on the studio registry, and (only when you set `agent.resource_id`) Cognitive Services OpenAI User on that Foundry resource. Nothing else. |

No secret is stored on the web app. The web app signs users in with the same `WAN Safety Studio` app registration as the local studio, but it authenticates to Entra with a **federated credential that trusts its managed identity** rather than with the Key Vault client secret. Creators never receive Azure tokens.

### Settings Terraform puts on the web app

| App setting | Value |
| --- | --- |
| `WEBSITES_PORT` | `8080` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Generation__Provider` | `azureml` |
| `AZURE_CLIENT_ID` | The portal identity's client ID (the standard Azure SDK variable: Azure SDK calls, the registry pull and the sign-in federated assertion all use it) |
| `AzureMl__SubscriptionId`, `AzureMl__TenantId`, `AzureMl__ResourceGroup`, `AzureMl__Workspace`, `AzureMl__Compute`, `AzureMl__ComputeIdentityClientId`, `AzureMl__StorageAccount`, `AzureMl__Container`, `AzureMl__Datastore` | The studio's own resources and compute identity, taken from the module's resources. No secrets. |
| `Auth__TenantId` | The configured tenant |
| `FEATURE_DATASET_BUILDER_ENABLED` | `false` unless you set `feature_dataset_builder_enabled` (see [below](#optional-video-helper-and-dataset-builder)) |
| `Agent__*` | Only when you set the `agent` variable |
| `KeyVault__Uri` | Only when you set `key_vault_uri`; the server then reads configuration secrets such as `Auth--ClientSecret` from that vault. Setting `key_vault_id` as well grants the portal identity **Key Vault Secrets User** on it. |

Terraform **ignores** these settings, which the operator scripts own: `AzureMl__Armed` (Start/Stop), and `AzureMl__Release__EnvironmentId`, `AzureMl__Release__ModelsRef`, `AzureMl__Release__CodeUri`, `Auth__ClientId`, `Auth__PublicOrigin` and the container image (Publish). A newly created site shows a placeholder image tag until the first Publish, so it will not start before then. See the [configuration reference](configuration.md) for every key the server reads.

Estimated standing cost: B1 is about USD 15/month (P0v3 about USD 58/month) and the private endpoint is about USD 7/month, before data processing. Both run while GPU nodes are at zero.

Check App Service quota in the target region before Deploy. Many subscriptions, including internal lab subscriptions, have zero App Service quota in some regions:

```powershell
az rest --method get `
  --url "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/locations/<location>/usages?api-version=2023-12-01" `
  --query "value[].{tier:name.localizedValue,used:currentValue,limit:limit}" --output table
```

A `Basic` or `Standard` limit of `0` means that tier cannot be created in the region; `-1` means no tier limit. A Premium v3 **core** limit does not prove that a Premium v3 plan can be created: Azure also enforces a per-SKU VM limit (for example `P0v3 VMs`, `P1v3 VMs`) that this API does not show. In a region with zero Basic quota, request an App Service quota increase for the SKU you will use (1 VM is enough) before Deploy. Internal Microsoft subscriptions follow the process linked from the error's `https://aka.ms/antquotahelp`. Customer subscriptions use an Azure support request of type **Service and subscription limits (quotas)**.

Regional virtual network integration requires the web app and `portal.subnet_id` to be in the same region, so a region that has quota helps only if the network owner provides a portal subnet there.

A zero-quota SKU fails Deploy with `Current Limit (<sku> VMs): 0`. Nothing is deleted; only the portal identity and its role assignments exist at that point. Rerunning Deploy after the quota is granted continues from there.

## 1. Network owner prerequisites

Ask the network owner for these items. The accelerator never creates or changes customer subnets, NSGs, route tables, DNS zones, or client DNS settings.

| Item | Requirement |
| --- | --- |
| App Service integration subnet | Same VNet/region as the studio; delegated to `Microsoft.Web/serverFarms`; not the GPU or PE subnet; `/28` minimum, `/26` recommended; not already used by another plan. |
| Integration subnet egress | Allow TCP 443 to the studio PE subnet (Blob, Azure ML API and the container registry, including its data endpoint). Entra ID and ARM traffic uses the App Service platform outbound path (`vnet_route_all_enabled=false`), not this subnet. |
| PE subnet capacity | One additional private IP for `pe-<stem>-portal`. |
| Inbound rule | Allow TCP 443 from creator client ranges (VPN pool and corporate ranges) to the PE subnet. |
| Private DNS zone | An existing `privatelink.azurewebsites.net` zone, linked to the VNets whose resolvers serve creators. |
| Client DNS | Creators and the operator must resolve `app-<stem>.azurewebsites.net` and `app-<stem>.scm.azurewebsites.net` to the private endpoint IP. This is usually a conditional forwarder or NRPT rule to your private DNS resolver. |
| Video helper (optional) | If you enable the `agent` variable, the integration subnet's platform egress must reach the Foundry endpoint, or the endpoint must be reachable through the integration subnet. |
| Resource provider | `Microsoft.Web` registered in the subscription. |

Verify the subnet delegation:

```powershell
az network vnet subnet show `
  --ids <app-service-integration-subnet-id> `
  --query "{id:id,prefix:addressPrefix,delegations:delegations[].serviceName,links:serviceAssociationLinks[].name}" `
  --output json
```

`delegations` must contain `Microsoft.Web/serverFarms`. `links` should be empty, which means no other plan is using the subnet.

Find the App Service private DNS zone ID:

```powershell
az network private-dns zone list `
  --query "[?name=='privatelink.azurewebsites.net'].{id:id,resourceGroup:resourceGroup}" `
  --output table
```

## 2. Add `portal` to `terraform.tfvars.json`

Add this object to the existing file, beside the other network fields. Do not overwrite your completed values with the example file.

```json
"portal": {
  "subnet_id": "/subscriptions/<sub>/resourceGroups/<network-rg>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<app-service-subnet>",
  "dns_zone_id": "/subscriptions/<sub>/resourceGroups/<dns-rg>/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net"
}
```

| Field | Where it comes from |
| --- | --- |
| `portal.subnet_id` | The `id` from the subnet command above |
| `portal.dns_zone_id` | The `id` from the zone command above |
| `portal.sku` (optional) | Defaults to `B1`. Allowed: `B1`-`B3`, `S1`-`S3`, `P0v3`-`P2v3`. Use a SKU that has quota in the region (see the quota check above). |

Omit `portal`, or set it to `null`, to deploy no App Service resources. Adding or removing it never forces a new Prepare.

### Optional: video helper and dataset builder

Both are off by default.

```json
"agent": {
  "endpoint": "https://<resource>.openai.azure.com/openai/v1/",
  "model": "<deployment-name>",
  "resource_id": "/subscriptions/<sub>/resourceGroups/<ai-rg>/providers/Microsoft.CognitiveServices/accounts/<resource>",
  "wire_api": "responses"
},
"feature_dataset_builder_enabled": true
```

`agent` turns on the [Video Production Helper](video-helper.md) by adding the `Agent__*` app settings (`Agent__Endpoint`, `Agent__Model`, `Agent__Auth`, and optionally `Agent__WireApi`, `Agent__Vision`). Fields: `endpoint`, `model` (both required), `auth`, `resource_id`, `wire_api` (`completions` or `responses`), `vision` (`false` for text-only models), `api_key_secret_uri`.

The helper has four auth methods ([details](video-helper.md#authentication-methods)); two apply to this host:

| `agent.auth` | Use | Terraform behaviour |
| --- | --- | --- |
| `UserAssignedManagedIdentity` (default) | Foundry / Azure OpenAI | Sets `Agent__ManagedIdentityClientId` to the portal identity. When `resource_id` is given, also assigns **Cognitive Services OpenAI User** to that identity on the resource. Without `resource_id`, ask the Foundry owner to grant the role to `id-<stem>-portal`. |
| `ApiKey` | OpenAI-compatible servers or Foundry key auth | The key is **never stored in Terraform**. Supply `api_key_secret_uri` (a Key Vault secret URI); Terraform sets `Agent__ApiKey` to a Key Vault reference, and you must grant `id-<stem>-portal` **Key Vault Secrets User** on that vault and keep it reachable from the site. Without `api_key_secret_uri`, the configuration is rejected. |

`SystemAssignedManagedIdentity` and `AzureCli` do not apply (the site has only the user-assigned identity and no CLI profile). `AzureCli` is for local development.

`feature_dataset_builder_enabled` sets `FEATURE_DATASET_BUILDER_ENABLED=true` for the [vision dataset builder](vision-training-plan.md).

## 3. Deploy, register sign-in, publish

Run these steps from a workstation that already meets the [local dashboard](local-dashboard.md) prerequisites: PowerShell 7.4+, Azure CLI signed in as the operator, uv, **Docker with Linux containers** (Publish builds the server image), and private DNS/VPN. Disarm the studio first if it is armed; Stop is always safe.

```powershell
# 1. Create the plan, web app, identity and private endpoint (no GPU change).
#    Add -ApproveGpuSpend -ApproveCustomerNsgRules if compute_enabled is true, as for any Deploy.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Deploy -ApprovePersistentCosts

# 2. Add the HTTPS redirect URI and the managed-identity federated credential.
.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges
```

After step 1, send the network owner the private endpoint IP so they can create client DNS:

```powershell
az network private-endpoint show -g <studio-resource-group> -n pe-<stem>-portal `
  --query "customDnsConfigs[].{fqdn:fqdn,ip:ipAddresses[0]}" --output table
```

When both `app-<stem>.azurewebsites.net` and `app-<stem>.scm.azurewebsites.net` resolve to that IP from your workstation, continue:

```powershell
# 3. Only when Prepare reports that source/adapter code changed (for example after upgrading this repo).
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Prepare -Profile wan

# 4. Build the server image, push it to the private registry and point the web app at it.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Publish -Profile wan

# 5. Arm generation for the local and hosted studios.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan -ApproveGpuSpend -NoPortal
#    Add -ApproveCustomerNsgRules for a customer-managed NSG.
```

Publish:

- Verifies the prepared release against Azure (the same check Start uses) and checks private DNS/TCP for both App Service hostnames and every private-endpoint host (registry included).
- Builds `src\VideoSafetyStudio.Server\Dockerfile` with the repo root as context (it builds the React UI, publishes the .NET server for `linux-x64`, includes the Copilot runtime that the video helper needs, and runs as a non-root user on port 8080), tagged `<registry>.azurecr.io/wan-safety-studio-server:<release version>-<12-character source hash>`.
- Pushes the image through `az acr login` to the private registry (VPN or private network required, as for Prepare).
- Through ARM, sets the web app image, `AzureMl__Release__EnvironmentId`, `AzureMl__Release__ModelsRef` and `AzureMl__Release__CodeUri` (from the prepared manifest), `Auth__ClientId` (from `portal-auth.json`) and `Auth__PublicOrigin` (`https://<site hostname>`), then restarts the site and waits for `/healthz`.
- Confirms that `/api/status` still requires sign-in.

Package restores during the image build use the repository's `nuget.config` and `web\.npmrc`; if your feed needs credentials, pass them as BuildKit secrets (see [the container guide](app-service-docker.md#build-the-image)). The Copilot CLI is downloaded from GitHub releases by the SDK build step, so the build workstation needs that egress.

Open `https://app-<stem>.azurewebsites.net/` and sign in with a `WAN Safety Studio Creators` member account.

## 4. Operate

| Operator action | Effect on the hosted studio |
| --- | --- |
| Start | Writes the local gate and sets the `AzureMl__Armed` app setting to `true`. The app restarts and generation becomes available. |
| Stop | Sets `AzureMl__Armed` to `false` before deleting compute. The app restarts disarmed. |
| Prepare (new version) | Prepare needs a prior Stop, so the site is disarmed; it keeps its older release pointers until you run Publish again, then Start. |
| Publish | Pushes a new image, updates the release pointers and restarts the app. |

Every restart clears sign-in sessions, so creators sign in again. A restart also ends any batch that is still submitting; jobs that were already created continue (see [how submission works](local-dashboard.md#5-create-videos)). Sessions are held in process memory, which is why the plan is pinned to **one instance**. Do not scale out.

Terraform ignores `AzureMl__Armed`, `AzureMl__Release__*`, `Auth__ClientId`, `Auth__PublicOrigin` and the image, so Deploy never arms, disarms or rolls back the hosted studio.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Deploy, Prepare or Publish fails with `No such host is known` | The workstation lost private DNS, usually because the VPN is disconnected or its gateway is off. Reconnect, confirm `Resolve-DnsName <storage>.blob.core.windows.net` returns a private IP, and rerun. |
| Browser cannot reach the site | `Resolve-DnsName app-<stem>.azurewebsites.net` must return the private endpoint IP. Check the VPN and the NSG inbound 443 rule on the PE subnet. |
| `403` / host mismatch | Open the exact `https://app-<stem>.azurewebsites.net/` URL, not the IP address. The server only accepts its public host. |
| Sign-in returns `AADSTS700213`/`AADSTS70021` (no matching federated identity) | Rerun `Initialize-PortalAuth.ps1 -ApproveIdentityChanges`. The federated subject must equal the portal identity's principal ID. |
| Sign-in shows a redirect URI mismatch | Rerun `Initialize-PortalAuth.ps1` after Deploy, then Publish. |
| "GPU generation is not armed" | Run Start after the latest Publish; the site must read `AzureMl__Armed=true`. |
| Videos do not play or the gallery is empty | The site identity needs Storage Blob Data Contributor on the studio storage account and a working private blob endpoint from the integration subnet. |
| The site is stuck on the placeholder image or logs `ImagePullFailure` | Run Publish. For pull errors check that `id-<stem>-portal` has AcrPull on the registry, that the integration subnet reaches the registry private endpoint (and data endpoint), and that `vnet_image_pull_enabled` is still on. |
| Publish: "not configured to pull with the portal managed identity" | Run Deploy so Terraform restores `acr_use_managed_identity_credentials`. |
| App does not become healthy | Stream logs with `az webapp log tail -g <rg> -n app-<stem>` over the private SCM endpoint. Startup errors name the failed check (for example a missing sign-in setting or `AzureMl__*` setting). |
| The video helper says it is not configured | Check the `Agent__*` app settings and the role assignment ([video helper](video-helper.md)). |

## Remove

Set `portal` to `null` and run Deploy. Terraform removes only the web app, plan, private endpoint, identity, and its role assignments. Deploy refuses plans that delete resources, so review the plan and apply it with Terraform directly after approval. Then ask the identity owner to remove the `wan-portal-<deployment>` federated credential and the HTTPS redirect URI from the app registration.
