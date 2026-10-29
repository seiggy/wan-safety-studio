# Runbook: set up the App Service settings for a hosted site

This sets the `Studio` configuration (and the other settings a hosted site needs) on an Azure App Service from nothing. Nothing in it has been run against live Azure; the commands are provided to be read, checked and run by an operator. The settings reference is [configuration.md](../configuration.md); the behavior behind them is [hosted lifecycle](hosted-lifecycle.md).

Terraform creates the infrastructure only. It sets no `Studio` setting and emits no machine contract; you copy resource IDs into the settings. A Terraform `studio` output, a receipt, `Studio__Foundation` and `Studio__Selection` no longer exist, and the host treats an unknown `Studio` setting as a configuration error (reported on `/health-ui`), so delete any such leftover setting from an older site (step 6).

Prerequisites: the foundation applied for every region you list (workspace, storage, registry, key vault, identities, private endpoints exist), the customer's network IDs (GPU subnet, private DNS names for the connectivity check) from the network owner, and `Contributor`-level (or settings-write) access to the web app. Use PowerShell 7 and a signed-in `az`.

## 1. Collect each ID

Read the informational Terraform outputs first. They are for you to read and copy by hand; no tool consumes them.

```powershell
terraform -chdir=infra output -json regions      # per region: resource IDs, names, effective ceilings
terraform -chdir=infra output -json portal       # the site's ID, host names, identity client and principal IDs
```

Where an output does not carry a value (or you do not have the state), read it with `az`. Replace `<...>`; every ID must be in the studio subscription.

| Setting | Command |
| --- | --- |
| `TenantId`, `SubscriptionId` | `az account show --query "{tenant:tenantId,subscription:id}"` |
| `Workspace:Id` | `az resource show -g <rg> -n <workspace> --resource-type Microsoft.MachineLearningServices/workspaces --query id -o tsv` |
| `Compute:IdentityId`, `Compute:IdentityClientId` | `az identity show -g <rg> -n <identity> --query "{id:id,clientId:clientId}"` (the identity jobs run as) |
| `Compute:Id` | `<workspace id>/computes/<name>`; the cluster does not exist until Start, so this is the ID it will have |
| `Storage:AccountId` | `az storage account show -g <rg> -n <account> --query id -o tsv` |
| `Platform:RegistryId` | `az acr show -g <rg> -n <registry> --query id -o tsv`. The host accepts only release and trainer images from this registry |
| `Network:GpuSubnetId`, `GpuSubnetCidr` | `az network vnet subnet show --ids <subnet id> --query "{id:id,cidr:addressPrefix}"` |
| `Network:GpuNsgId` | `az network nsg show -g <rg> -n <nsg> --query id -o tsv` |
| `Network:CustomerManagedNsg` | `true` when the NSG belongs to the customer; Start then needs the security owner's explicit acknowledgment of its rules |
| `Network:NatGatewayId`, `PublicIpId` (only with `ManageEgress`) | `az network nat gateway show ...` and `az network public-ip show ... --query id -o tsv` |
| `Network:PublicIpTags` (optional) | `ipTags` applied to the studio's egress public IP, only with `ManageEgress`. Use the tags your policy requires |
| `Network:PrivateConnectivityHosts` | The Start private-connectivity check uses these. The private DNS names of the region's storage (blob, file), key vault, registry (and its regional data endpoint), Azure ML API and notebooks endpoints: `az network private-endpoint list -g <rg> --query "[].customDnsConfigs[].fqdn" -o tsv`, and check the zone's A records in the customer's DNS. At least five, lowercase. Start tests that each resolves privately and answers on TCP 443 |
| `OwnershipTags` | The proof of ownership (see below). Omit to use the defaults, or copy the tags on the region's foundation resources (`az group show -n <rg> --query tags`). Must include `managedBy` |
| `ControlRegion` | The region key whose storage holds operational state; pick one of your region keys |
| `Compute:MaxNodes`, `MaxJobMinutes`, `MaxPaygHourlyUsd`, `ManageEgress` | Policy decisions, not discoverable: they are ceilings (see configuration.md). Match the Terraform inputs for the region |

