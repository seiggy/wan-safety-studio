# Host the studio on a private App Service

This optional step moves the same Entra-protected studio UI from the operator's workstation into Azure App Service, so approved Video Creators can sign in from the corporate network without running anything locally. The GPU lifecycle does not change: the operator still runs Prepare, Start, and Stop.

```text
Creator browser (corporate network/VPN + private DNS)
  | HTTPS 443
  v
Private endpoint (customer PE subnet) --> App Service (Linux, Python 3.12, one instance)
                                            public network access: disabled
                                            identity: id-<stem>-portal (user-assigned)
                                            | VNet integration (customer delegated subnet), RFC1918 only
                                            +--> Blob / Azure ML private endpoints
                                            | platform outbound
                                            +--> Microsoft Entra ID, Azure Resource Manager
Browser --> private Blob endpoint (short-lived user-delegation SAS for video playback and gallery thumbnails)
```

What Terraform adds when `portal` is set:

| Resource | Name | Notes |
| --- | --- | --- |
| App Service plan | `asp-<stem>` | Linux, `B1` by default, **one worker** |
| Web app | `app-<stem>` | HTTPS only, TLS 1.2, FTP/WebDeploy basic authentication disabled, public access disabled |
| Private endpoint | `pe-<stem>-portal` | In the existing `private_endpoint_subnet_id`, registered in your `privatelink.azurewebsites.net` zone |
| Managed identity | `id-<stem>-portal` | Storage Blob Data Contributor on the studio storage account and AzureML Data Scientist on the workspace. Nothing else. |

No secret is stored on the web app. The web app signs users in with the same `WAN Safety Studio` app registration as the local portal, but it authenticates to Entra with a **federated credential that trusts its managed identity** rather than with the Key Vault client secret. Creators never receive Azure tokens.

Estimated standing cost: B1 is about USD 15/month (P0v3 about USD 58/month) and the private endpoint is about USD 7/month, before data processing. Both run while GPU nodes are at zero.

Check App Service quota in the target region before Deploy. Many subscriptions, including internal lab subscriptions, have zero Basic/Standard quota and only Premium v3 quota:

```powershell
az rest --method get `
  --url "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/locations/<location>/usages?api-version=2023-12-01" `
  --query "value[].{tier:name.localizedValue,used:currentValue,limit:limit}" --output table
```

If the `Basic` limit is `0`, either request App Service quota for that region or set `portal.sku` to `P0v3` when Premium v3 has a limit. A zero-quota tier fails Deploy with `Current Limit (B1 VMs): 0`; nothing else is changed except the portal identity and role assignments, and rerunning Deploy after the fix continues from there.

## 1. Network owner prerequisites

Ask the network owner for these items. The accelerator never creates or changes customer subnets, NSGs, route tables, DNS zones, or client DNS settings.

| Item | Requirement |
| --- | --- |
| App Service integration subnet | Same VNet/region as the studio; delegated to `Microsoft.Web/serverFarms`; not the GPU or PE subnet; `/28` minimum, `/26` recommended; not already used by another plan. |
| Integration subnet egress | Allow TCP 443 to the studio PE subnet (Blob and Azure ML API). Entra ID and ARM traffic uses the App Service platform outbound path (`vnet_route_all_enabled=false`), not this subnet. |
| PE subnet capacity | One additional private IP for `pe-<stem>-portal`. |
| Inbound rule | Allow TCP 443 from creator client ranges (VPN pool and corporate ranges) to the PE subnet. |
| Private DNS zone | An existing `privatelink.azurewebsites.net` zone, linked to the VNets whose resolvers serve creators. |
| Client DNS | Creators and the operator must resolve `app-<stem>.azurewebsites.net` and `app-<stem>.scm.azurewebsites.net` to the private endpoint IP. This is usually a conditional forwarder or NRPT rule to your private DNS resolver. The operator needs the `scm` name only for Publish. |
| Blob playback DNS | Creators' browsers load videos straight from the studio storage account. They must also resolve `<storage>.blob.core.windows.net` privately and reach it on TCP 443. |
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
| `portal.sku` (optional) | Defaults to `B1`. Allowed: `B1`-`B3`, `S1`-`S3`, `P0v3`-`P2v3`. Use a tier that has quota in the region (see the quota check above). |

