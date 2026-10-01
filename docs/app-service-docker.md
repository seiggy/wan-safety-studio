# Host the studio portal as a container on an App Service you create

Use this guide when your own infrastructure automation creates the App Service instead of Terraform's `portal` option in [app-service.md](app-service.md). You build the portal image, push it to your private Azure Container Registry (ACR), and configure the web app by hand.

In this mode the studio's Terraform and scripts do not touch the web app. Deploy, Prepare, Start, and Stop still run from the operator workstation as usual. **Start and Stop do not arm or disarm this web app**; you do that with one app setting (see [section 5](#5-operate)).

The container reads its configuration from environment variables (App Service app settings), so no `config.json` or `foundation.json` is baked into the image. The image contains only code, the hash-checked upstream runtime, and two non-secret receipts.

## 1. Prerequisites

| Requirement | Notes |
| --- | --- |
| Studio deployed and prepared | Run Deploy, `Initialize-PortalAuth.ps1`, and Prepare as in the [local dashboard](local-dashboard.md) guide. **Run Prepare again after updating to this version of the repo.** The image relies on the prepared `config.py` helper that reads environment variables. |
| Operator cache | `%LOCALAPPDATA%\wan-safety-studio\<subscription-id>-<deployment_name>`, or the folder in `-CacheDirectory` / `$env:WAN_STUDIO_CACHE`. It holds `foundation.json`, `prepared-wan.json`, `portal-auth.json`, and the prepared `source-<version>` folder. |
| Docker | Docker with BuildKit (Docker Desktop, or Docker Engine 23+) and access to `pypi.org`, or to your mirror (see `PYPI_INDEX` below). |
| Private ACR | Your registry, for example `<registry>.azurecr.io`, and permission to push (`AcrPush`). |
| Network | The same network prerequisites as the Terraform-hosted portal: [app-service.md section 1](app-service.md#1-network-owner-prerequisites). A private ACR also needs TCP 443 egress from the integration subnet to the ACR private endpoint, plus `privatelink.azurecr.io` DNS. |
| Web app host name | Decide the public origin before building, for example `https://<app-name>.azurewebsites.net` or a custom domain. The sign-in redirect URI inside the image depends on it. |

## 2. Build and push the image

Run these commands from the repository root in PowerShell 7.

```powershell
$cache  = "$env:LOCALAPPDATA\wan-safety-studio\<subscription-id>-<deployment_name>"
$origin = 'https://<app-name>.azurewebsites.net'   # lowercase; no path, port or trailing slash
$image  = '<registry>.azurecr.io/wan-safety-studio-portal:<tag>'

# Stage the two receipts that go into the image.
$stage = Join-Path $env:TEMP 'wan-portal-release'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item "$cache\prepared-wan.json" $stage
$auth = Get-Content "$cache\portal-auth.json" -Raw | ConvertFrom-Json -AsHashtable
$auth.redirectUri = "$origin/auth/callback"
$auth | ConvertTo-Json | Set-Content -Encoding utf8 "$stage\portal-auth.json"

# The prepared, patched upstream runtime.
$sourceRoot = (Get-Content "$cache\prepared-wan.json" -Raw | ConvertFrom-Json).sourceRoot

docker build --platform linux/amd64 -f app/Dockerfile `
  --build-context "upstream=$sourceRoot" `
  --build-context "release=$stage" `
  -t $image app

az acr login --name <registry>
docker push $image
Remove-Item $stage -Recurse -Force
```

Notes:

- Python packages are installed from `app/uv.lock` with `--require-hashes`, the same pinned wheels that Prepare and Publish use. To use a PyPI mirror, add `--build-arg PYPI_INDEX=<simple-index-url>`.
- `portal-auth.json` and `prepared-wan.json` contain IDs only, no secrets. The image contains no Azure credentials.
- At startup the container re-hashes every bundled upstream file and checks the source revision, profile, and deployment scope against `prepared-wan.json`. If anything differs, it refuses to start.
- **Rebuild and push after every Prepare** that reports new source or adapter code, and after changing the origin.

## 3. Create the web app

Create these resources with your own automation. The values mirror what Terraform creates for the built-in portal ([infra/portal.tf](../infra/portal.tf)).

| Setting | Required value |
| --- | --- |
| Plan | Linux, any SKU with quota in the region. **One instance.** Sign-in sessions live in process memory, so do not scale out or enable autoscale. |
| Publishing model | Container, image `<registry>.azurecr.io/wan-safety-studio-portal:<tag>`, platform `linux/amd64`. No startup command; the image's `CMD` starts the portal. |
| Identity | One **user-assigned** managed identity. The portal requires a client ID, so a system-assigned identity is not supported. |
| Registry pull | Pull with the managed identity: set `acrUseManagedIdentityCreds=true` and `acrUserManagedIdentityID=<identity client ID>`. If the ACR is private-endpoint only, also enable image pull over VNet (`vnetImagePullEnabled=true`). |
| HTTPS | HTTPS only, minimum TLS 1.2, FTP/FTPS disabled, basic publishing credentials disabled. |
| Always On | On. |
| Health check | Path `/healthz`. |
| Inbound | Private endpoint (`sites`) in the studio's private endpoint subnet, registered in `privatelink.azurewebsites.net`. Public network access disabled. |
| Outbound | Regional VNet integration on a subnet delegated to `Microsoft.Web/serverFarms`. The app must reach the Blob and Azure ML private endpoints. It must also reach Microsoft Entra ID (`login.microsoftonline.com`) and Azure Resource Manager (`management.azure.com`). Terraform leaves **Route All** off so those go out the platform path. If you turn Route All on, allow them through your firewall. |
| Session affinity | Off. |

Give the identity these role assignments. Take the IDs from `foundation.json` in the operator cache.

| Role | Scope |
| --- | --- |
| Storage Blob Data Contributor | `storageAccountId` |
| AzureML Data Scientist | `workspaceId` |
| AcrPull | Your ACR |

The identity needs no other roles. Example with Azure CLI:

```powershell
$principalId = az identity show -g <rg> -n <identity-name> --query principalId -o tsv
az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
  --role 'Storage Blob Data Contributor' --scope <storageAccountId>
az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
  --role 'AzureML Data Scientist' --scope <workspaceId>
az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal `
  --role AcrPull --scope <acr-resource-id>

$site = az webapp show -g <rg> -n <app-name> --query id -o tsv
az resource update --ids "$site/config/web" `
  --set properties.acrUseManagedIdentityCreds=true properties.acrUserManagedIdentityID=<identity-client-id>
az resource update --ids $site --set properties.vnetImagePullEnabled=true   # private-only ACR
```

## 4. App settings

| Name | Required | Value | Where to get it |
| --- | --- | --- | --- |
| `WAN_STUDIO_FOUNDATION_JSON` | Yes | The 11 runtime fields below, as one JSON string | See [Foundation fields](#foundation-fields). |
| `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` | Yes | Client ID (GUID) of the web app's user-assigned identity | `az identity show -g <rg> -n <identity-name> --query clientId -o tsv` |
| `WAN_STUDIO_PUBLIC_ORIGIN` | Yes | The origin used when building the image, for example `https://<app-name>.azurewebsites.net` | Your web app host name, or a custom domain bound to the app. Lowercase, no path, port, or trailing slash. Must match the redirect URI in the image. |
| `WEBSITES_PORT` | Yes | `8000` | Fixed; the container listens on port 8000. |
| `WAN_STUDIO_ARMED` | Only while armed | The content of `armed.json` from the operator cache, as one JSON string | Written by Start. See [section 5](#5-operate). |

Do **not** set `WAN_STUDIO_CONFIG_JSON`, `WAN_STUDIO_CONFIG`, or `WAN_STUDIO_FOUNDATION`. The container needs no studio configuration; setting any config form switches on the operator's full validation, which needs every Deploy output.

### Foundation fields

These are the only foundation values the portal reads. Start from [docs/samples/foundation.json.example](samples/foundation.json.example), which uses placeholder IDs (`11111111-...`) and the example deployment `wan-demo` with name suffix `a1b2c3d4`.

| Field | Where to get it |
| --- | --- |
| `subscriptionId`, `tenantId` | The studio subscription and tenant, as lowercase GUIDs (config `subscription_id`, `tenant_id`). `tenantId` must also equal the one in `portal-auth.json`. |
| `deploymentName` | Config `deployment_name` |
| `resourceGroupName` | `rg-<deployment_name>-<suffix>`, the studio resource group created by Deploy |
| `workspaceName` | `mlw-<deployment_name>-<suffix>` in that resource group |
| `storageAccountName` | `az storage account list -g <studio-rg> --query [0].name -o tsv` |
| `computeIdentityClientId` | `az identity show -g <studio-rg> -n id-<deployment_name>-<suffix>-gpu --query clientId -o tsv` |
| `computeId` | `/subscriptions/<subscriptionId>/resourceGroups/<resourceGroupName>/providers/Microsoft.MachineLearningServices/workspaces/<workspaceName>/computes/wan-gpu` |
| `computeName`, `containerName`, `datastoreName` | Fixed: `wan-gpu`, `wan-studio`, `wan_blob` |

Extra fields are ignored, so pasting a whole `foundation.json` also works. Each of the 11 values must match, character for character, what Prepare used: the portal hashes them and refuses to start if the hash differs from the prepared release. Key order and whitespace do not matter. If you have the operator cache, copy the values from its `foundation.json` (or `terraform -chdir=infra output -json studio`) rather than typing them.

The value stays valid across Start and Stop. Update it only after a Deploy that changes one of these fields, which also needs a new Prepare, image, and push.

To avoid shell quoting problems with JSON values, set the settings from a file:

```powershell
$cache = "$env:LOCALAPPDATA\wan-safety-studio\<subscription-id>-<deployment_name>"
$source = Get-Content "$cache\foundation.json" -Raw | ConvertFrom-Json   # or your filled-in sample
$foundation = $source | Select-Object subscriptionId, tenantId, deploymentName, resourceGroupName, workspaceName,
  computeName, computeId, computeIdentityClientId, storageAccountName, containerName, datastoreName
$settingsFile = Join-Path $env:TEMP 'wan-portal-settings.json'
@{
  WAN_STUDIO_FOUNDATION_JSON            = $foundation | ConvertTo-Json -Compress
  WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID = '<identity-client-id>'
  WAN_STUDIO_PUBLIC_ORIGIN              = 'https://<app-name>.azurewebsites.net'
  WEBSITES_PORT                         = '8000'
} | ConvertTo-Json | Set-Content -Encoding utf8 $settingsFile
az webapp config appsettings set -g <rg> -n <app-name> --settings "@$settingsFile" --output none
Remove-Item $settingsFile
```

None of these values is a secret. They do contain resource names and IDs, so treat them as internal.

### Sign-in registration

The portal uses the `WAN Safety Studio` app registration created by `Initialize-PortalAuth.ps1`, whose client ID is in `portal-auth.json`. It signs in to Entra with a **federated credential that trusts the web app's managed identity**, so no client secret is stored on the web app. An identity owner (Application Administrator, or an owner of the registration) adds two items:

```powershell
$clientId    = (Get-Content "$cache\portal-auth.json" -Raw | ConvertFrom-Json).clientId
$tenantId    = (Get-Content "$cache\portal-auth.json" -Raw | ConvertFrom-Json).tenantId
$principalId = az identity show -g <rg> -n <identity-name> --query principalId -o tsv

# 1. Add the hosted redirect URI. Keep the existing (localhost) URIs, because the command replaces the list.
$uris = @(az ad app show --id $clientId --query web.redirectUris -o tsv) + 'https://<app-name>.azurewebsites.net/auth/callback'
az ad app update --id $clientId --web-redirect-uris @uris

# 2. Trust the web app's managed identity (the subject is the identity's principal ID, not its client ID).
$fic = Join-Path $env:TEMP 'wan-portal-fic.json'
@{
  name        = 'wan-portal-<deployment_name>'
  issuer      = "https://login.microsoftonline.com/$tenantId/v2.0"
  subject     = $principalId
  audiences   = @('api://AzureADTokenExchange')
  description = 'WAN Safety Studio App Service managed identity (secretless MSAL client assertion).'
} | ConvertTo-Json | Set-Content -Encoding utf8 $fic
az ad app federated-credential create --id $clientId --parameters "@$fic"
Remove-Item $fic
```

Creators still need membership in the `WAN Safety Studio Creators` group, as for the local portal.

### Verify

With private DNS and VPN in place, `https://<app-name>.azurewebsites.net/healthz` returns `{"status": "ok", "authentication": "required"}`. Opening the root URL sends you to Microsoft sign-in.

## 5. Operate

| Operator action | What to do on this web app |
| --- | --- |
| Start | After Start succeeds, copy `armed.json` from the operator cache into the `WAN_STUDIO_ARMED` app setting. The app restarts and generation becomes available. |
| Stop | **Before** running Stop, delete the `WAN_STUDIO_ARMED` app setting. The app restarts disarmed. Stop does not do this for you. |
| Prepare (new version) | The running image keeps its older release and fails closed, because the armed version no longer matches. Rebuild and push the image (section 2), point the web app at the new tag, then Start and set `WAN_STUDIO_ARMED` again. |
| Deploy with changed configuration | After the new Prepare and image, update `WAN_STUDIO_FOUNDATION_JSON` if any of its 11 fields changed. |

```powershell
# Arm (after Start)
$armed = Get-Content "$cache\armed.json" -Raw | ConvertFrom-Json -AsHashtable | ConvertTo-Json -Compress
$armedFile = Join-Path $env:TEMP 'wan-portal-armed.json'
@{ WAN_STUDIO_ARMED = $armed } | ConvertTo-Json | Set-Content -Encoding utf8 $armedFile
az webapp config appsettings set -g <rg> -n <app-name> --settings "@$armedFile" --output none
Remove-Item $armedFile

# Disarm (before Stop)
az webapp config appsettings delete -g <rg> -n <app-name> --setting-names WAN_STUDIO_ARMED --output none
```

If your automation manages app settings declaratively, make sure it does not add or remove `WAN_STUDIO_ARMED` on its own, which would arm or disarm the portal unexpectedly.

Every restart clears sign-in sessions, and a restart also ends any batch that is still submitting. Jobs already created in Azure ML continue running.

## Troubleshooting

Read container output with `az webapp log tail -g <rg> -n <app-name>` (turn on **App Service logs > Application logging: File System** first) or in the Log stream blade. So that request details never reach the logs, a failed startup prints only the exception type, for example `KeyError: operation failed; ...`.

| Symptom or log message | Check |
| --- | --- |
| `KeyError: operation failed` at startup | `WAN_STUDIO_FOUNDATION_JSON` is missing or lacks one of the 11 fields; a config setting is set (remove it); or the image was built from a cache prepared before this repo version (run Prepare, then rebuild). |
| `ValueError: operation failed` at startup | In order: a path-form setting (`WAN_STUDIO_FOUNDATION`) is set beside its `_JSON` form; `WAN_STUDIO_PUBLIC_ORIGIN` has a path, port, trailing slash, or uppercase letters; a foundation value is malformed (non-GUID ID, or `computeId` not built from the other fields); the foundation values do not match what Prepare used; the `upstream` build context was not the manifest's `sourceRoot` (rebuild as in section 2); or the image's redirect URI is not `WAN_STUDIO_PUBLIC_ORIGIN` + `/auth/callback` (rebuild with the right origin). |
| `JSONDecodeError: operation failed` at startup | A `_JSON` setting or `WAN_STUDIO_ARMED` is not valid JSON. Set it from a file as shown above. |
| Redirect URI mismatch at sign-in | Add `https://<host>/auth/callback` to the app registration's redirect URIs, using the same host as `WAN_STUDIO_PUBLIC_ORIGIN`. |
| `403 This portal accepts only its private App Service host` | Open the exact host in `WAN_STUDIO_PUBLIC_ORIGIN`, not the IP address or another host name. |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity) | The federated credential subject must be the identity's **principal ID**. The issuer must be `https://login.microsoftonline.com/<tenant>/v2.0`. |
| Managed identity errors in the log | `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` must be the client ID of a user-assigned identity **attached to this web app**. |
| Image pull fails (`ImagePullFailure`, 401) | Check the AcrPull assignment, `acrUseManagedIdentityCreds`/`acrUserManagedIdentityID`, and, for a private ACR, `vnetImagePullEnabled` plus DNS and egress to the ACR private endpoint. |
| Container starts but the site never becomes healthy | `WEBSITES_PORT` must be `8000`. The health check path must be `/healthz`. |
| "GPU generation is not armed" | Set `WAN_STUDIO_ARMED` from the current `armed.json` after Start, and make sure the image was built after the latest Prepare. |
| Videos do not play | Creators' browsers must resolve and reach the storage account's private blob endpoint. |
| Build fails fetching wheels (TLS or connection errors) | The build machine cannot reach `files.pythonhosted.org`. Use `--build-arg PYPI_INDEX=<mirror>` or build from a network that allows it. |