Pipeline values (`Pipeline:...`) are not App Service settings; see [private pipelines](private-pipelines.md#pipeline-settings).

**Ownership tags are a safety measure.** `OwnershipTags` is the studio's proof of ownership. Before the studio creates, changes or deletes a compute, NAT gateway or public IP, or deletes anything at Stop, it requires the resource to carry exactly these tags, so it can never modify or delete a customer's resource that happens to have a similar name or ID. Keep the Terraform tags and these settings identical: if you change the tags after resources exist, those resources look foreign and Stop and Start refuse them.

## 2. Fill the sample

Copy [../samples/studio-settings.json.example](../samples/studio-settings.json.example) to a working file **outside the repository** (for example `D:\ops\studio-settings.json`) and replace every `<placeholder>`. Keep one entry per region under `Studio:Regions`, keyed by a lowercase region name. Delete the `Pipeline` section from the copy you apply to the site (the web host never reads it; it is for [the pipelines](#the-pipelines-settings-document)). The sample has no secrets; still do not commit the filled file.

## 3. Convert the nested JSON to application settings

This flattens the `Studio` object into `Studio__...` name/value pairs and writes the JSON array `az` accepts (`[{ "name", "value", "slotSetting" }]`). Objects become `__` path segments, arrays use their index, booleans are written `true`/`false`, and empty objects (such as `PublicIpTags`) produce no setting.

```powershell
function Add-Flat($node, [string]$path, $out) {
    if ($null -eq $node) { return }
    if ($node -is [System.Management.Automation.PSCustomObject]) {
        foreach ($p in $node.PSObject.Properties) { Add-Flat $p.Value "${path}__$($p.Name)" $out }
    }
    elseif ($node -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $node.Count; $i++) { Add-Flat $node[$i] "${path}__$i" $out }
    }
    else {
        $text = if ($node -is [bool]) { $node.ToString().ToLowerInvariant() } else { [string]::Format([cultureinfo]::InvariantCulture, '{0}', $node) }
        $out.Add([pscustomobject]@{ name = $path; value = $text; slotSetting = $false })
    }
}

$document = Get-Content D:\ops\studio-settings.json -Raw | ConvertFrom-Json
$settings = [System.Collections.Generic.List[object]]::new()
Add-Flat $document.Studio 'Studio' $settings

if ($settings | Where-Object { $_.value -match '[<>]' }) { throw 'A placeholder is still in the file.' }
$settings | ConvertTo-Json -AsArray | Set-Content D:\ops\studio-app-settings.json -Encoding utf8
$settings | Select-Object -First 8 | Format-Table name, value
```

Check a few names against the rule: `Studio__ControlRegion`, `Studio__Regions__eastus2__Compute__MaxNodes`, `Studio__Regions__eastus2__Network__PrivateConnectivityHosts__0`, `Studio__Regions__eastus2__OwnershipTags__managedBy`.

## 4. Add the non-Studio settings

A hosted site also needs these (names and meanings in [configuration.md](../configuration.md)). Add them to the same array or set them separately:

| Setting | Value |
| --- | --- |
| `WEBSITES_PORT` | `8080` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Generation__Provider` | `azureml` (also the default) |
| `AZURE_CLIENT_ID` | The site's user-assigned managed identity client ID (`portal` output `identityClientId`, or `az identity show`) |
| `Auth__ClientId` | Client ID of the `WAN Safety Studio` app registration ([Entra bootstrap](entra-bootstrap.md)) |
| `Auth__TenantId` | Optional: defaults to `Studio__TenantId`, then `AzureMl__TenantId` |
| `Auth__PublicOrigin` | `https://<site hostname>` |
| `FEATURE_DATASET_BUILDER_ENABLED` | `false` unless you use the dataset builder |
| `Agent__*`, `KeyVault__Uri` | Only if you use the video helper or Key Vault ([configuration.md](../configuration.md#agent-video-production-helper)) |

Do not set the flat `AzureMl__*` resource settings on a hosted site; `Studio:Regions` replaces them.

## 5. Apply and restart

```powershell
az webapp config appsettings set -g <site-rg> -n <site-name> --settings "@D:\ops\studio-app-settings.json"
az webapp config appsettings list -g <site-rg> -n <site-name> --query "[?starts_with(name,'Studio__')].name" -o tsv | Sort-Object
az webapp restart -g <site-rg> -n <site-name>
```

Applying settings already restarts the site; the explicit restart makes the moment known. Use the same file for another slot with `slotSetting` as needed.

## 6. Remove stale settings (older sites only)

A site that was set up before this change may carry `Studio__Foundation` or `Studio__Selection`. Neither exists any more and an unknown `Studio` name is reported as an error on `/health-ui`:

```powershell
az webapp config appsettings delete -g <site-rg> -n <site-name> --setting-names Studio__Foundation Studio__Selection
```

## 7. Verify

1. **Configuration health.** Open `https://<site hostname>/health-ui` (anonymous, over the private endpoint). `status` must be `ready`; `ready-with-warnings` lists non-blocking notes. The JSON form is `GET /api/health/config`. A misconfigured site still starts but answers every `/api` and `/auth` route with 503 until the listed settings are fixed. The page names each App Service setting (the `__` form), what is wrong, a made-up example and where in the portal to find the real value; it never shows a configured value or secret. Fix the settings, let the site restart, and reload the page.
2. **Liveness.** `GET https://<site hostname>/healthz` returns success. It is a liveness probe only and does not report configuration. If the site never becomes healthy, read the log: `az webapp log tail -g <site-rg> -n <site-name>`.
3. **Studio status.** Sign in as a `StudioAdmin` and open the Administration page, or call `GET /api/studio/status` from the signed-in browser session. Each configured region shows its durable phase and live Azure state; an unreadable region shows `UNREADABLE`, never off ([how to fix it](hosted-lifecycle.md#fix-a-region-that-shows-unreadable)). A 503 from `/api/studio/*` while the health page is `ready` means `Studio:Regions` is empty or the settings did not reach the site.
4. **Typical findings** on the health page:
   - `Studio:ControlRegion`: must name one of the entries under `Studio:Regions`.
   - `Studio:Regions:eastus2:Network:PrivateConnectivityHosts:0`: at least five lowercase host names are needed.
   - `Studio:Regions:eastus2:Compute:Id`: must be `<workspace id>/computes/<name>`.
   - An unknown `Studio` name: a misspelled or removed setting. Check the spelling against [configuration.md](../configuration.md).
   Fix that setting (a typo in a name is an unknown setting), re-apply and restart.
5. **Live limits.** In the Administration page review the per-region limits; they start from the ceilings and are changed there, never in App Service.

Start and arming are separate, deliberate steps ([hosted lifecycle](hosted-lifecycle.md)); nothing here creates GPU compute.

## The pipelines' settings document

The deployment pipelines do not read App Service settings. They read one explicit document shaped like the sample: a `Studio` section identical to the site's, plus the `Pipeline` section that the web host never reads. Its values are described in [private pipelines](private-pipelines.md#pipeline-settings). Provide it as the protected value `STUDIO_SETTINGS_JSON` (or the outside-checkout file named by `STUDIO_SETTINGS_PATH`) for release, promote, deploy-server and recover; `scripts\recovery\Stop-Studio.ps1` takes `-SettingsPath`. Terraform customer input is separate (`STUDIO_TFVARS_JSON`/`STUDIO_TFVARS_PATH`, and `STUDIO_BACKEND_JSON`/`STUDIO_BACKEND_PATH`). Names, approvals and environments are in [private pipelines](private-pipelines.md#explicit-configuration-and-identities).

**The same `Studio` JSON feeds both**: keep one reviewed file, apply its `Studio` object to the site with the snippet above, and store the whole file as `STUDIO_SETTINGS_JSON`. When a resource ID changes, update the one file and both places.
