# Host the studio portal as a container on an App Service you create

Use this guide when your own infrastructure automation creates the App Service instead of Terraform's `portal` option in [app-service.md](app-service.md). You build the portal image, push it to your private Azure Container Registry (ACR), and configure the web app by hand.

In this mode the studio's Terraform and scripts do not touch the web app. **Start and Stop do not arm or disarm it**; you do that with one app setting (see [section 5](#5-operate)).

The container reads everything from environment variables (App Service app settings). Nothing from your deployment is baked into the image: no `config.json`, `foundation.json`, `portal-auth.json` or `prepared-wan.json`. The image is only the portal code plus the pinned upstream runtime, so one image works for any web app, origin, and workspace.

## 1. Prerequisites

| Requirement | Notes |
| --- | --- |
| This repository | A clone is enough. Nothing from the studio's Terraform or scripts is needed. |
| The prepared GPU assets | The Azure ML environment and the workflow code upload that your pipeline already created, plus the model files in blob storage (`scripts/upload-wan-models.sh`). You need the environment's asset ID (see [`WAN_STUDIO_RELEASE_JSON`](#4-app-settings)). |
| Git | To fetch and patch the pinned upstream source. |
| Docker | Docker with BuildKit (Docker Desktop, or Docker Engine 23+) and access to `pypi.org`, or to your mirror (see `PYPI_INDEX` below). |
| Private ACR | Your registry, for example `<registry>.azurecr.io`, and permission to push (`AcrPush`). |
| Network | The same network prerequisites as the Terraform-hosted portal: [app-service.md section 1](app-service.md#1-network-owner-prerequisites). A private ACR also needs TCP 443 egress from the integration subnet to the ACR private endpoint, plus `privatelink.azurecr.io` DNS. |
| Web app host name | Decide the public origin, for example `https://<app-name>.azurewebsites.net` or a custom domain. It is an app setting, so the image doesn't depend on it. |

## 2. Build and push the image

Run these commands from the repository root in bash.

```bash
set -euo pipefail
image='<registry>.azurecr.io/wan-safety-studio-portal:<tag>'
work="$(mktemp -d)"
up="$work/upstream"

# 1. The pinned, patched upstream runtime (the commit is UPSTREAM_SHA in app/cost_guard.py).
sha="$(sed -n 's/^UPSTREAM_SHA = "\([0-9a-f]\{40\}\)"/\1/p' app/cost_guard.py)"
git clone --filter=blob:none --no-checkout https://github.com/jakeatmsft/azureml_vidgen_comfyui.git "$up"
git -C "$up" config core.autocrlf false
git -C "$up" checkout --detach "$sha"
git -C "$up" apply --ignore-space-change "$PWD/app/upstream-cost.patch"
cp app/config.py app/cost_guard.py "$up/azureml/"

# 2. Build and push.
docker build --platform linux/amd64 -f app/Dockerfile --build-context "upstream=$up" -t "$image" app
az acr login --name <registry>      # or: docker login <registry>.azurecr.io
docker push "$image"
rm -rf "$work"
```

Notes:

- Python packages are installed from `app/uv.lock` with `--require-hashes`. To use a PyPI mirror, add `--build-arg PYPI_INDEX=<simple-index-url>`.
- The image contains no Azure credentials and no deployment values.
- Rebuild and push only when this repository's code changes. A new GPU environment or models asset needs only a changed app setting, not a new image.
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

Give the identity these role assignments. Scopes are the resource IDs of the storage account, workspace, and ACR (resource > **Properties > Resource ID** in the Azure portal).

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
| `WAN_STUDIO_FOUNDATION_JSON` | Yes | The 10 runtime fields below, as one JSON string | See [Foundation fields](#foundation-fields). |
| `WAN_STUDIO_RELEASE_JSON` | Yes | `{"environmentId": "..."}` as one JSON string (optional keys `modelsRef`, `codeUri`) | The Azure ML environment's asset ID. See [Release pointers](#release-pointers). |
| `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` | Yes | Client ID (GUID) of the web app's user-assigned identity | Azure portal: the identity resource > Overview > Client ID |
| `WAN_STUDIO_PORTAL_CLIENT_ID` | Yes | Client (application) ID of the `WAN Safety Studio` app registration (lowercase GUID) | Entra ID > App registrations > the app > Overview, or your Terraform output |
| `WAN_STUDIO_PUBLIC_ORIGIN` | Yes | The web app's origin, for example `https://<app-name>.azurewebsites.net` | Your web app host name, or a custom domain bound to the app. Lowercase, no path, port, or trailing slash. The redirect URI `<origin>/auth/callback` must be on the app registration. |
| `WEBSITES_PORT` | Yes | `8000` | Fixed; the container listens on port 8000. |
| `WAN_STUDIO_DISABLE_AUTH` | No | `true` | Turns sign-in **off**. See [Disabling sign-in](#disabling-sign-in). |
| `WAN_STUDIO_ARMED` | Only while generation is enabled | `true` | Any non-empty value arms generation; delete the setting to disarm. See [section 5](#5-operate). |

Do **not** set `WAN_STUDIO_CONFIG_JSON`, `WAN_STUDIO_CONFIG`, or `WAN_STUDIO_FOUNDATION`. The container needs no studio configuration; setting any config form switches on the operator's full validation, which needs every Deploy output.

### Release pointers

`WAN_STUDIO_RELEASE_JSON` tells the portal which GPU environment and models to run. Both already exist in your Azure ML workspace.

```json
{
  "environmentId": "azureml://locations/eastus2/workspaces/00000000-0000-0000-0000-000000000000/environments/my-wan-env/versions/dc0d29031b73-8ea1320533d2599f",
  "modelsRef": "azureml://datastores/wan_blob/paths/models/wan/"
}
```

| Key | Where to get it |
| --- | --- |
| `environmentId` | Azure ML studio > **Assets > Environments** > your environment > the version you want. Use the full asset ID, which ends in `/versions/<version>`. The portal takes the version from the end of this ID. |
| `modelsRef` (optional) | The folder holding the model files: `azureml://datastores/<datastoreName>/paths/<folder>/`, with `diffusion_models/`, `text_encoders/` and `vae/` directly inside. **Defaults to `azureml://datastores/<datastoreName>/paths/models/wan/`**, which is where `scripts/upload-wan-models.sh` puts them, so you can leave it out. No Azure ML model asset is needed. A registered asset (`azureml:<name>:<version>`) also works. |
| `codeUri` (optional) | Only if the workflow code was uploaded somewhere other than `azureml://datastores/<datastoreName>/paths/code/<version>-wan/`. Studio > **Assets > Data > Datastores > Browse** shows the path. |

The portal only submits jobs that use exactly this environment and these models, so changing the value changes what runs.

### Foundation fields

These are the only foundation values the portal reads. Start from [docs/samples/foundation.json.example](samples/foundation.json.example), which uses placeholder IDs (`11111111-...`) and the example deployment `wan-demo` with name suffix `a1b2c3d4`.

| Field | Where to get it |
| --- | --- |
| `subscriptionId`, `tenantId` | The studio subscription and tenant, as lowercase GUIDs (config `subscription_id`, `tenant_id`). The sign-in token's tenant must equal `tenantId`. |
| `resourceGroupName` | `rg-<deployment_name>-<suffix>`, the studio resource group created by Deploy |
| `workspaceName` | `mlw-<deployment_name>-<suffix>` in that resource group |
| `storageAccountName` | `az storage account list -g <studio-rg> --query [0].name -o tsv` |
| `computeIdentityClientId` | `az identity show -g <studio-rg> -n id-<deployment_name>-<suffix>-gpu --query clientId -o tsv` |
| `computeId` | `/subscriptions/<subscriptionId>/resourceGroups/<resourceGroupName>/providers/Microsoft.MachineLearningServices/workspaces/<workspaceName>/computes/wan-gpu` |
| `computeName`, `containerName`, `datastoreName` | Fixed: `wan-gpu`, `wan-studio`, `wan_blob` |

Extra fields are ignored, so pasting a whole `foundation.json` also works. Key order and whitespace do not matter. Fill them in from your own infrastructure (the table above); in the Azure portal, [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#finding-the-values-in-the-azure-portal) shows where each one is. Uploaded input images go to a fixed `studio/web-inputs/` folder in the `containerName` container.

Update the value only if one of these fields changes.

Easiest in the Azure portal: web app > **Settings > Environment variables > App settings > Add**, paste the compact one-line JSON as the value, then **Apply**. With the Azure CLI, set the settings from a file to avoid shell quoting problems:

```powershell
$source = Get-Content docs\samples\foundation.json.example -Raw | ConvertFrom-Json   # your filled-in copy of the sample
$foundation = $source | Select-Object subscriptionId, tenantId, resourceGroupName, workspaceName,
  computeName, computeId, computeIdentityClientId, storageAccountName, containerName, datastoreName
$settingsFile = Join-Path $env:TEMP 'wan-portal-settings.json'
@{
  WAN_STUDIO_FOUNDATION_JSON            = $foundation | ConvertTo-Json -Compress
  WAN_STUDIO_RELEASE_JSON               = '{"environmentId":"<environment-asset-id>","modelsRef":"azureml://datastores/<datastoreName>/paths/models/wan/"}' # modelsRef is optional
  WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID = '<identity-client-id>'
  WAN_STUDIO_PORTAL_CLIENT_ID           = '<app-registration-client-id>'
  WAN_STUDIO_PUBLIC_ORIGIN              = 'https://<app-name>.azurewebsites.net'
  WEBSITES_PORT                         = '8000'
} | ConvertTo-Json | Set-Content -Encoding utf8 $settingsFile
az webapp config appsettings set -g <rg> -n <app-name> --settings "@$settingsFile" --output none
Remove-Item $settingsFile
```

None of these values is a secret. They do contain resource names and IDs, so treat them as internal.

### Disabling sign-in

Set `WAN_STUDIO_DISABLE_AUTH=true` and the portal skips Microsoft sign-in: every visitor is treated as one open user. With it on, `WAN_STUDIO_PORTAL_CLIENT_ID`, the app registration, the federated credential and the creators group aren't needed, and `/healthz` reports `"authentication": "disabled"`.

**Anyone who can reach the site can then submit GPU jobs and view results.** Use it only when something else already restricts access: the private endpoint and network rules, or App Service Authentication (Easy Auth) in front of the app. The portal logs a warning at startup. The host check and the CSRF/origin checks on writes stay on. Delete the setting to turn sign-in back on.
### Sign-in registration

The portal uses the `WAN Safety Studio` app registration (its client ID is `WAN_STUDIO_PORTAL_CLIENT_ID`). It signs in to Entra with a **federated credential that trusts the web app's managed identity**, so no client secret is stored on the web app. An identity owner (Application Administrator, or an owner of the registration) adds two items. If your Terraform manages the registration, declare both there instead; see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md#app-registration).

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
| Start | Set the `WAN_STUDIO_ARMED` app setting to `true`. The app restarts and generation becomes available. |
| Stop | Delete the `WAN_STUDIO_ARMED` app setting **before** stopping the compute. The app restarts disarmed. |
| New GPU environment or models | Update `WAN_STUDIO_RELEASE_JSON`. No image rebuild. |
| New portal code | Rebuild and push the image (section 2) and point the web app at the new tag. |

```powershell
# Arm
az webapp config appsettings set -g <rg> -n <app-name> --settings WAN_STUDIO_ARMED=true --output none

# Disarm
az webapp config appsettings delete -g <rg> -n <app-name> --setting-names WAN_STUDIO_ARMED --output none
```

In the Azure portal: web app > **Settings > Environment variables > App settings**, add or delete `WAN_STUDIO_ARMED`, then **Apply**.

If your automation manages app settings declaratively, make sure it does not add or remove `WAN_STUDIO_ARMED` on its own, which would arm or disarm the portal unexpectedly.

Every restart clears sign-in sessions, and a restart also ends any batch that is still submitting. Jobs already created in Azure ML continue running.

## Troubleshooting

For the full checklist across the compute, network, identity, and sign-in pieces, see [manual-deployment-troubleshooting.md](manual-deployment-troubleshooting.md).

Read container output with `az webapp log tail -g <rg> -n <app-name>` (turn on **App Service logs > Application logging: File System** first) or in the Log stream blade. A failed startup prints `Startup configuration error: <type>: <message>` naming the setting or file at fault. Errors after startup (job requests) print only the exception type, so that request details never reach the logs.

| Symptom or log message | Check |
| --- | --- |
| `KeyError: operation failed` at startup | `WAN_STUDIO_FOUNDATION_JSON` or `WAN_STUDIO_RELEASE_JSON` is missing or lacks a field; or `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` is missing. |
| `ValueError: operation failed` at startup | In order: a path-form setting (`WAN_STUDIO_FOUNDATION`) is set beside its `_JSON` form; `WAN_STUDIO_PUBLIC_ORIGIN` has a path, port, trailing slash, or uppercase letters; a foundation value is malformed (non-GUID ID, or `computeId` not built from the other fields); `WAN_STUDIO_PORTAL_CLIENT_ID` is missing or not a lowercase GUID; `WAN_STUDIO_RELEASE_JSON.modelsRef` is an `azureml:<name>` asset reference with no `:<version>`. |
| `IndexError` or `ModuleNotFoundError: azureml` at startup | `environmentId` doesn't end in `/versions/<version>`, or the image's `upstream` build context was wrong (rebuild as in section 2). |
| `JSONDecodeError: operation failed` at startup | A `_JSON` setting is not valid JSON. Set it from a file as shown above, or paste it into the portal as a single line. |
| Redirect URI mismatch at sign-in | Add `https://<host>/auth/callback` to the app registration's redirect URIs, using the same host as `WAN_STUDIO_PUBLIC_ORIGIN`. |
| `403 This portal accepts only its private App Service host` | Open the exact host in `WAN_STUDIO_PUBLIC_ORIGIN`, not the IP address or another host name. |
| `AADSTS700213` / `AADSTS70021` (no matching federated identity) | The federated credential subject must be the identity's **principal ID**. The issuer must be `https://login.microsoftonline.com/<tenant>/v2.0`. |
| Managed identity errors in the log | `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` must be the client ID of a user-assigned identity **attached to this web app**. |
| Image pull fails (`ImagePullFailure`, 401) | Check the AcrPull assignment, `acrUseManagedIdentityCreds`/`acrUserManagedIdentityID`, and, for a private ACR, `vnetImagePullEnabled` plus DNS and egress to the ACR private endpoint. |
| Container starts but the site never becomes healthy | `WEBSITES_PORT` must be `8000`. The health check path must be `/healthz`. |
| "GPU generation is not armed" | Set `WAN_STUDIO_ARMED=true` and let the app restart. |
| Videos do not play | Creators' browsers must resolve and reach the storage account's private blob endpoint. |
| Build fails fetching wheels (TLS or connection errors) | The build machine cannot reach `files.pythonhosted.org`. Use `--build-arg PYPI_INDEX=<mirror>` or build from a network that allows it. |
