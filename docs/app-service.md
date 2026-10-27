# Host the studio on a private App Service

This optional step runs the studio (the .NET server and its React UI, in one container) in Azure App Service, so approved Video Creators can sign in from the corporate network without running anything locally. Start, Stop and release selection are hosted lifecycle operations in this server ([hosted lifecycle](operations/hosted-lifecycle.md)); GitHub Actions build and deploy it ([private pipelines](operations/private-pipelines.md)). The hosted studio uses the **Azure ML generation provider** (`Generation__Provider=azureml`): it submits Wan 2.2 command jobs to the studio workspace, stores inputs and videos in the studio blob container, and shows them in the same gallery as a local ComfyUI run. None of this has been verified against live Azure.

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
| Managed identity | `id-<stem>-portal` | Storage Blob Data Contributor on the studio storage account, AzureML Data Scientist on the workspace, **AcrPull** on the studio registry (plus whatever role grants the Terraform inputs request; see [infra\README.md](../infra/README.md)). Nothing else. |

No secret is stored on the web app. The web app signs users in with the `WAN Safety Studio` app registration created by the identity owner ([Entra bootstrap](operations/entra-bootstrap.md)), authenticating to Entra with a **federated credential that trusts its managed identity**; no client secret exists. Creators never receive Azure tokens.

### Settings the operator puts on the web app