Omit `portal`, or set it to `null`, to deploy no App Service resources. Adding or removing it never forces a new Prepare.

## 3. Deploy, register sign-in, publish

Run these steps from a workstation that already meets the [local dashboard](local-dashboard.md) prerequisites: PowerShell 7.4+, Azure CLI signed in as the operator, uv, and private DNS/VPN. Disarm the studio first if it is armed; Stop is always safe.

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

# 4. Package and deploy the verified release to the private web app.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Publish -Profile wan

# 5. Arm generation for the local and hosted portals.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan -ApproveGpuSpend -NoPortal
#    Add -ApproveCustomerNsgRules for a customer-managed NSG.
```

Publish:

- Verifies the prepared release against Azure (the same check Start uses).
- Checks private DNS/TCP for both App Service hostnames.
- Builds a zip that contains the studio adapter, the web UI, only the manifest-listed upstream `azureml/` runtime (hash-checked), the non-secret configuration and receipts, and Linux wheels installed from `app/uv.lock` with `--require-hashes`.
- Deploys the zip through the private SCM endpoint with Microsoft Entra authentication.
- Confirms that `/healthz` returns 200 and `/api/status` still requires sign-in.

If `WAN_STUDIO_PYPI_INDEX` is set, the wheels come from that mirror, as they do for Prepare.

At startup the web app re-checks every bundled upstream file against the prepared manifest. It refuses to start when the release, source revision, or deployment scope differs.

Open `https://app-<stem>.azurewebsites.net/` and sign in with a `WAN Safety Studio Creators` member account.

## 4. Operate

| Operator action | Effect on the hosted portal |
| --- | --- |
| Start | Writes the local gate and sets the `WAN_STUDIO_ARMED` app setting. The app restarts and generation becomes available. |
| Stop | Removes the app setting before deleting compute. The app restarts disarmed. |
| Prepare (new version) | The hosted portal keeps its older release and fails closed because the gate version will not match. Run Publish again, then Start. |
| Publish | Replaces the app content and restarts it. |

Every restart clears sign-in sessions, so creators sign in again. Sessions are held in process memory, which is why the plan is pinned to **one instance**. Do not scale out.

Terraform ignores `WAN_STUDIO_ARMED`, so Deploy never arms or disarms the hosted portal.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Deploy, Prepare or Publish fails with `No such host is known` | The workstation lost private DNS, usually because the VPN is disconnected or its gateway is off. Reconnect, confirm `Resolve-DnsName <storage>.blob.core.windows.net` returns a private IP, and rerun. |
| Browser cannot reach the site | `Resolve-DnsName app-<stem>.azurewebsites.net` must return the private endpoint IP. Check the VPN and the NSG inbound 443 rule on the PE subnet. |
| `403 This portal accepts only its private App Service host` | Open the exact `https://app-<stem>.azurewebsites.net/` URL, not the IP address. |
| Sign-in returns `AADSTS700213`/`AADSTS70021` (no matching federated identity) | Rerun `Initialize-PortalAuth.ps1 -ApproveIdentityChanges`. The federated subject must equal the portal identity's principal ID. |
| Sign-in shows a redirect URI mismatch | Rerun `Initialize-PortalAuth.ps1` after Deploy, then Publish. |
| "GPU generation is not armed" | Run Start after the latest Publish. The published and armed release versions must be the same. |
| Videos do not play | The browser must resolve and reach the storage account's private blob endpoint. |
| Publish fails at `az webapp deploy` | The `scm` hostname must resolve privately. Your account needs Website Contributor (or Contributor) on the web app. |
| App does not become healthy | Stream logs with `az webapp log tail -g <rg> -n app-<stem>` over the private SCM endpoint. Startup errors name the failed check. |

## Remove

Set `portal` to `null` and run Deploy. Terraform removes only the web app, plan, private endpoint, identity, and its role assignments. Deploy refuses plans that delete resources, so review the plan and apply it with Terraform directly after approval. Then ask the identity owner to remove the `wan-portal-<deployment>` federated credential and the HTTPS redirect URI from the app registration.
