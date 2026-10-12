# Manual deployment: configuration checklist and troubleshooting

For teams that deploy the studio with their own automation. [app-service-docker.md](app-service-docker.md) has the step-by-step container setup. This page lists every piece that has to line up, how to check each one, and the failures to expect. Work through the pieces in order. Each later piece depends on the earlier ones.

## How the pieces fit

```
Browser --(VPN/private DNS)--> App Service (container, user-assigned identity)
   |  sign-in: Entra ID (federated credential, no secret)
   |  uploads input images ---> Blob container  wan-studio
   |  submits / reads jobs  ---> Azure ML workspace --> compute cluster (GPU, Spot, min 0 / max 1)
                                                         |  runs the prepared image from ACR
                                                         |  reads models/code, writes video-library/ (compute identity)
```

Three identities are involved, and mixing them up is the most common cause of `403` errors:

| Identity | Used by | Needs |
| --- | --- | --- |
| Web app identity | Portal: uploads inputs, creates and reads jobs, lists the gallery | `Storage Blob Data Contributor` on the storage account, `AzureML Data Scientist` on the workspace, `AcrPull` on the ACR |
| Compute identity | The GPU job itself. Its client ID is `AzureMl__ComputeIdentityClientId`. | `Storage Blob Data Contributor` on the storage account, `AcrPull` on the ACR |
| Workspace identity | The workspace reading the datastore and pulling images | `Storage Blob Data Contributor` (storage), `Key Vault Secrets Officer` (vault), `AcrPull` (ACR) |

## 1. Checklist

Tick each item before debugging anything else.