Terraform creates the site, identity and private endpoint only. It sets no `Studio`, `AzureMl`, `Auth` or `Agent` application setting and emits no machine contract; the operator sets every setting the server needs. The step-by-step procedure (collect IDs, fill a sample, convert it to application settings, restart, verify) is the runbook [app-service-settings.md](operations/app-service-settings.md); every setting and its default is in the [configuration guide](configuration.md#configure-the-app-in-azure-app-service).

In summary a hosted site needs: the `Studio__...` topology (regions, resource IDs, ceilings), `AZURE_CLIENT_ID` (the site identity's client ID), `Auth__ClientId` and `Auth__PublicOrigin` (`Auth__TenantId` is optional: it falls back to `Studio__TenantId`), `WEBSITES_PORT=8080`, `ASPNETCORE_ENVIRONMENT=Production`, `Generation__Provider=azureml`, and optionally `Agent__*`, `KeyVault__Uri` and `FEATURE_DATASET_BUILDER_ENABLED`. Limits that administrators change while it runs (concurrency, timeouts, priority, queue wait) are edited in the Administration page, not in App Service. A newly created site shows a placeholder image tag until the first server deployment, so it will not start before then. The durable submission fence, not `AzureMl__Armed`, opens generation on a host with `Studio:Regions`.

Estimated standing cost: B1 is about USD 15/month (P0v3 about USD 58/month) and the private endpoint is about USD 7/month, before data processing. Both run while GPU nodes are at zero.

Check App Service quota in the target region before the foundation apply. Many subscriptions, including internal lab subscriptions, have zero App Service quota in some regions:

```powershell
az rest --method get `
  --url "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/locations/<location>/usages?api-version=2023-12-01" `
  --query "value[].{tier:name.localizedValue,used:currentValue,limit:limit}" --output table
```

A `Basic` or `Standard` limit of `0` means that tier cannot be created in the region; `-1` means no tier limit. A Premium v3 **core** limit does not prove that a Premium v3 plan can be created: Azure also enforces a per-SKU VM limit (for example `P0v3 VMs`, `P1v3 VMs`) that this API does not show. In a region with zero Basic quota, request an App Service quota increase for the SKU you will use (1 VM is enough) before the foundation apply. Internal Microsoft subscriptions follow the process linked from the error's `https://aka.ms/antquotahelp`. Customer subscriptions use an Azure support request of type **Service and subscription limits (quotas)**.

Regional virtual network integration requires the web app and `portal.subnet_id` to be in the same region, so a region that has quota helps only if the network owner provides a portal subnet there.

A zero-quota SKU fails the foundation apply with `Current Limit (<sku> VMs): 0`. Nothing is deleted; only the portal identity and its role assignments exist at that point. Rerunning the apply after the quota is granted continues from there.

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
| Video helper (optional) | If you enable the video helper (`Agent__*` settings), the integration subnet's platform egress must reach the Foundry endpoint, or the endpoint must be reachable through the integration subnet. |
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

Omit `portal`, or set it to `null`, to deploy no App Service resources. The configuration shown here is the regional-root input; see [infra\README.md](../infra/README.md) for the full schema.

### Optional: video helper and dataset builder

Both are off by default and are configured with application settings, not Terraform:

| Setting | Meaning |
| --- | --- |
| `Agent__Endpoint`, `Agent__Model` | Turn on the [Video Production Helper](video-helper.md). Both required |
| `Agent__Auth` | `UserAssignedManagedIdentity` (then also `Agent__ManagedIdentityClientId` = the site identity's client ID) or `ApiKey` (then `Agent__ApiKey`, as an App Service Key Vault reference such as `@Microsoft.KeyVault(SecretUri=...)`; never a plaintext value in a file) |
| `Agent__WireApi`, `Agent__Vision` | Optional: `completions` or `responses`; `false` for text-only models |
| `FEATURE_DATASET_BUILDER_ENABLED` | `true` shows the [vision dataset builder](vision-training-plan.md); default `false` |

For managed-identity auth, ask the Foundry owner to grant **Cognitive Services OpenAI User** on the resource to the site's identity. For an API key from Key Vault, grant the site identity **Key Vault Secrets User** on that vault and keep it reachable from the site. `SystemAssignedManagedIdentity` and `AzureCli` do not apply (the site has only the user-assigned identity and no CLI profile); `AzureCli` is for local development. The full list is in the [configuration guide](configuration.md#agent-video-production-helper).
## 3. Create the site, register sign-in, deploy the server

Every step below is a protected, manually dispatched workflow or one-time manual work; none has been dispatched or live-validated in this repository. Approvals, environments and the configuration each needs are in [private pipelines](operations/private-pipelines.md).

1. **Foundation.** Dispatch `foundation.yml`. The plan job (`studio-plan`) saves a plan; review that run's `reviewed.json`; a separately approved apply job (`studio-apply`) applies that exact saved plan. This creates the plan, web app, identity and private endpoint and no GPU compute. A site that exists only with a placeholder image does not start yet. For a deployment that still has v1 state, complete the verified-OFF migration in [infra\README.md](../infra/README.md) first.
2. **Private endpoint DNS.** After step 1, send the network owner the private endpoint IP so they can create client DNS:

   ```powershell
   az network private-endpoint show -g <studio-resource-group> -n pe-<stem>-portal `
     --query "customDnsConfigs[].{fqdn:fqdn,ip:ipAddresses[0]}" --output table
   ```

   Continue when both `app-<stem>.azurewebsites.net` and `app-<stem>.scm.azurewebsites.net` resolve to that IP from the machine that will browse or run the private runner.
3. **Sign-in.** An identity owner follows [Entra bootstrap](operations/entra-bootstrap.md): the registration, roles `VideoCreator` and `StudioAdmin`, groups, the federated credential for this site's managed identity, and admin consent. The registration's client ID becomes the `Auth__ClientId` application setting; `Auth__PublicOrigin` is `https://<site hostname>`.
4. **Application settings.** Set the `Studio__...` topology and the other settings by hand, following [app-service-settings.md](operations/app-service-settings.md), then restart the site.
5. **GPU release.** Dispatch `release.yml` with component `gpu` (build-only image, verified model and licence snapshot, sealed artifact), then `promote.yml` per region. The create-only regional binding is written last.
6. **Server.** Dispatch `release.yml` with component `server`, then `deploy-server.yml` (environment `studio-server`), which deploys the approved artifact's registry digest. It does not build or arm.
7. **Arm.** Sign in as a `StudioAdmin`, select the promoted release in the Administration page while the region is Off, and use Start with explicit GPU spend approval (and the customer-NSG acknowledgment when an existing NSG is used). Stop releases compute. See [hosted lifecycle](operations/hosted-lifecycle.md).

Package restores during the image build use the repository's `nuget.config` and `web\.npmrc`; see [the container guide](app-service-docker.md#build-the-image) for credentials.

Open `https://app-<stem>.azurewebsites.net/` and sign in with a member of the creators group.

## 4. Operate

| Action | Effect on the hosted studio |
| --- | --- |
| Start (Administration page, StudioAdmin) | Verifies the selected release, then creates one private Spot node and arms the region through the durable fence. |
| Stop (Administration page, or `POST /api/studio/stop`) | Fences the host, releases compute and proves it. Works without Python, Docker or Terraform. |
| New GPU release | `release.yml` (gpu) and `promote.yml`; then select it in the Administration page while the region is Off. |
| New server version | `release.yml` (server), then `deploy-server.yml`. The site restarts. |
| Host unreachable or broken | `scripts\recovery\Stop-Studio.ps1` (or `recover.yml`) releases compute independently of the host. |

Every restart clears sign-in sessions, so creators sign in again. A restart also ends any batch that is still submitting; jobs that were already created continue (see [how submission works](local-dashboard.md#5-create-videos)). Sessions are held in process memory, which is why the plan is pinned to **one instance**. Do not scale out.

Because Terraform sets no application settings and does not manage the container image (`deploy-server.yml` does), a foundation apply never rolls back the hosted studio's settings, release or sign-in.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| A workflow or the runner fails with `No such host is known` | The machine lost private DNS, usually because the VPN is disconnected or its gateway is off. Confirm `Resolve-DnsName <storage>.blob.core.windows.net` returns a private IP, and rerun. |
| Browser cannot reach the site | `Resolve-DnsName app-<stem>.azurewebsites.net` must return the private endpoint IP. Check the VPN and the NSG inbound 443 rule on the PE subnet. |
| `403` / host mismatch | Open the exact `https://app-<stem>.azurewebsites.net/` URL, not the IP address. The server only accepts its public host. |
| Sign-in returns `AADSTS700213`/`AADSTS70021` (no matching federated identity) | The federated subject must equal the portal identity's principal ID ([Entra bootstrap](operations/entra-bootstrap.md)). |
| Sign-in shows a redirect URI mismatch | The registration needs `https://app-<stem>.azurewebsites.net/auth/callback`, and `Auth__PublicOrigin` must match. |
| "GPU generation is not armed" | The region is not `armed`. A StudioAdmin runs Start in the Administration page after the release is promoted and selected. |
| Videos do not play or the gallery is empty | The site identity needs Storage Blob Data Contributor on the studio storage account and a working private blob endpoint from the integration subnet. |
| The site is stuck on the placeholder image or logs `ImagePullFailure` | Run `deploy-server.yml`. For pull errors check that `id-<stem>-portal` has AcrPull on the registry, that the integration subnet reaches the registry private endpoint (and data endpoint), and that `vnet_image_pull_enabled` is still on. |
| App does not become healthy | Stream logs with `az webapp log tail -g <rg> -n app-<stem>` over the private SCM endpoint. Startup errors name the failed check; a configuration problem does not stop the host, which serves `/health-ui` (anonymous) listing the setting names to fix. Settings are read at start, so restart after changing them. |
| The video helper says it is not configured | Check the `Agent__*` app settings and the role assignment ([video helper](video-helper.md)). |

## Remove

Set `portal` to `null` and run the foundation workflow. Terraform removes only the web app, plan, private endpoint, identity, and its role assignments. The foundation workflow rejects plans that delete resources, so review the plan and apply it with Terraform directly after approval. Then ask the identity owner to remove the federated credential and the HTTPS redirect URI from the app registration.
