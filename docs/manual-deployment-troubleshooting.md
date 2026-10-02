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
| Compute identity | The GPU job itself. Its client ID is `computeIdentityClientId`. | `Storage Blob Data Contributor` on the storage account, `AcrPull` on the ACR |
| Workspace identity | The workspace reading the datastore and pulling images | `Storage Blob Data Contributor` (storage), `Key Vault Secrets Officer` (vault), `AcrPull` (ACR) |

## 1. Checklist

Tick each item before debugging anything else.

**Azure ML and compute**
- [ ] Compute cluster exists in the workspace with the name you put in `computeName` (default `wan-gpu`).
- [ ] Cluster: Spot (`LowPriority`), min 0 and max 1 node, no public IP, in the GPU subnet, with the compute identity attached as its user-assigned identity.
- [ ] Datastore named `datastoreName` (default `wan_blob`) points at the storage account and container `wan-studio`, using **identity-based** access (`serviceDataAccessAuthIdentity: WorkspaceUserAssignedIdentity`), not account keys.
- [ ] The prepared environment, models, and code exist in the workspace (the Prepare and Publish step you already ran). `prepared-wan.json` lists their names.
- [ ] GPU quota: both `TotalLowPriorityCores` and the VM family have enough free cores for one node, and the region has Spot capacity.

**Network**
- [ ] Private DNS resolves, from the web app's VNet, the Blob, Azure ML (`api.azureml.ms`, `notebooks.azure.net`), ACR (`azurecr.io` and its `data` endpoint), and the web app's own `privatelink.azurewebsites.net` zone.
- [ ] The GPU subnet can reach the storage, ACR, and Azure ML private endpoints on 443, and the Azure ML service tags it needs.
- [ ] The web app's integration subnet reaches Entra ID and `management.azure.com`.

**Web app**
- [ ] One instance, user-assigned identity, container image pulled by identity, `WEBSITES_PORT=8000`, health check `/healthz`.
- [ ] App settings: `WAN_STUDIO_FOUNDATION_JSON`, `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID`, `WAN_STUDIO_PUBLIC_ORIGIN`. No `WAN_STUDIO_CONFIG*`.
- [ ] The three role assignments above exist for the web app identity.

