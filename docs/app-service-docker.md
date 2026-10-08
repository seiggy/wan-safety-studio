# Host the studio as a container on an App Service you create

Use this guide when your own infrastructure automation creates the App Service instead of Terraform's `portal` option in [app-service.md](app-service.md). You build the studio server image, push it to your private Azure Container Registry (ACR), and configure the web app by hand.

The image is the **.NET 10 server** (`src\VideoSafetyStudio.Server`) with the React UI in `wwwroot`. It replaces the earlier Python portal image. It runs the Azure ML generation provider: it submits Wan 2.2 jobs to your Azure ML workspace and keeps inputs, videos and job records in the studio blob container.

In this mode the studio's Terraform and scripts do not touch the web app. **Start and Stop do not arm or disarm it**; you do that with one app setting (see [section 5](#5-operate)).

Nothing from your deployment is baked into the image. Everything comes from environment variables (App Service app settings), so one image works for any web app, origin, and workspace.

## 1. Prerequisites

| Requirement | Notes |
| --- | --- |
| This repository | A clone is enough. |
| The prepared GPU assets | The Azure ML environment and the workflow code upload that your pipeline already created, plus the model files in blob storage (`scripts/upload-wan-models.sh`, 8 files including the LoRAs, the 5B checkpoint and both VAEs). You need the environment's asset ID (see [`WAN_STUDIO_RELEASE_JSON`](#release-pointers)). |
| Docker | Docker with BuildKit and Linux containers. The build restores NuGet packages from the repo `nuget.config` and npm packages from `web\.npmrc` (both point at the approved package feed), and the Copilot SDK build step downloads the Copilot CLI from GitHub releases. |
| Private ACR | Your registry, for example `<registry>.azurecr.io`, and permission to push (`AcrPush`). |
| Network | The same network prerequisites as the Terraform-hosted studio: [app-service.md section 1](app-service.md#1-network-owner-prerequisites). A private ACR also needs TCP 443 egress from the integration subnet to the ACR private endpoint (and its data endpoint), plus `privatelink.azurecr.io` DNS. |
| Web app host name | Decide the public origin, for example `https://<app-name>.azurewebsites.net` or a custom domain. It is an app setting, so the image doesn't depend on it. |

## 2. Build the image

Run from the repository root (the build context is the repo root; `.dockerignore` limits it to `src\`, `web\` and `nuget.config`).

```powershell
$image = '<registry>.azurecr.io/wan-safety-studio-server:<tag>'
docker build --platform linux/amd64 -f src/VideoSafetyStudio.Server/Dockerfile -t $image .
az acr login --name <registry>      # or: docker login <registry>.azurecr.io
docker push $image
```

What the Dockerfile does:

1. **web** stage: `npm ci` with the repo's `web\.npmrc` and `package-lock.json`, then `npm run build`.
2. **publish** stage (.NET SDK 10): `dotnet publish -r linux-x64` of the server with `-p:SkipWebBuild=true` (the web stage already built the UI, which is copied to `wwwroot`). The SDK build step downloads the Copilot CLI/runtime for `linux-x64` (the video helper's agent loop) into `runtimes/linux-x64/native`, and the Dockerfile marks the native executables executable.
3. **final** stage: `mcr.microsoft.com/dotnet/aspnet:10.0`, runs as the non-root `app` user, listens on **8080** (`ASPNETCORE_HTTP_PORTS=8080`), entry point `dotnet VideoSafetyStudio.Server.dll`.

If your package feed requires credentials, pass them as BuildKit secrets; they are mounted only during the restore and never stored in a layer:

```powershell
docker build --platform linux/amd64 -f src/VideoSafetyStudio.Server/Dockerfile -t $image `
  --secret id=npmrc,src=$HOME/.npmrc --secret id=nugetconfig,src=<path-to-user-NuGet.Config> .
```

`scripts\Invoke-WanSafetyStudio.ps1 -Action Publish` does the same build for the Terraform-hosted site and tags it `<registry>.azurecr.io/wan-safety-studio-server:<release version>-<source hash>`.

Rebuild and push only when this repository's code changes. A new GPU environment or models asset needs only a changed app setting.

## 3. Create the web app

Create these resources with your own automation. The values mirror what Terraform creates for the built-in option ([infra/portal.tf](../infra/portal.tf)).

| Setting | Required value |
| --- | --- |
| Plan | Linux, any SKU with quota in the region. **One instance.** Sign-in sessions live in process memory, so do not scale out or enable autoscale. |
| Publishing model | Container, image `<registry>.azurecr.io/wan-safety-studio-server:<tag>`, platform `linux/amd64`. No startup command. |
| Identity | One **user-assigned** managed identity. The server needs its client ID, so a system-assigned identity is not supported. |
| Registry pull | Pull with the managed identity: set `acrUseManagedIdentityCreds=true` and `acrUserManagedIdentityID=<identity client ID>`. If the ACR is private-endpoint only, also enable image pull over VNet (`vnetImagePullEnabled=true`). |
| HTTPS | HTTPS only, minimum TLS 1.2, FTP/FTPS disabled, basic publishing credentials disabled. |
| Always On | On. |
| Health check | Path `/healthz`. |
| Inbound | Private endpoint (`sites`) in the studio's private endpoint subnet, registered in `privatelink.azurewebsites.net`. Public network access disabled. |
| Outbound | Regional VNet integration on a subnet delegated to `Microsoft.Web/serverFarms`. The app must reach the Blob, Azure ML and registry private endpoints. It must also reach Microsoft Entra ID (`login.microsoftonline.com`) and Azure Resource Manager (`management.azure.com`). Terraform leaves **Route All** off so those go out the platform path. If you turn Route All on, allow them through your firewall. |
| Session affinity | Off. |

Give the identity these role assignments. Scopes are the resource IDs of the storage account, workspace, and ACR.

| Role | Scope |
| --- | --- |
| Storage Blob Data Contributor | `storageAccountId` |
| AzureML Data Scientist | `workspaceId` |
| AcrPull | Your ACR |
| Cognitive Services OpenAI User (optional) | The Foundry / Azure OpenAI resource, for the [video helper](video-helper.md) with managed identity auth |

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
| `WEBSITES_PORT` | Yes | `8080` | Fixed; the container listens on 8080. |
| `Generation__Provider` | Yes | `azureml` | Selects the Azure ML provider (`comfyui` is for a direct ComfyUI server). |
| `ASPNETCORE_ENVIRONMENT` | Recommended | `Production` | Keep it out of `Development`: sign-in is mandatory in Production. |
| `WAN_STUDIO_FOUNDATION_JSON` | Yes | The foundation fields below, as one JSON string (never together with `WAN_STUDIO_FOUNDATION`, a file path) | See [Foundation fields](#foundation-fields). |
| `WAN_STUDIO_RELEASE_JSON` | Yes | `{"environmentId": "..."}` as one JSON string (optional keys `modelsRef`, `codeUri`) | The Azure ML environment's asset ID. See [Release pointers](#release-pointers). |
| `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` | Yes | Client ID (GUID) of the web app's user-assigned identity | The identity resource > Overview > Client ID |
| `AZURE_CLIENT_ID` | Yes | The same client ID | Makes `DefaultAzureCredential` use that identity for ARM and Blob calls. |
| `WAN_STUDIO_PORTAL_CLIENT_ID` | Yes (unless auth is disabled) | Client (application) ID of the `WAN Safety Studio` app registration | Entra ID > App registrations > the app > Overview, or `portal-auth.json` from `Initialize-PortalAuth.ps1` |
| `WAN_STUDIO_PUBLIC_ORIGIN` | Yes | The web app's origin, for example `https://<app-name>.azurewebsites.net` | Lowercase, no path, port, or trailing slash. The redirect URI `<origin>/auth/callback` must be on the app registration. If unset, the origin is `https://` + `portal.hostname` from the foundation JSON. |
| `WAN_STUDIO_ARMED` | Only while generation is enabled | `{"compute":"wan-gpu","profile":"wan","version":"<release version>"}` | JSON; its presence arms generation and it must match the foundation compute, profile `wan` and the release version (the last segment of `environmentId`). Delete the setting to disarm. See [section 5](#5-operate). |
| `WAN_STUDIO_DISABLE_AUTH` | No | `true` | Turns sign-in **off**. See [Disabling sign-in](#disabling-sign-in). |
| `FEATURE_DATASET_BUILDER_ENABLED` | No | `true` | Enables the vision dataset builder; off by default. |
| `Agent__Endpoint`, `Agent__Model`, `Agent__Auth`, `Agent__ManagedIdentityClientId`, `Agent__ApiKey`, `Agent__WireApi`, `Agent__Vision` | No | See [Video helper](#video-helper-optional) | Omit them to leave the helper off. |

### Release pointers

`WAN_STUDIO_RELEASE_JSON` tells the studio which GPU environment and models to run. Both already exist in your Azure ML workspace.

```json
{
  "environmentId": "azureml:comfyui-wan-cu128:dc0d29031b73-8ea1320533d2599f",
  "modelsRef": "azureml://datastores/wan_blob/paths/models/wan/"
}
```

| Key | Where to get it |
| --- | --- |
| `environmentId` | Azure ML studio > **Assets > Environments** > your environment > the version you want. `azureml:<name>:<version>` or the full asset ID ending in `/versions/<version>` both work; the studio takes the release version from the last `:` or `/` segment. |
| `modelsRef` (optional) | The folder holding the model files: `azureml://datastores/<datastoreName>/paths/<folder>/`, with `diffusion_models/`, `text_encoders/`, `vae/` and `loras/` directly inside. **Defaults to `azureml://datastores/<datastoreName>/paths/models/wan/`**, which is where `scripts/upload-wan-models.sh` puts them. A registered asset (`azureml:<name>:<version>`) also works. |
| `codeUri` (optional) | Only if the workflow code was uploaded somewhere other than `azureml://datastores/<datastoreName>/paths/code/<version>-wan/`. |

The studio only submits jobs that use exactly this environment and these models, so changing the value changes what runs.

### Foundation fields

These are the foundation values the studio reads. Start from [docs/samples/foundation.json.example](samples/foundation.json.example), which uses placeholder IDs and the example deployment `wan-demo`.

| Field | Where to get it |
| --- | --- |
| `subscriptionId`, `tenantId` | The studio subscription and tenant, as lowercase GUIDs. The sign-in token's tenant must equal `tenantId`. |
| `resourceGroupName` | `rg-<deployment_name>-<suffix>`, the studio resource group created by Deploy |
| `workspaceName` | `mlw-<deployment_name>-<suffix>` in that resource group |
| `storageAccountName` | `az storage account list -g <studio-rg> --query [0].name -o tsv` |
| `computeIdentityClientId` | `az identity show -g <studio-rg> -n id-<deployment_name>-<suffix>-gpu --query clientId -o tsv` |
| `computeId` | `/subscriptions/<subscriptionId>/resourceGroups/<resourceGroupName>/providers/Microsoft.MachineLearningServices/workspaces/<workspaceName>/computes/wan-gpu` (must match the other fields, case-insensitively) |
| `computeName`, `containerName`, `datastoreName` | Fixed: `wan-gpu`, `wan-studio`, `wan_blob` |
| `portal.hostname` (optional) | The site host name; only needed when `WAN_STUDIO_PUBLIC_ORIGIN` is unset |

Names must match `^[a-zA-Z0-9][a-zA-Z0-9_.-]*$` and IDs must be canonical GUIDs. Extra fields are ignored, so pasting a whole `foundation.json` also works. [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#finding-the-values-in-the-azure-portal) shows where each value is in the Azure portal. Uploaded input images go to `studio/web-inputs/`, videos to `video-library/<job>/` and job records to `video-library-jobs/` in the `containerName` container.

Set the settings from a file to avoid shell quoting problems:

```powershell
$source = Get-Content docs\samples\foundation.json.example -Raw | ConvertFrom-Json   # your filled-in copy of the sample
$foundation = $source | Select-Object subscriptionId, tenantId, resourceGroupName, workspaceName,
  computeName, computeId, computeIdentityClientId, storageAccountName, containerName, datastoreName
$settingsFile = Join-Path $env:TEMP 'wan-studio-settings.json'
@{
  WEBSITES_PORT                         = '8080'
  Generation__Provider                  = 'azureml'
  ASPNETCORE_ENVIRONMENT                = 'Production'
  WAN_STUDIO_FOUNDATION_JSON            = $foundation | ConvertTo-Json -Compress
  WAN_STUDIO_RELEASE_JSON               = '{"environmentId":"azureml:<environment-name>:<version>"}'
  WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID = '<identity-client-id>'
  AZURE_CLIENT_ID                       = '<identity-client-id>'
  WAN_STUDIO_PORTAL_CLIENT_ID           = '<app-registration-client-id>'
  WAN_STUDIO_PUBLIC_ORIGIN              = 'https://<app-name>.azurewebsites.net'
} | ConvertTo-Json | Set-Content -Encoding utf8 $settingsFile
az webapp config appsettings set -g <rg> -n <app-name> --settings "@$settingsFile" --output none
Remove-Item $settingsFile
```

None of these values is a secret. They do contain resource names and IDs, so treat them as internal.

### Video helper (optional)

The [Video Production Helper](video-helper.md) stays off until `Agent__Endpoint` and `Agent__Model` are set. It has four auth methods; on this host use one of two:

| `Agent__Auth` | Settings | Notes |
| --- | --- | --- |
| `UserAssignedManagedIdentity` | `Agent__ManagedIdentityClientId=<identity client id>` | Grant the identity **Cognitive Services OpenAI User** on the Foundry resource. No secret. |
| `ApiKey` | `Agent__ApiKey=@Microsoft.KeyVault(SecretUri=<secret uri>)` | Store the key in Key Vault and reference it; give the identity **Key Vault Secrets User** and set `keyVaultReferenceIdentity` to it. Never paste the key as plain text. |

`SystemAssignedManagedIdentity` needs a system-assigned identity (not present here) and `AzureCli` is for local development. Optional: `Agent__WireApi` (`responses` for Foundry reasoning models) and `Agent__Vision=false` for text-only models.

### Disabling sign-in

Set `WAN_STUDIO_DISABLE_AUTH=true` and the studio skips Microsoft sign-in: every request acts as one fixed user and the server logs a loud warning. `WAN_STUDIO_PORTAL_CLIENT_ID`, the app registration, the federated credential and the creators group are then not needed.

**Anyone who can reach the site can then submit GPU jobs and view results.** Use it only when something else already restricts access: the private endpoint and network rules, or App Service Authentication (Easy Auth) in front of the app. Delete the setting to turn sign-in back on.

### Sign-in registration

The studio uses the `WAN Safety Studio` app registration (its client ID is `WAN_STUDIO_PORTAL_CLIENT_ID`) with an authorization-code flow. It signs in to Entra with a **federated credential that trusts the web app's managed identity**, so no client secret is stored on the web app. Only members holding the `VideoCreator` app role (assigned through the `WAN Safety Studio Creators` group) get in, and the token's tenant must equal the foundation `tenantId`. An identity owner adds two items (or run [`scripts\Initialize-PortalAuth.ps1`](../scripts/Initialize-PortalAuth.ps1), which also creates the registration, group and role). If your Terraform manages the registration, declare both there; see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#app-registration).

```powershell
$clientId    = '<app-registration-client-id>'
$tenantId    = '<tenant-id>'
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
  description = 'WAN Safety Studio App Service managed identity (secretless client assertion).'
} | ConvertTo-Json | Set-Content -Encoding utf8 $fic
az ad app federated-credential create --id $clientId --parameters "@$fic"
Remove-Item $fic
```

### Verify

With private DNS and VPN in place, `https://<app-name>.azurewebsites.net/healthz` returns 200 and opening the root URL lets you sign in. `/api/session` returns your name and a CSRF token after sign-in; `/api/status` shows `"mode": "azureml"`.

## 5. Operate

| Operator action | What to do on this web app |
| --- | --- |
| Start | Set the `WAN_STUDIO_ARMED` app setting. The app restarts and generation becomes available. |
| Stop | Delete the `WAN_STUDIO_ARMED` app setting **before** stopping the compute. The app restarts disarmed. |
| New GPU environment or models | Update `WAN_STUDIO_RELEASE_JSON` (and `WAN_STUDIO_ARMED.version`). No image rebuild. |
| New studio code | Rebuild and push the image (section 2) and point the web app at the new tag. |

```powershell
# Arm (version = last segment of environmentId); a file avoids shell quoting problems
$file = Join-Path $env:TEMP 'wan-armed.json'
@{ WAN_STUDIO_ARMED = (@{ compute = 'wan-gpu'; profile = 'wan'; version = '<version>' } | ConvertTo-Json -Compress) } |
  ConvertTo-Json | Set-Content -Encoding utf8 $file
az webapp config appsettings set -g <rg> -n <app-name> --settings "@$file" --output none
Remove-Item $file

# Disarm
az webapp config appsettings delete -g <rg> -n <app-name> --setting-names WAN_STUDIO_ARMED --output none
```

If your automation manages app settings declaratively, make sure it does not add or remove `WAN_STUDIO_ARMED` on its own, which would arm or disarm the studio unexpectedly.

Every restart clears sign-in sessions, and a restart also ends any batch that is still submitting. Jobs already created in Azure ML continue running.

## Troubleshooting

For the full checklist across the compute, network, identity, and sign-in pieces, see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md).

Read container output with `az webapp log tail -g <rg> -n <app-name>` (turn on **App Service logs > Application logging: File System** first) or in the Log stream blade. The server fails fast at startup with a message naming the missing or invalid setting.

| Symptom or log message | Check |
| --- | --- |
| "has no sign-in configured" at startup | Set `WAN_STUDIO_PORTAL_CLIENT_ID` and `WAN_STUDIO_PUBLIC_ORIGIN` (or a foundation `portal.hostname`), or deliberately `WAN_STUDIO_DISABLE_AUTH=true`. |
| Foundation or release errors at startup | `WAN_STUDIO_FOUNDATION_JSON` / `WAN_STUDIO_RELEASE_JSON` missing, not valid JSON, set together with their path form, or lacking a field; `computeId` must match the other fields. |
| Redirect URI mismatch at sign-in | Add `https://<host>/auth/callback` to the app registration's redirect URIs, using the same host as `WAN_STUDIO_PUBLIC_ORIGIN`. |
| Host mismatch / `403` | Open the exact host in `WAN_STUDIO_PUBLIC_ORIGIN`, not the IP address or another host name. |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity) | The federated credential subject must be the identity's **principal ID**. The issuer must be `https://login.microsoftonline.com/<tenant>/v2.0`. |
| Managed identity errors in the log | `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` and `AZURE_CLIENT_ID` must be the client ID of a user-assigned identity **attached to this web app**. |
| Image pull fails (`ImagePullFailure`, 401) | Check the AcrPull assignment, `acrUseManagedIdentityCreds`/`acrUserManagedIdentityID`, and, for a private ACR, `vnetImagePullEnabled` plus DNS and egress to the ACR private endpoint. |
| Container starts but the site never becomes healthy | `WEBSITES_PORT` must be `8080`. The health check path must be `/healthz`. |
| "GPU generation is not armed" | Set `WAN_STUDIO_ARMED` (matching compute, profile and release version) and let the app restart. |
| Videos or the gallery fail to load | The identity needs Storage Blob Data Contributor and a private route to the blob endpoint. |
| Image build fails restoring packages | The build machine cannot reach the package feed or GitHub releases (Copilot CLI download). Supply feed credentials with `--secret` as shown in section 2. |
