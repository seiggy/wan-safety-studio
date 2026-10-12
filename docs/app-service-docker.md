# Host the studio as a container on an App Service you create

Use this guide when your own infrastructure automation creates the App Service instead of Terraform's `portal` option in [app-service.md](app-service.md). You build the studio server image, push it to your private Azure Container Registry (ACR), and configure the web app by hand.

The image is the **.NET 10 server** (`src\VideoSafetyStudio.Server`) with the React UI in `wwwroot`. It replaces the earlier Python portal image. It runs the Azure ML generation provider: it submits Wan 2.2 jobs to your Azure ML workspace and keeps inputs, videos and job records in the studio blob container.

In this mode the studio's Terraform and scripts do not touch the web app. **Start and Stop do not arm or disarm it**; you do that with one app setting (see [section 5](#5-operate)).

Nothing from your deployment is baked into the image. Everything comes from environment variables (App Service app settings), so one image works for any web app, origin, and workspace.

## 1. Prerequisites

| Requirement | Notes |
| --- | --- |
| This repository | A clone is enough. |
| The prepared GPU assets | The Azure ML environment and the workflow code upload that your pipeline already created, plus the model files in blob storage (`scripts/upload-wan-models.sh`, 8 files including the LoRAs, the 5B checkpoint and both VAEs). You need the environment's asset ID (see [Release pointers](#release-pointers)). |
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
| `AzureMl__SubscriptionId`, `AzureMl__TenantId`, `AzureMl__ResourceGroup`, `AzureMl__Workspace`, `AzureMl__Compute`, `AzureMl__ComputeIdentityClientId`, `AzureMl__StorageAccount`, `AzureMl__Container` | Yes | The studio resources, one setting each | See [Resource settings](#resource-settings). |
| `AzureMl__Datastore` | No | Default `workspaceblobstore`; the studio's datastore is `wan_blob` | See [Resource settings](#resource-settings). |
| `AzureMl__Release__EnvironmentId` | Yes | The Azure ML environment's asset ID | See [Release pointers](#release-pointers). |
| `AzureMl__Release__ModelsRef`, `AzureMl__Release__CodeUri` | No | Pointers to the models and workflow code | See [Release pointers](#release-pointers). |
| `AZURE_CLIENT_ID` | Yes | Client ID (GUID) of the web app's user-assigned identity | The identity resource > Overview > Client ID. The standard Azure SDK variable: ARM, Blob and registry calls and the sign-in federated assertion use that identity. |
| `Auth__ClientId` | Yes (unless auth is disabled) | Client (application) ID of the `WAN Safety Studio` app registration | Entra ID > App registrations > the app > Overview, or `portal-auth.json` from `Initialize-PortalAuth.ps1` |
| `Auth__TenantId` | No | Defaults to `AzureMl__TenantId` | The sign-in token's tenant must equal this tenant. |
| `Auth__PublicOrigin` | Yes | The web app's origin, for example `https://<app-name>.azurewebsites.net` | Lowercase, no path, port, or trailing slash. The redirect URI `<origin>/auth/callback` must be on the app registration. |
| `AzureMl__Armed` | Yes (`false` by default) | `true` while generation is enabled, `false` otherwise | Read on every submission. Always write `false` to disarm rather than deleting it. See [section 5](#5-operate). |
| `Auth__Mode` | No | `Entra` outside Development; `None` turns sign-in off | See [Disabling sign-in](#disabling-sign-in). |
| `KeyVault__Uri` | No | `https://<vault>.vault.azure.net/` | Adds Key Vault as a configuration source (for example the secret `Auth--ClientSecret`). Give the identity **Key Vault Secrets User**. |
| `FEATURE_DATASET_BUILDER_ENABLED` | No | `true` | Enables the vision dataset builder; off by default. |
| `Agent__Endpoint`, `Agent__Model`, `Agent__Auth`, `Agent__ManagedIdentityClientId`, `Agent__ApiKey`, `Agent__WireApi`, `Agent__Vision` | No | See [Video helper](#video-helper-optional) | Omit them to leave the helper off. |

The [configuration reference](configuration.md) lists every key the server reads, including the tuning keys (`AzureMl__PollSeconds`, `AzureMl__MaxConcurrency`, `AzureMl__MaxJobMinutes`, `AzureMl__MaxUploadMb`).

### Release pointers

`AzureMl__Release__EnvironmentId`, `AzureMl__Release__ModelsRef` and `AzureMl__Release__CodeUri` tell the studio which GPU environment, models and workflow code to run. All of them already exist in your Azure ML workspace.

| Setting | Where to get it |
| --- | --- |
| `AzureMl__Release__EnvironmentId` | Azure ML studio > **Assets > Environments** > your environment > the version you want, for example `azureml:wan:dc0d29031b73-8ea1320533d2599f`. `azureml:<name>:<version>` or the full asset ID ending in `/versions/<version>` both work; the studio takes the release version from the last `:` or `/` segment. Empty means not prepared. |
| `AzureMl__Release__ModelsRef` (optional) | The folder holding the model files: `azureml://datastores/<datastoreName>/paths/<folder>/`, with `diffusion_models/`, `text_encoders/`, `vae/` and `loras/` directly inside. **Defaults to `azureml://datastores/<datastoreName>/paths/models/wan/`**, which is where `scripts/upload-wan-models.sh` puts them. A registered asset (`azureml:<name>:<version>`) also works. |
| `AzureMl__Release__CodeUri` (optional) | Only if the workflow code was uploaded somewhere other than `azureml://datastores/<datastoreName>/paths/code/<version>-wan/`. |

The studio only submits jobs that use exactly this environment and these models, so changing a value changes what runs.

### Resource settings

These are the studio resource values the studio reads. Start from [docs/samples/foundation.json.example](samples/foundation.json.example) (placeholder IDs, example deployment `wan-demo`) or from the `foundation.json` the operator scripts write; each field maps to one setting.

| Setting | Foundation field | Where to get it |
| --- | --- | --- |
| `AzureMl__SubscriptionId`, `AzureMl__TenantId` | `subscriptionId`, `tenantId` | The studio subscription and tenant, as lowercase GUIDs. |
| `AzureMl__ResourceGroup` | `resourceGroupName` | `rg-<deployment_name>-<suffix>`, the studio resource group created by Deploy |
| `AzureMl__Workspace` | `workspaceName` | `mlw-<deployment_name>-<suffix>` in that resource group |
| `AzureMl__StorageAccount` | `storageAccountName` | `az storage account list -g <studio-rg> --query [0].name -o tsv` |
| `AzureMl__ComputeIdentityClientId` | `computeIdentityClientId` | `az identity show -g <studio-rg> -n id-<deployment_name>-<suffix>-gpu --query clientId -o tsv` |
| `AzureMl__Compute`, `AzureMl__Container`, `AzureMl__Datastore` | `computeName`, `containerName`, `datastoreName` | Fixed: `wan-gpu`, `wan-studio`, `wan_blob` |

The compute resource ID is derived from these values, never configured. Names must match `^[a-zA-Z0-9][a-zA-Z0-9_.-]*$` and IDs must be canonical GUIDs. [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#finding-the-values-in-the-azure-portal) shows where each value is in the Azure portal. Uploaded input images go to `studio/web-inputs/`, videos to `video-library/<job>/` and job records to `video-library-jobs/` in the `AzureMl__Container` container.

Set the settings from a file to avoid shell quoting problems:

```powershell
$source = Get-Content docs\samples\foundation.json.example -Raw | ConvertFrom-Json   # your filled-in copy of the sample
$settingsFile = Join-Path $env:TEMP 'wan-studio-settings.json'
@{
  WEBSITES_PORT                    = '8080'
  Generation__Provider             = 'azureml'
  ASPNETCORE_ENVIRONMENT           = 'Production'
  AzureMl__SubscriptionId          = $source.subscriptionId
  AzureMl__TenantId                = $source.tenantId
  AzureMl__ResourceGroup           = $source.resourceGroupName
  AzureMl__Workspace               = $source.workspaceName
  AzureMl__Compute                 = $source.computeName
  AzureMl__ComputeIdentityClientId = $source.computeIdentityClientId
  AzureMl__StorageAccount          = $source.storageAccountName
  AzureMl__Container               = $source.containerName
  AzureMl__Datastore               = $source.datastoreName
  AzureMl__Release__EnvironmentId  = 'azureml:<environment-name>:<version>'
  AzureMl__Armed                   = 'false'
  AZURE_CLIENT_ID                  = '<identity-client-id>'
  Auth__ClientId                   = '<app-registration-client-id>'
  Auth__PublicOrigin               = 'https://<app-name>.azurewebsites.net'
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

Set `Auth__Mode=None` and the studio skips Microsoft sign-in: every request acts as one fixed user and the server logs a loud warning. `Auth__ClientId`, the app registration, the federated credential and the creators group are then not needed.

**Anyone who can reach the site can then submit GPU jobs and view results.** Use it only when something else already restricts access: the private endpoint and network rules, or App Service Authentication (Easy Auth) in front of the app. Delete the setting to turn sign-in back on.

### Sign-in registration

The studio uses the `WAN Safety Studio` app registration (its client ID is `Auth__ClientId`) with an authorization-code flow. It signs in to Entra with a **federated credential that trusts the web app's managed identity**, so no client secret is stored on the web app. Only members holding the `VideoCreator` app role (assigned through the `WAN Safety Studio Creators` group) get in, and the token's tenant must equal `AzureMl__TenantId`. An identity owner adds two items (or run [`scripts\Initialize-PortalAuth.ps1`](../scripts/Initialize-PortalAuth.ps1), which also creates the registration, group and role). If your Terraform manages the registration, declare both there; see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#app-registration).

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
| Start | Set the `AzureMl__Armed` app setting to `true`. The app restarts and generation becomes available. |
| Stop | Set `AzureMl__Armed` to `false` **before** stopping the compute. The app restarts disarmed. |
| New GPU environment or models | Update `AzureMl__Release__EnvironmentId` (and `AzureMl__Release__ModelsRef` / `AzureMl__Release__CodeUri` if they changed). No image rebuild. |
| New studio code | Rebuild and push the image (section 2) and point the web app at the new tag. |

```powershell
# Arm
az webapp config appsettings set -g <rg> -n <app-name> --settings AzureMl__Armed=true --output none

# Disarm
az webapp config appsettings set -g <rg> -n <app-name> --settings AzureMl__Armed=false --output none
```

If your automation manages app settings declaratively, make sure it does not change `AzureMl__Armed` on its own, which would arm or disarm the studio unexpectedly.

Every restart clears sign-in sessions, and a restart also ends any batch that is still submitting. Jobs already created in Azure ML continue running.

## Troubleshooting

For the full checklist across the compute, network, identity, and sign-in pieces, see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md).

Read container output with `az webapp log tail -g <rg> -n <app-name>` (turn on **App Service logs > Application logging: File System** first) or in the Log stream blade. The server fails fast at startup with a message naming the missing or invalid setting.

| Symptom or log message | Check |
| --- | --- |
| "has no sign-in configured" at startup | Set `Auth__ClientId` and `Auth__PublicOrigin`, or deliberately `Auth__Mode=None`. |
| Configuration errors at startup | An `AzureMl__*` setting is missing or malformed (a non-GUID id, a name with invalid characters), or `AzureMl__Release__EnvironmentId` is empty (not prepared). See the [configuration reference](configuration.md). |
| Redirect URI mismatch at sign-in | Add `https://<host>/auth/callback` to the app registration's redirect URIs, using the same host as `Auth__PublicOrigin`. |
| Host mismatch / `403` | Open the exact host in `Auth__PublicOrigin`, not the IP address or another host name. |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity) | The federated credential subject must be the identity's **principal ID**. The issuer must be `https://login.microsoftonline.com/<tenant>/v2.0`. |
| Managed identity errors in the log | `AZURE_CLIENT_ID` must be the client ID of a user-assigned identity **attached to this web app**. |
| Image pull fails (`ImagePullFailure`, 401) | Check the AcrPull assignment, `acrUseManagedIdentityCreds`/`acrUserManagedIdentityID`, and, for a private ACR, `vnetImagePullEnabled` plus DNS and egress to the ACR private endpoint. |
| Container starts but the site never becomes healthy | `WEBSITES_PORT` must be `8080`. The health check path must be `/healthz`. |
| "GPU generation is not armed" | Set `AzureMl__Armed=true` and let the app restart. |
| Videos or the gallery fail to load | The identity needs Storage Blob Data Contributor and a private route to the blob endpoint. |
| Image build fails restoring packages | The build machine cannot reach the package feed or GitHub releases (Copilot CLI download). Supply feed credentials with `--secret` as shown in section 2. |