**Sign-in**
- [ ] App registration has the redirect URI `<origin>/auth/callback` and a federated credential whose subject is the web app identity's **principal ID**.
- [ ] Creators are in the `WAN Safety Studio Creators` group, and the group is assigned to the `VideoCreator` app role on the enterprise application (see [App registration](#app-registration)).

## Configuration reference

### App settings

| Name | Required | Value |
| --- | --- | --- |
| `WAN_STUDIO_FOUNDATION_JSON` | Yes | JSON object with the 11 fields below, as a single-line string |
| `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` | Yes | Client ID of the web app's user-assigned identity (lowercase GUID) |
| `WAN_STUDIO_PORTAL_CLIENT_ID` | Yes | Client (application) ID of the app registration (lowercase GUID) |
| `WAN_STUDIO_PUBLIC_ORIGIN` | Yes | `https://<host>`: lowercase, no path, port, or trailing slash. Its `/auth/callback` must be a redirect URI on the app registration. |
| `WEBSITES_PORT` | Yes | `8000` |
| `WAN_STUDIO_ARMED` | Only while generation is enabled | JSON object with 3 fields, below. Remove the setting to disarm. |
| `PORT` | No | Leave unset. The container listens on 8000. |

Do not set `WAN_STUDIO_CONFIG`, `WAN_STUDIO_CONFIG_JSON`, or `WAN_STUDIO_FOUNDATION`. The first two switch on full config validation, and the last conflicts with the `_JSON` form.

### `WAN_STUDIO_FOUNDATION_JSON`

All 11 fields are required. Extra fields are ignored.

```json
{
  "subscriptionId": "11111111-1111-1111-1111-111111111111",
  "tenantId": "22222222-2222-2222-2222-222222222222",
  "deploymentName": "wan-demo",
  "resourceGroupName": "rg-wan-demo-a1b2c3d4",
  "workspaceName": "mlw-wan-demo-a1b2c3d4",
  "computeName": "wan-gpu",
  "computeId": "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-wan-demo-a1b2c3d4/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo-a1b2c3d4/computes/wan-gpu",
  "computeIdentityClientId": "44444444-4444-4444-4444-444444444444",
  "storageAccountName": "stwandemoa1b2c3d4",
  "containerName": "wan-studio",
  "datastoreName": "wan_blob"
}
```

| Field | Rule |
| --- | --- |
| `subscriptionId`, `tenantId`, `computeIdentityClientId` | Lowercase GUIDs. The sign-in token's tenant must equal `tenantId`. |
| `deploymentName` | 3 to 16 characters, lowercase letters, digits, and hyphens, starting with a letter and not ending in a hyphen |
| `resourceGroupName`, `workspaceName`, `computeName`, `storageAccountName`, `containerName`, `datastoreName` | Letters, digits, `_`, `.`, `-` only |
| `computeId` | Must be exactly `/subscriptions/<subscriptionId>/resourceGroups/<resourceGroupName>/providers/Microsoft.MachineLearningServices/workspaces/<workspaceName>/computes/<computeName>` (case-insensitive) |
| All 11 | Must match, character for character, the values used when the release was prepared |

`containerName` is the Blob container, which holds `video-library/` (output) and `<deploymentName>/web-inputs/` (uploads). `datastoreName` is the Azure ML datastore that points to it, and the job guard only allows paths under it.

### `WAN_STUDIO_ARMED`

This is the content of `armed.json`, written when the operator arms the compute.

```json
{"compute": "wan-gpu", "profile": "wan", "version": "dc0d29031b73-0123456789abcdef"}
```

| Field | Rule |
| --- | --- |
| `compute` | Equal to `computeName` |
| `profile` | `wan` |
| `version` | Equal to `version` in `prepared-wan.json` inside the image |

The portal ignores any other keys. A value that doesn't match the baked-in release leaves generation refused, with no error at startup.

**Finding `version`.** It isn't the image digest. It's a content hash computed during Prepare (`<first 12 chars of the pinned upstream commit>-<16 hex chars>`) and stored in `prepared-wan.json`, which the build copies into `/app/release/`. Rebuilding the image never changes it. Look it up from the image the portal runs:

```powershell
az acr login --name <registry>
docker run --rm --entrypoint cat <image> /app/release/prepared-wan.json | ConvertFrom-Json | Select-Object version, profile, sourceSha, scopeFingerprint
```

The same string is the version of the Azure ML environment `<deploymentName>-wan`, so it should match: `az ml environment list -g <rg> -w <workspace> --name <deploymentName>-wan -o table`. If the two differ, the portal image and the Azure ML assets came from different Prepare runs. Rebuild the image from the Prepare that created the assets, or re-run Prepare.

### File baked into the image

`prepared-wan.json` is generated by the prepare step. Don't edit it. The portal checks `sourceSha`, `profile`, `scopeFingerprint` (the hash of the 11 foundation fields), and the hashes of the bundled upstream files. It is the only file in the image that comes from your deployment. There is no `portal-auth.json` in the container: the registration's client ID is `WAN_STUDIO_PORTAL_CLIENT_ID`, the tenant comes from the foundation, and the role name `VideoCreator` and the redirect URI (`WAN_STUDIO_PUBLIC_ORIGIN` + `/auth/callback`) are fixed by the portal.

### Finding the values in the Azure portal

No CLI needed. Menu names can shift, so use the portal search box if one has moved.

| Value | Where |
| --- | --- |
| `subscriptionId` | **Subscriptions** > the subscription's ID |
| `tenantId` | **Microsoft Entra ID > Overview > Tenant ID** |
| `resourceGroupName`, `workspaceName`, `storageAccountName` | Open the studio resource group. The names of the Azure Machine Learning workspace and the Storage account in its resource list. |
| `computeName` | Azure ML studio (workspace > **Launch studio**) > **Manage > Compute > Compute clusters** |
| `computeId` | Not shown. Build it: `/subscriptions/<subscriptionId>/resourceGroups/<resourceGroupName>/providers/Microsoft.MachineLearningServices/workspaces/<workspaceName>/computes/<computeName>` |
| `computeIdentityClientId` | Open the cluster in the studio to see its user-assigned identity, then open that Managed Identity resource in the portal: **Overview > Client ID** |
| `containerName` | Storage account > **Data storage > Containers** (default `wan-studio`) |
| `datastoreName` | Studio > **Assets > Data > Datastores**: the datastore pointing at that container (default `wan_blob`) |
| `deploymentName` | Not an Azure object. It is the folder used for uploads and the name the release was prepared with. Try the top-level folder in the container next to `video-library`, or the prefix of the environment name. If the portal fails at startup with the fingerprint error, this is the first value to suspect. |
| `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` | Web app > **Settings > Identity > User assigned**, open the identity, **Overview > Client ID** |
| `WAN_STUDIO_PORTAL_CLIENT_ID` | **Entra ID > App registrations > WAN Safety Studio > Overview > Application (client) ID** |
| `version` (for `WAN_STUDIO_ARMED`) | Studio > **Assets > Environments** > the prepared environment > the version string. It must equal `version` in the image's `prepared-wan.json`. |
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

Admin consent needs an Entra admin, and it's the step most often done by hand. Without it, sign-in shows "Need admin approval". No client secret is needed for the container. Pass the registration's `clientId` output to the app setting `WAN_STUDIO_PORTAL_CLIENT_ID`; nothing is written into the image.

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

Reading startup logs: the portal prints only the exception type on failure, for example `ValueError: operation failed`. The cause list for each type is in [Startup failures](#3-startup-failures).

## 3. Startup failures

| Log line | Meaning and fix |
| --- | --- |
| Container never starts, no app log | Image pull failed. Check `AcrPull`, `acrUseManagedIdentityCreds` and `acrUserManagedIdentityID`, `vnetImagePullEnabled` for a private ACR, and ACR private DNS. Look in **Deployment Center > Logs**. |
| `KeyError: operation failed` | `WAN_STUDIO_FOUNDATION_JSON` missing or lacks one of the 11 fields; a config setting is also set; or `WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID` is missing. |
| `ValueError: operation failed` | Checked in this order: both `WAN_STUDIO_FOUNDATION` and `_JSON` set; `WAN_STUDIO_PUBLIC_ORIGIN` not lowercase `https://host` with no path or port; a foundation value malformed (non-GUID, or `computeId` not equal to `.../workspaces/<workspaceName>/computes/<computeName>`); the foundation values differ from what Prepare used; the image's upstream files differ from `prepared-wan.json`; `WAN_STUDIO_PORTAL_CLIENT_ID` is missing (the portal then looks for a `portal-auth.json` file) or not a lowercase GUID. |
| `JSONDecodeError: operation failed` | A `_JSON` setting or `WAN_STUDIO_ARMED` is not valid JSON. Set app settings from a file (see the doc above), not inline in a shell. |
| `Published release differs from this studio` appears in the log | The fingerprint of the 11 foundation fields differs from the manifest. One value has a typo or a case difference. Compare each value with the cache's `foundation.json`. |
| Health check fails and the app restarts in a loop | Same causes as above. Also check that `WEBSITES_PORT` is 8000. |

## 4. Sign-in problems

| Symptom | Cause and fix |
| --- | --- |
| `AADSTS50011` redirect URI mismatch | The registration lacks `<origin>/auth/callback`, or `WAN_STUDIO_PUBLIC_ORIGIN` differs from the host users browse to. The URI must match the origin exactly. |
| `AADSTS70025` or `AADSTS700213` (no matching federated identity record) | The federated credential's subject must be the identity's **principal ID** (object ID), issuer `https://login.microsoftonline.com/<tenant-id>/v2.0`, audience `api://AzureADTokenExchange`. Tenant-ID or client-ID values here are the usual mistake. |
| `Microsoft sign-in failed. Check app assignment and tenant` | The user isn't assigned to the app, or is from another tenant. Add the user to the creators group, and confirm the group is assigned to the enterprise application. |
| `Your account must be assigned the Video Creator role through the approved creator group` | The ID token's `roles` claim doesn't contain `VideoCreator`. Check that the app role exists with that exact value, that the creators group is assigned to it on the enterprise application, and that the user is a member of the group. Then sign out and in. Group assignment to an app role needs an Entra ID P1/P2 license. |
| `This portal accepts only its private App Service host` | The request came in with a different `Host` header (custom domain, Front Door, or a direct IP). Set `WAN_STUDIO_PUBLIC_ORIGIN` to the host users actually use and rebuild the image for it. |
| `Invalid request origin or CSRF token` | The page is served from a host other than the configured origin, or cookies are blocked. Use the configured origin. |
| `Sign-in has expired` after a short time | The app restarted (sessions are in memory) or was scaled beyond one instance. Keep one instance, and sign in again. |

## 5. Generation problems

Check the gate first. Generation is available only when `WAN_STUDIO_ARMED` is set and matches the prepared release.

| Portal message or symptom | Cause and fix |
| --- | --- |
| `GPU generation is not armed` | `WAN_STUDIO_ARMED` is unset. Set it to the content of `armed.json` (see [section 5 of the Docker doc](app-service-docker.md#5-operate)). |
| `The operator disarmed generation` | The setting was removed while a request was in progress. Re-arm it, or this is expected after Stop. |
| `WAN preparation is not complete` | The prepared manifest is missing from the image, so the image was built without `prepared-wan.json`. Rebuild with the `release` build context. |
| Armed, but every submission fails with a `ValueError` | `WAN_STUDIO_ARMED` has a `version` that differs from `prepared-wan.json`. After a new Prepare, re-run Start and copy the new `armed.json`. The image must match the same Prepare. |
| `A batch is still being submitted` | Wait for job IDs. Don't resubmit. |
| Submission is rejected by the job guard | The job's compute differs from `computeName`; the timeout isn't within 7200 seconds; more than one instance or one video per job; an input is not a private datastore path. A SKU or compute name different from what is in the foundation also lands here. |
| Job status `ServerSafetyVerificationFailed-CancellationRequested` | The server-side job didn't read back as one instance, a limit of 2 hours or less, and the right compute. The portal cancelled it on purpose. Check that the compute name matches and that a custom Azure ML policy isn't rewriting the job. |
| Job is `Queued` or `Preparing` for a long time | Spot capacity or quota. The node is created on the first job, so allow several minutes. Check cluster state and quota: `az ml compute show` and `az ml compute list-usage`. |
| Job fails to pull the image | The compute identity (or the workspace identity) lacks `AcrPull`; ACR private endpoint or DNS is unreachable from the GPU subnet; the environment's image digest no longer exists in the registry. |
| Job fails reading models or writing output | The compute identity lacks `Storage Blob Data Contributor`; the storage firewall blocks the GPU subnet; the datastore is not identity-based. |
| Job fails to start with a managed identity error | `computeIdentityClientId` in the foundation isn't the identity attached to the cluster. |
| `403` or `AuthorizationFailed` when the portal lists jobs or the gallery | The web app identity lacks `AzureML Data Scientist` on the workspace or `Storage Blob Data Contributor` on the storage account. Role assignments can take several minutes to apply, and the app caches tokens, so restart the app after changing them. |
| Gallery is empty after a successful job | Output must be under `video-library/` in the `wan-studio` container. The compute identity needs write access there. |
| Input upload fails | The Blob private endpoint or DNS isn't reachable from the web app. `nslookup` the account name from the app and confirm a private IP. |

The portal's Status page shows whether the release and gate are loaded. If it shows the wrong state, restart the app.

## 6. Changing things safely

| Change | Needed |
| --- | --- |
| Rotate the `WAN_STUDIO_ARMED` value or arm/disarm | Change only that app setting. The app restarts. |
| Different GPU SKU, node idle time, or subnet | Update the cluster. If `computeName`, `computeId`, or the identity changes, update `WAN_STUDIO_FOUNDATION_JSON`. A SKU change alone needs nothing in the portal. |
| New origin or host name | Update `WAN_STUDIO_PUBLIC_ORIGIN`, the redirect URI, and the DNS. No rebuild. |
| New Prepare / upstream update | New image, new `armed.json`. |
| Rename any of the 11 foundation fields' values | New Prepare, image, and `WAN_STUDIO_FOUNDATION_JSON`. The fingerprint changes. |

## 7. What to collect when asking for help

1. Startup log lines (exception type is enough, since messages are hidden on purpose).
2. Output of the verification commands in section 2, with IDs redacted if needed.
3. The job name and its status from Azure ML studio, plus the job's `std_log.txt` for failures after the job starts.
4. The portal's `/api/status` response after signing in.

Do not share `WAN_STUDIO_ARMED` or the foundation values outside the team. They hold no secrets, but they identify the deployment.