**Azure ML and compute**
- [ ] Compute cluster exists in the workspace with the name you put in `AzureMl__Compute` (default `wan-gpu`).
- [ ] Cluster: Spot (`LowPriority`), min 0 and max 1 node, no public IP, in the GPU subnet, with the compute identity attached as its user-assigned identity.
- [ ] Datastore named `AzureMl__Datastore` (default `wan_blob`) points at the storage account and container `wan-studio`, using **identity-based** access (`serviceDataAccessAuthIdentity: WorkspaceUserAssignedIdentity`), not account keys.
- [ ] The GPU environment, the model files in blob storage (`models/wan/` in the `wan-studio` container), and the workflow code upload exist, and `AzureMl__Release__EnvironmentId` (with `AzureMl__Release__ModelsRef` and `AzureMl__Release__CodeUri` when they are not the defaults) points at them (see [the release settings](#release-settings-and-the-armed-switch)).
- [ ] GPU quota: both `TotalLowPriorityCores` and the VM family have enough free cores for one node, and the region has Spot capacity.

**Network**
- [ ] Private DNS resolves, from the web app's VNet, the Blob, Azure ML (`api.azureml.ms`, `notebooks.azure.net`), ACR (`azurecr.io` and its `data` endpoint), and the web app's own `privatelink.azurewebsites.net` zone.
- [ ] The GPU subnet can reach the storage, ACR, and Azure ML private endpoints on 443, and the Azure ML service tags it needs.
- [ ] The web app's integration subnet reaches Entra ID and `management.azure.com`.

**Web app**
- [ ] One instance, user-assigned identity, container image pulled by identity, `WEBSITES_PORT=8080`, health check `/healthz`.
- [ ] App settings: the nine `AzureMl__*` resource settings, `AzureMl__Release__EnvironmentId`, `AzureMl__Armed`, `AZURE_CLIENT_ID`, `Auth__ClientId`, `Auth__PublicOrigin`, `Generation__Provider=azureml`, `WEBSITES_PORT`. No `WAN_STUDIO_*` settings; they were removed.
- [ ] The three role assignments above exist for the web app identity.

**Sign-in**
- [ ] App registration has the redirect URI `<origin>/auth/callback` and a federated credential whose subject is the web app identity's **principal ID**.
- [ ] Creators are in the `WAN Safety Studio Creators` group, and the group is assigned to the `VideoCreator` app role on the enterprise application (see [App registration](#app-registration)).

## Configuration reference

### App settings

| Name | Required | Value |
| --- | --- | --- |
| `AzureMl__SubscriptionId`, `AzureMl__TenantId` | Yes | The studio subscription and tenant (lowercase GUIDs) |
| `AzureMl__ResourceGroup`, `AzureMl__Workspace`, `AzureMl__Compute` | Yes | Resource group, Azure ML workspace and GPU compute names |
| `AzureMl__ComputeIdentityClientId` | Yes | Client ID of the compute (GPU job) identity (lowercase GUID) |
| `AzureMl__StorageAccount`, `AzureMl__Container` | Yes | The studio storage account and container |
| `AzureMl__Datastore` | No | Default `workspaceblobstore`; the studio's datastore is `wan_blob` |
| `AzureMl__Release__EnvironmentId`, `AzureMl__Release__ModelsRef`, `AzureMl__Release__CodeUri` | Yes / No / No | Pointers to the GPU environment, models and code, below |
| `AzureMl__Armed` | Yes | `true` while generation is enabled, otherwise `false` |
| `AZURE_CLIENT_ID` | Yes | Client ID of the web app's user-assigned identity (lowercase GUID) |
| `Auth__ClientId` | Yes | Client (application) ID of the app registration (lowercase GUID) |
| `Auth__PublicOrigin` | Yes | `https://<host>`: lowercase, no path, port, or trailing slash. Its `/auth/callback` must be a redirect URI on the app registration. |
| `Auth__TenantId` | No | Defaults to `AzureMl__TenantId` |
| `Auth__Mode` | No | `Entra` outside Development; `None` turns sign-in off for everyone who can reach the site (see [Disabling sign-in](app-service-docker.md#disabling-sign-in)). `Auth__ClientId` is then not needed. |
| `KeyVault__Uri` | No | Adds Azure Key Vault as a configuration source (for example the secret `Auth--ClientSecret`) |
| `WEBSITES_PORT` | Yes | `8080` |
| `Generation__Provider` | Yes | `azureml` |
| `PORT` | No | Leave unset. The container listens on 8080. |

The complete list of keys, defaults and the tuning options is in the [configuration reference](configuration.md). The removed `WAN_STUDIO_*` JSON settings (`..._FOUNDATION_JSON`, `..._RELEASE_JSON`, `..._ARMED` and the rest) are ignored by current images; delete them from older sites.

### Resource settings

| Setting | Rule |
| --- | --- |
| `AzureMl__SubscriptionId`, `AzureMl__TenantId`, `AzureMl__ComputeIdentityClientId` | Lowercase GUIDs. The sign-in token's tenant must equal `AzureMl__TenantId` (or `Auth__TenantId`). |
| `AzureMl__ResourceGroup`, `AzureMl__Workspace`, `AzureMl__Compute`, `AzureMl__StorageAccount`, `AzureMl__Container`, `AzureMl__Datastore` | Letters, digits, `_`, `.`, `-` only |

The compute resource ID is derived (`/subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.MachineLearningServices/workspaces/<workspace>/computes/<compute>`) and never configured. `AzureMl__Container` is the Blob container, which holds `video-library/` (output) and `studio/web-inputs/` (uploads). `AzureMl__Datastore` is the Azure ML datastore that points to it, and the job guard only allows paths under it.

### Release settings and the armed switch

`AzureMl__Release__*` point at the GPU assets in your workspace, and `AzureMl__Armed` is an on/off switch.

| Setting | Rule |
| --- | --- |
| `AzureMl__Release__EnvironmentId` | Full asset ID of the Azure ML environment, ending in `/versions/<version>` (or `azureml:<name>:<version>`). The version is read from the end of it. Studio > **Assets > Environments** > the version. Empty means not prepared. |
| `AzureMl__Release__ModelsRef` (optional) | Datastore folder with the model files: `azureml://datastores/<datastoreName>/paths/<folder>/`, containing `diffusion_models/`, `text_encoders/`, `vae/`. Defaults to `azureml://datastores/<datastoreName>/paths/models/wan/` (where `scripts/upload-wan-models.sh` uploads), so it can be omitted. No model asset needs registering. `azureml:<name>:<version>` also works. |
| `AzureMl__Release__CodeUri` (optional) | Defaults to `azureml://datastores/<datastoreName>/paths/code/<version>-wan/`. Set it only if the workflow code lives elsewhere. |

`AzureMl__Armed` is `true` while generation is enabled and `false` otherwise; it is read on every submission. Write `false` to disarm instead of deleting the setting. The portal only submits jobs with exactly this environment, these models, and the configured compute, so check the values carefully.

There is no manifest file to find: the portal reads these settings as ordinary configuration.

### Finding the values in the Azure portal

No CLI needed. Menu names can shift, so use the portal search box if one has moved.

| Value | Where |
| --- | --- |
| `AzureMl__SubscriptionId` | **Subscriptions** > the subscription's ID |
| `AzureMl__TenantId` | **Microsoft Entra ID > Overview > Tenant ID** |
| `AzureMl__ResourceGroup`, `AzureMl__Workspace`, `AzureMl__StorageAccount` | Open the studio resource group. The names of the Azure Machine Learning workspace and the Storage account in its resource list. |
| `AzureMl__Compute` | Azure ML studio (workspace > **Launch studio**) > **Manage > Compute > Compute clusters** |
| `AzureMl__ComputeIdentityClientId` | Open the cluster in the studio to see its user-assigned identity, then open that Managed Identity resource in the portal: **Overview > Client ID** |
| `AzureMl__Container` | Storage account > **Data storage > Containers** (default `wan-studio`) |
| `AzureMl__Datastore` | Studio > **Assets > Data > Datastores**: the datastore pointing at that container (default `wan_blob`) |
| `AZURE_CLIENT_ID` | Web app > **Settings > Identity > User assigned**, open the identity, **Overview > Client ID** |
| `Auth__ClientId` | **Entra ID > App registrations > WAN Safety Studio > Overview > Application (client) ID** |
| `AzureMl__Release__EnvironmentId` | Studio > **Assets > Environments** > the environment > the version you want (full asset ID) |
| Role assignments | Storage account, workspace, and ACR > **Access control (IAM) > Role assignments** |
| Redirect URI, federated credential | App registration > **Authentication**, and **Certificates & secrets > Federated credentials** |
| Group assigned to the app role | **Entra ID > Enterprise applications > WAN Safety Studio > Users and groups** |
### App registration

Everything below can be created with Terraform's `azuread` provider or by hand. The portal itself checks that the sign-in token is single-tenant, its audience is `clientId`, and its `roles` claim contains `VideoCreator`.

| Item | Value | `azuread` resource |
| --- | --- | --- |
| Application | name `WAN Safety Studio`, single tenant (`AzureADMyOrg`) | `azuread_application` |
| Redirect URI | platform Web, `<origin>/auth/callback` | `web.redirect_uris` |
| App role | value `VideoCreator`, member type `User`, enabled | `app_role` |
| API permissions | Microsoft Graph delegated `openid`, `profile` only | `required_resource_access` |
| Enterprise application | **Assignment required = yes** | `azuread_service_principal` (`app_role_assignment_required = true`) |
| Creators group | security group, for example `WAN Safety Studio Creators` | `azuread_group` |
| Group to role | creators group assigned to `VideoCreator` | `azuread_app_role_assignment` |
| Federated credential | issuer `https://login.microsoftonline.com/<tenant-id>/v2.0`, subject = web app identity **principal ID**, audience `api://AzureADTokenExchange` | `azuread_application_federated_identity_credential` |
| Admin consent | tenant-wide grant of `openid profile` | `azuread_service_principal_delegated_permission_grant` |

Admin consent needs an Entra admin, and it's the step most often done by hand. Without it, sign-in shows "Need admin approval". No client secret is needed for the container. Pass the registration's `clientId` output to the app setting `Auth__ClientId`; nothing is written into the image.

## 2. Verification commands

Run these from a machine inside the network.

```powershell
# Web app is up and has the right origin
curl https://<app-name>.azurewebsites.net/healthz        # {"status": "ok", "authentication": "required"}

# Container started? Turn on App Service logs (Application logging: File System) first.
az webapp log tail -g <rg> -n <app-name>

# Identity roles on the three scopes
$p = az identity show -g <rg> -n <portal-identity> --query principalId -o tsv
az role assignment list --assignee $p --all --query "[].{role:roleDefinitionName,scope:scope}" -o table

# Compute exists and matches
az ml compute show -g <rg> -w <workspace> -n <compute-name> --query "{size:size,priority:tier,min:min_instances,max:max_instances,identity:identity.user_assigned_identities}" -o json

# Quota (Spot cores and family)
az ml compute list-usage -g <rg> -w <workspace> -o table      # or: az vm list-usage --location <region>

# SKU offered and unrestricted in the region (the restrictions list should be empty)
az vm list-skus --location <region> --size <vm-size> --all --query "[].restrictions" -o json

# DNS, run from the web app (Kudu/SSH) or a VM in the same VNet
nslookup <storage-account>.blob.core.windows.net          # must return a private IP
nslookup <workspace-guid>.workspace.<region>.api.azureml.ms
```

Reading startup logs: a configuration failure prints `Startup configuration error: <type>: <message>`, which names the setting at fault. Images built before this change print only `<type>: operation failed`; the cause list for each type is in [Startup failures](#3-startup-failures).

## Reading the portal log

The container writes timestamped lines to stdout (App Service **Log stream**, or `az webapp log tail`). Set `WAN_STUDIO_LOG_LEVEL=DEBUG` for extra detail. No request bodies, tokens or URL query values are ever logged.

| Line | Meaning |
| --- | --- |
| `Portal starting: pid=… instance=… origin=… auth=…` | One per process start. A second one means the container restarted, and every in-memory batch is gone. |
| `Workspace: …`, `Release: …` | The values the portal actually loaded: resource group, workspace, compute, storage, container, environment, models, code. Compare them with what you meant to set. |
| `req=<id> METHOD /path -> status in Nms \| reason` | One per request (health checks and static files only at DEBUG). The reason names why a 4xx/5xx happened. The `Reference:` shown in the browser is this `req` id. |
| `start/done/FAILED <step>` | Each Azure call (workspace read, compute read, storage read, image upload, job submit) with its duration and memory use. FAILED shows the HTTP status, Azure error code, request ids and message, plus the file:line where it failed. |
| `azure.core…http_logging_policy: Request URL … Response status` | Every call to Azure ML, Storage and the identity endpoint, with Azure's request ids for a support ticket. |
| `Batch <id> created / job n of m created / finished` | The submit trail. `finished … error=True` is followed by the reason in the browser and the `Batch … stopped` line above it. |
| `Batch <id> lookup failed: unknown id (known batches 0, pid …)` | The browser asked for a batch this process doesn't have: the process restarted, or a second instance answered. |
| `Portal shutting down on a stop signal` | App Service stopped the container normally. |
| `Process exiting normally` | The process ended on its own. |
| startup lines, then **no** shutdown or exit line, then startup lines again | The process was killed hard, which is usually out of memory. Check the `mem=` values against the limit. |
| `Fatal startup error: <type>: <message> at file:line` | A startup failure. The file and line name the setting or file at fault. |

If Python crashes natively, `faulthandler` prints the Python stack of every thread before the process dies.
## 3. Startup failures

| Log line | Meaning and fix |
| --- | --- |
| Container never starts, no app log | Image pull failed. Check `AcrPull`, `acrUseManagedIdentityCreds` and `acrUserManagedIdentityID`, `vnetImagePullEnabled` for a private ACR, and ACR private DNS. Look in **Deployment Center > Logs**. |
| `FileNotFoundError: operation failed; inspect Status and run Stop...` (older image) | A required file or release pointer is missing. Rebuild the image from this repo to get `Startup configuration error: <type>: <message>`, which names the setting at fault. |
| `KeyError: operation failed` | An `AzureMl__*` setting (or `AzureMl__Release__EnvironmentId`) is missing, or `AZURE_CLIENT_ID` is missing. |
| `ValueError: operation failed` | Checked in this order: `Auth__PublicOrigin` not lowercase `https://host` with no path or port; a resource setting malformed (non-GUID, or a name with invalid characters); `Auth__ClientId` missing or not a lowercase GUID; `AzureMl__Release__ModelsRef` is an `azureml:<name>` asset reference with no `:<version>`. |
| `JSONDecodeError: operation failed` | A value that should be JSON (for example an `Agent__*` setting) is malformed. Set app settings from a file (see the doc above), not inline in a shell. |
| Health check fails and the app restarts in a loop | Same causes as above. Also check that `WEBSITES_PORT` is 8080. |

## 4. Sign-in problems

| Symptom | Cause and fix |
| --- | --- |
| `AADSTS50011` redirect URI mismatch | The registration lacks `<origin>/auth/callback`, or `Auth__PublicOrigin` differs from the host users browse to. The URI must match the origin exactly. |
| `AADSTS70025` or `AADSTS700213` (no matching federated identity record) | The federated credential's subject must be the identity's **principal ID** (object ID), issuer `https://login.microsoftonline.com/<tenant-id>/v2.0`, audience `api://AzureADTokenExchange`. Tenant-ID or client-ID values here are the usual mistake. |
| `Microsoft sign-in failed. Check app assignment and tenant` | The user isn't assigned to the app, or is from another tenant. Add the user to the creators group, and confirm the group is assigned to the enterprise application. |
| `Your account must be assigned the Video Creator role through the approved creator group` | The ID token's `roles` claim doesn't contain `VideoCreator`. Check that the app role exists with that exact value, that the creators group is assigned to it on the enterprise application, and that the user is a member of the group. Then sign out and in. Group assignment to an app role needs an Entra ID P1/P2 license. |
| `This portal accepts only its private App Service host` | The request came in with a different `Host` header (custom domain, Front Door, or a direct IP). Set `Auth__PublicOrigin` to the host users actually use. |
| `Invalid request origin or CSRF token` | The page is served from a host other than the configured origin, or cookies are blocked. Use the configured origin. |
| `Sign-in has expired` after a short time | The app restarted (sessions are in memory) or was scaled beyond one instance. Keep one instance, and sign in again. |

## 5. Generation problems

Check the gate first. Generation is available only when `AzureMl__Armed` is `true`.

| Portal message or symptom | Cause and fix |
| --- | --- |
| `GPU generation is not armed` | `AzureMl__Armed` is `false` or unset. Set it to `true` (the operator `Start` action does this). |
| `The operator disarmed generation` | The setting was set to `false` while a request was in progress. Re-arm it, or this is expected after Stop. |
| `A batch is still being submitted` | Wait for job IDs. Don't resubmit. |
| Submission is rejected by the job guard | The job's compute differs from `AzureMl__Compute`; the job's environment or models differ from the `AzureMl__Release__*` settings; the timeout isn't within 7200 seconds; more than one instance or one video per job; an input is not a private datastore path. A SKU or compute name different from what is configured also lands here. |
| Job status `ServerSafetyVerificationFailed-CancellationRequested` | The server-side job didn't read back as one instance, a limit of 2 hours or less, and the right compute. The portal cancelled it on purpose. Check that the compute name matches and that a custom Azure ML policy isn't rewriting the job. |
| Job is `Queued` or `Preparing` for a long time | Spot capacity or quota. The node is created on the first job, so allow several minutes. Check cluster state and quota: `az ml compute show` and `az ml compute list-usage`. |
| Job fails to pull the image | The compute identity (or the workspace identity) lacks `AcrPull`; ACR private endpoint or DNS is unreachable from the GPU subnet; the environment's image digest no longer exists in the registry. |
| Job fails with `Failed to mount URI azureml://…/paths/code/<version>-wan/ … INPUT_repo` | The code folder doesn't exist at that path. Upload it with `scripts/upload-wan-code.sh <account> <container> <prefix>` (the container the datastore in `AzureMl__Datastore` points at), then set `AzureMl__Release__CodeUri` to `azureml://datastores/<datastoreName>/paths/<prefix>/`. The folder must have `azureml/` and `src/` at its root. |
| Job fails or finds no models | The folder at `AzureMl__Release__ModelsRef` must contain `diffusion_models/`, `text_encoders/` and `vae/` directly (no extra parent folder), and the datastore must be the one in `AzureMl__Datastore`. Check in the portal: Storage account > Containers > `wan-studio` > `models/wan/`. | The compute identity lacks `Storage Blob Data Contributor`; the storage firewall blocks the GPU subnet; the datastore is not identity-based. |
| Job fails to start with a managed identity error | `AzureMl__ComputeIdentityClientId` isn't the identity attached to the cluster. |
| `403` or `AuthorizationFailed` when the portal lists jobs or the gallery | The web app identity lacks `AzureML Data Scientist` on the workspace or `Storage Blob Data Contributor` on the storage account. Role assignments can take several minutes to apply, and the app caches tokens, so restart the app after changing them. |
| Gallery is empty after a successful job | Output must be under `video-library/` in the `wan-studio` container. The compute identity needs write access there. |
| Input upload fails | The Blob private endpoint or DNS isn't reachable from the web app. `nslookup` the account name from the app and confirm a private IP. |

The portal's Status page shows whether the release and gate are loaded. If it shows the wrong state, restart the app.

## 6. Changing things safely

| Change | Needed |
| --- | --- |
| Arm or disarm | Set `AzureMl__Armed` to `true` or `false`. The app restarts. |
| Different GPU SKU, node idle time, or subnet | Update the cluster. If the compute name or identity changes, update `AzureMl__Compute` / `AzureMl__ComputeIdentityClientId`. A SKU change alone needs nothing in the portal. |
| New origin or host name | Update `Auth__PublicOrigin`, the redirect URI, and the DNS. No rebuild. |
| New GPU environment or models | Update `AzureMl__Release__EnvironmentId` (and `ModelsRef` / `CodeUri` if they changed). No rebuild. |
| New portal code | Rebuild and push the image, point the web app at the new tag. |

## 7. What to collect when asking for help

1. Startup log lines (exception type is enough, since messages are hidden on purpose).
2. Output of the verification commands in section 2, with IDs redacted if needed.
3. The job name and its status from Azure ML studio, plus the job's `std_log.txt` for failures after the job starts.
4. The portal's `/api/status` response after signing in.

Do not share `AzureMl__Armed` or the resource settings outside the team. They hold no secrets, but they identify the deployment.
