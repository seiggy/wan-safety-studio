# Configuration guide

This is the reference for every setting the server reads. The server is a standard ASP.NET Core app: settings are .NET options bound from configuration sections (`Studio`, `Auth`, `AzureMl`, `Storage`, ...). There is no JSON-in-an-environment-variable setting, no Terraform receipt and no `WAN_STUDIO_*` setting the server reads.

Where a value lives depends on who changes it and when:

| Kind of value | Where it lives | Changed by |
| --- | --- | --- |
| Topology, identities and resource IDs, policy ceilings, network landing-zone IDs, tags, sign-in, providers, features | Environment variables (in Azure App Service: application settings) | An operator, with a restart |
| Limits administrators change while the app runs: global concurrent jobs, per-region concurrent jobs, per-region job timeout, per-region priority, queue-wait-before-moving minutes, release selection, Start and Stop | The **Administration** page (role `StudioAdmin`), stored in `operations/settings.json` in the control region's `wan-control` container | An administrator, no restart |

The GUI values are always bounded by the deployed ceilings in `Studio:Regions:<region>:Compute` (nodes, job minutes, at most 120). The ceilings themselves are not editable in the GUI.

Terraform only creates infrastructure. It emits no machine-readable contract and sets no `Studio` setting: the operator copies resource IDs from the informational Terraform outputs or from `az` into the settings below. Real Azure behavior of the hosted configuration has not been verified live.

## Configure the app in Azure App Service

**Where.** Azure portal, the web app, **Settings > Environment variables > App settings**; or the CLI:

```powershell
az webapp config appsettings set -g <resource-group> -n <site-name> --settings "Studio__DeploymentName=wan-demo" "Studio__ControlRegion=eastus2"
az webapp config appsettings set -g <resource-group> -n <site-name> --settings "@settings.json"   # many at once; see the runbook
az webapp config appsettings list -g <resource-group> -n <site-name> -o table
```

The file form is a JSON array of `{ "name": ..., "value": ..., "slotSetting": false }`. The complete procedure, including turning the nested sample into that array, is in [app-service-settings.md](operations/app-service-settings.md). For a container-based site see also [app-service-docker.md](app-service-docker.md).

**Naming rule.** The App Service application-setting name is the configuration key with `:` replaced by `__`. App Service passes each setting to the process as an environment variable, and the .NET configuration system reads both forms identically (tests cover that the flat form binds the same as the nested form).

| Rule | Configuration key | Application setting |
| --- | --- | --- |
| Section and property | `Studio:ControlRegion` | `Studio__ControlRegion` |
| Dictionary entry: the key is a path segment | `Studio:Regions:eastus2:Compute:MaxNodes` | `Studio__Regions__eastus2__Compute__MaxNodes` |
| Nested dictionary | `Studio:Regions:eastus2:Network:PublicIpTags:<key>` | `Studio__Regions__eastus2__Network__PublicIpTags__<key>` |
| Tag dictionary | `Studio:Regions:eastus2:OwnershipTags:managedBy` | `Studio__Regions__eastus2__OwnershipTags__managedBy` |
| Array: the index is a path segment | `Studio:Regions:eastus2:Network:PrivateConnectivityHosts:0` | `Studio__Regions__eastus2__Network__PrivateConnectivityHosts__0` |
| Boolean / number | `Studio:Regions:eastus2:Compute:ManageEgress` | value `true` / `1`; written as plain text |

Names are case-insensitive to .NET; keep the casing shown here. `eastus2` and `westeurope` are example region keys only: use your own regions.

**Secrets.** No `Studio` value is a secret (resource IDs, GUIDs and host names only). The only secret the server can read for sign-in is `Auth__ClientSecret`, needed only for a local, non-federated sign-in; on App Service the site's managed identity signs a federated assertion (`AZURE_CLIENT_ID`) and no secret exists. If you need a secret, keep it in Key Vault (`KeyVault__Uri`, below) or an App Service Key Vault reference, never in the repository or the sample.

**Restart.** Saving settings in the portal restarts the site; `az webapp config appsettings set` also restarts it. Options are read at start, so a change takes effect after the restart. Live limits edited in the GUI need no restart.

**How a bad value is reported.** The host never stops for bad configuration. It starts, evaluates every setting once at start, and if a required setting is missing or invalid it runs no background workers, answers every `/api` and `/auth` route with `503` (`The studio configuration is incomplete. Open /health-ui`), and serves only the anonymous [configuration health page](#configuration-health-page) and its JSON. Open `https://<site>/health-ui`, fix the named settings, and let the site restart (saving App Service settings restarts it); then reload the page.

**Live limits.** Concurrency, per-region timeout, priority and queue wait are edited in the Administration page, not here. See [hosted lifecycle](operations/hosted-lifecycle.md#administration-p7).

**Hosted mode.** Hosted multi-region operation is on exactly when `Studio:Regions` has at least one entry. Then the host validates the whole `Studio` section, uses the regional backend and the durable per-region fence, enables the `/api/studio/*` routes, and ignores `AzureMl__Armed` and the flat `AzureMl` resource settings for submissions. With `Studio:Regions` empty none of that happens (local and single-workspace mode).

## Configuration health page

When any required setting is missing or invalid, the host still starts but runs no background workers and answers every `/api` and `/auth` route with `503`. It serves two anonymous endpoints:

- `/health-ui`, a page listing what to fix.
- `GET /api/health/config`, the same as JSON: `{ "status": "ready" | "ready-with-warnings" | "misconfigured", "mode": "hosted-multi-region" | "single-workspace" | "local-comfyui", "issues": [{ "setting", "severity": "error" | "warning", "problem", "example", "where" }] }`.

Each issue gives the exact App Service setting name (the `__` form, for example `Studio__Regions__<region>__Workspace__Id`), what is wrong, a made-up static example, and where in the Azure portal to find the real value. The page never shows or reveals a configured value or a secret. The checks cover `Generation:Provider`, `Auth` (mode, client ID, public origin, tenant, credential), the whole `Studio` section (unknown or misspelled keys, IDs, formats and cross-setting rules) and, when no `Studio` regions exist, the single-workspace `AzureMl` settings.

Settings are read at start: after saving App Service settings the site restarts, then reload the page. `/healthz` and `/health` are unchanged liveness probes and do not report configuration; the App Service health check path can stay `/healthz`.

## How values are resolved

Later sources win:

1. `appsettings.json` (defaults, below), then `appsettings.<Environment>.json`.
2. Environment variables (`:` becomes `__`). App Service application settings are environment variables.
3. Azure Key Vault, only when `KeyVault:Uri` is set. A secret name uses `--` for `:` (`Auth--ClientSecret` is `Auth:ClientSecret`).

Under the Aspire AppHost, `AzureMl:*`, `Auth:*` and `KeyVault:Uri` set in the AppHost configuration are passed to the server as the flat `AzureMl__*`, `Auth__*` and `KeyVault__Uri` variables. Restart the AppHost after changing them.

## Studio (hosted multi-region topology)

Section `Studio`. Every region listed under `Regions` is one Azure ML spoke the host may manage; the host acts only on the exact resource IDs given here. Every resource ID must be in `Studio:SubscriptionId`. A complete two-region example in appsettings shape is [samples/studio-settings.json.example](samples/studio-settings.json.example). Environment names below use `<r>` for the region key (for example `eastus2`).

### Scope and control region

| Setting | Environment name | Required | Default | Meaning |
| --- | --- | --- | --- | --- |
| `Studio:TenantId` | `Studio__TenantId` | Yes, with `Regions` | none | Entra tenant GUID of the deployment. |
| `Studio:SubscriptionId` | `Studio__SubscriptionId` | Yes, with `Regions` | none | Subscription GUID. Every resource ID must be in it. |
| `Studio:DeploymentName` | `Studio__DeploymentName` | Yes, with `Regions` | none | Short lowercase name: letters, digits, single hyphens (no `--`). Identifies the deployment in tags and audit records. Example `wan-demo`. |
| `Studio:ControlRegion` | `Studio__ControlRegion` | Yes, with `Regions` | none | Region key whose storage holds operational state, settings, audit and job records. Must be one of the `Regions` keys. |
| `Studio:Regions:<r>` | `Studio__Regions__<r>__...` | Yes (at least one for hosted mode) | empty | One entry per region. The key is lowercase letters and digits. Regions must not share a workspace or a GPU subnet. |

"Required, with `Regions`" means: not needed while `Regions` is empty, required once it has entries.

### Per region

Settings under `Studio:Regions:<r>:`; every row below is required unless a default is shown.

| Setting | Environment name suffix | Required / default | Meaning |
| --- | --- | --- | --- |
| `Workspace:Id` | `__Workspace__Id` | Required | ARM ID of the Azure ML workspace (`.../providers/Microsoft.MachineLearningServices/workspaces/<name>`). |
| `Compute:Id` | `__Compute__Id` | Required | `<workspace id>/computes/<name>`. The cluster is created at Start and removed at Stop; the ID is where it will be. Jobs for any other compute are refused. |
| `Compute:IdentityId` | `__Compute__IdentityId` | Required | ARM ID of the user-assigned identity jobs run as. Jobs run as this identity. |
| `Compute:IdentityClientId` | `__Compute__IdentityClientId` | Required | Client ID (GUID) of that identity. |
| `Compute:MaxNodes` | `__Compute__MaxNodes` | Default `1`; at least 1 | Ceiling on cluster nodes. The GUI per-region concurrency cannot exceed it. |
| `Compute:MaxJobMinutes` | `__Compute__MaxJobMinutes` | Default `120`; 1 to 120 | Ceiling on the job timeout. The GUI per-region timeout cannot exceed it. |
| `Compute:MaxPaygHourlyUsd` | `__Compute__MaxPaygHourlyUsd` | Default `4`; above 0 | Safety threshold for the exact Linux pay-as-you-go hourly price checked at Start. Not a billing cap. |
| `Compute:ManageEgress` | `__Compute__ManageEgress` | Default `false` | `true` when the studio creates and removes its own NAT gateway and public IP for the compute. Then `Network:NatGatewayId` and `Network:PublicIpId` are required. |
| `Storage:AccountId` | `__Storage__AccountId` | Required | ARM ID of the region's private storage account (containers `wan-studio`, `wan-release`, `wan-control`). |
| `Platform:RegistryId` | `__Platform__RegistryId` | Required | ARM ID of the container registry. The host accepts only release and trainer images from this registry; an image from any other registry is refused. |
| `Network:GpuSubnetId` | `__Network__GpuSubnetId` | Required | ARM ID of the dedicated GPU subnet (`.../virtualNetworks/<vnet>/subnets/<name>`). Not shared with another workload. |
| `Network:GpuSubnetCidr` | `__Network__GpuSubnetCidr` | Required | Canonical IPv4 network of that subnet, for example `10.42.1.0/26`. |
| `Network:GpuNsgId` | `__Network__GpuNsgId` | Required | ARM ID of the GPU subnet's network security group. |
| `Network:CustomerManagedNsg` | `__Network__CustomerManagedNsg` | Default `false` | `true` when that NSG belongs to the customer. Start then needs the security owner's explicit acknowledgment of the NSG rules before it proceeds. |
| `Network:NatGatewayId` | `__Network__NatGatewayId` | Only with `ManageEgress` | ARM ID of the NAT gateway the studio attaches to the GPU subnet. |
| `Network:PublicIpId` | `__Network__PublicIpId` | Only with `ManageEgress` | ARM ID of the public IP the NAT uses. |
| `Network:PublicIpTags:<key>` | `__Network__PublicIpTags__<key>` | Default empty | `ipTags` applied to the studio's egress public IP, only when `Compute:ManageEgress` is `true` (the studio then creates that IP). Some customers' policies require them. Non-empty keys and values. |
| `Network:PrivateConnectivityHosts:<n>` | `__Network__PrivateConnectivityHosts__<n>` | Required: at least five | The Start private-connectivity check uses these. Lowercase host names Start must see resolve privately and answer on TCP 443: storage blob and file, key vault, registry (and its regional data endpoint), the Azure ML API and notebooks hosts. Indexes start at 0 and are contiguous. |
| `OwnershipTags:<key>` | `__OwnershipTags__<key>` | Default `application=wan-safety-studio`, `deployment=<DeploymentName>`, `managedBy=terraform`, `region=<r>` | The proof of ownership; see [Ownership tags](#ownership-tags-a-safety-measure). If you set any, set them all: the set must be non-empty and include `managedBy`. |

Examples: `Studio__Regions__eastus2__Compute__MaxNodes=2`, `Studio__Regions__eastus2__Network__PrivateConnectivityHosts__0=<storage>.blob.core.windows.net`, `Studio__Regions__eastus2__Network__PublicIpTags__<key>=<value>`.

#### Ownership tags: a safety measure

`OwnershipTags` is the studio's proof of ownership and is a vital safety measure. Before the studio creates, changes or deletes a compute, a NAT gateway or a public IP, or deletes anything at Stop, it requires the resource to carry exactly these tags. That way it can never modify or delete a customer's resource that happens to have a similar name or ID.

Keep the Terraform tags and these settings identical. If you change the tags after resources exist, those resources look foreign to the studio and Stop and Start refuse them.

Fixed, not configurable: the GPU SKU, Spot (low-priority) priority, zero minimum nodes, 120-second idle time and one instance per node. An absolute 120-minute job limit applies everywhere.

### Not read by the web host: `Pipeline`

The deployment pipelines read a separate `Pipeline` section from their own settings document. It is not an App Service setting and the web host never reads it. Each value, and how to find it, is described in [private pipelines](operations/private-pipelines.md#pipeline-settings).

### Administration (GUI-managed, not environment)

Edited by a `StudioAdmin` in the Administration page and stored in `operations/settings.json`; always within the ceilings above. Defaults before anyone saves: per-region concurrency = `Compute:MaxNodes`, per-region timeout = `Compute:MaxJobMinutes`, global = the sum of the regions, queue wait 10 minutes, control region priority 0 and others after it.

| GUI setting | Bounded by |
| --- | --- |
| Global concurrent jobs | Sum of the regional concurrency |
| Per-region concurrent jobs | `Compute:MaxNodes` |
| Per-region job timeout | `Compute:MaxJobMinutes` (at most 120) |
| Per-region priority | Non-negative integer; lower runs first |
| Minutes a queued job waits before moving to another region | Positive integer |
| Release selection, Start, Stop | Region must be Off to select a release; Start needs explicit spend approval |

## Auth

Section `Auth`; environment names `Auth__<Name>`. Values are read at start. A missing `ClientId`, `PublicOrigin`, tenant or client credential (`AZURE_CLIENT_ID` or `Auth:ClientSecret`) in `Entra` mode is reported on the [health page](#configuration-health-page).

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `Auth:Mode` | `Auth__Mode` | `None` in Development, `Entra` otherwise | `None`: no sign-in, one fixed local identity. `Entra`: Entra sign-in, the `VideoCreator` role check and CSRF. An explicit value always wins; `None` outside Development is allowed (behind a gateway that authenticates) and logs a warning. Any other value is reported on the health page. |
| `Auth:ClientId` | `Auth__ClientId` | empty (appsettings) | Entra app registration client ID. Required for `Entra`. |
| `Auth:TenantId` | `Auth__TenantId` | `Studio:TenantId`, then `AzureMl:TenantId` | Entra tenant. Required for `Entra` unless one of those is set, so a hosted deployment normally needs no separate value. |
| `Auth:PublicOrigin` | `Auth__PublicOrigin` | empty (appsettings) | The site's public `https://host` origin (or a loopback `http` origin). Required for `Entra`; builds the redirect URI and pins the host and CSRF origin. |
| `Auth:KeysPath` | `Auth__KeysPath` | `/home/.wan-studio/keys` on App Service, otherwise the framework default | Where the data-protection keys persist so sessions survive a restart (Entra mode). |
| `Auth:ClientSecret` | `Auth__ClientSecret` | unset | Optional local fallback when there is no managed identity. A secret: keep it in Key Vault (`Auth--ClientSecret`). |
| `Auth:AllowedHosts` | `Auth__AllowedHosts` | unset | Comma-separated host names accepted with `Auth:Mode=None` (loopback names are always accepted in Development). Unset outside Development accepts any host, for a gateway. |
| `AZURE_CLIENT_ID` (Azure SDK) | `AZURE_CLIENT_ID` | unset | Client ID of the user-assigned managed identity the site runs as. When set it is the credential for Azure SDK calls (ARM, blob, Key Vault) and signs the federated assertion for Entra sign-in. When unset the default credential chain is used (on a developer machine, your `az login`). |
| `AZURE_TOKEN_CREDENTIALS` (Azure SDK) | same | unset | Narrows the default chain, for example `AzureCliCredential`. |

`Entra` needs `Auth:ClientId`, `Auth:PublicOrigin`, a tenant, and a client credential (`AZURE_CLIENT_ID` or `Auth:ClientSecret`); anything missing is listed on the health page with the setting names. Roles are `VideoCreator` and `StudioAdmin` ([Entra bootstrap](operations/entra-bootstrap.md)).

## Generation and AzureMl (single-workspace / local mode)

`Generation:Provider` (`Generation__Provider`) is `azureml` (default) or `comfyui`; any other value is reported on the health page. The AppHost sets `comfyui` for local development.

The flat `AzureMl` section below is the **single-workspace and local mode**: one workspace, one compute, no regional routing, no Administration page, `AzureMl:Armed` as the arming switch. **It is being replaced by `Studio:Regions` in a follow-up.** In a hosted deployment with `Studio:Regions` entries these resource settings are not used for submissions; do not set them there. They are kept for local development with `Generation__Provider=azureml` and for legacy hosts.

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `AzureMl:SubscriptionId` | `AzureMl__SubscriptionId` | none | Subscription (canonical GUID). Required for this mode. |
| `AzureMl:TenantId` | `AzureMl__TenantId` | none | Tenant (canonical GUID). Required. |
| `AzureMl:ResourceGroup` | `AzureMl__ResourceGroup` | none | Resource group name. Required. |
| `AzureMl:Workspace` | `AzureMl__Workspace` | none | Workspace name. Required. |
| `AzureMl:Compute` | `AzureMl__Compute` | none | GPU compute name; jobs for any other compute are refused. Required. |
| `AzureMl:ComputeIdentityClientId` | `AzureMl__ComputeIdentityClientId` | none | Client ID of the identity the job runs as. Required. |
| `AzureMl:StorageAccount` | `AzureMl__StorageAccount` | none | Studio storage account; also the object store for videos and inputs. Required. |
| `AzureMl:Container` | `AzureMl__Container` | none | Studio container. Required. |
| `AzureMl:Datastore` | `AzureMl__Datastore` | `workspaceblobstore` | Datastore over that container; run data lives under it. |
| `AzureMl:ReleaseContainer` | `AzureMl__ReleaseContainer` | none | Separate container for prepared code, models and licences; must differ from `Container`. Required. |
| `AzureMl:ReleaseDatastore` | `AzureMl__ReleaseDatastore` | none | Datastore over the release container; must differ from `Datastore`. Required. |
| `AzureMl:Release:EnvironmentId` | `AzureMl__Release__EnvironmentId` | empty | `azureml:<name>:<version>` or the workspace's environment version ID. Empty means not prepared: generation is refused. |
| `AzureMl:Release:ModelsRef` | `AzureMl__Release__ModelsRef` | `azureml://datastores/{ReleaseDatastore}/paths/models/wan/` | Models location: a datastore path or an `azureml:<name>:<version>` asset. |
| `AzureMl:Release:CodeUri` | `AzureMl__Release__CodeUri` | `azureml://datastores/{ReleaseDatastore}/paths/code/{version}-wan/` | Job code location; `{version}` is the last segment of `EnvironmentId`. |
| `AzureMl:Armed` | `AzureMl__Armed` | `false` | Local arming switch, read on every submit. **Ignored when `Studio:Regions` has entries**: the durable fence is then the only gate ([hosted lifecycle](operations/hosted-lifecycle.md#durable-fence)). |
| `AzureMl:PollSeconds` | `AzureMl__PollSeconds` | `5` | Job status polling interval. |
| `AzureMl:MaxConcurrency` | `AzureMl__MaxConcurrency` | `25` | Jobs submitted at once; Azure ML queues them. |
| `AzureMl:MaxJobMinutes` | `AzureMl__MaxJobMinutes` | `120` | Job timeout, 1 to 120. |
| `AzureMl:MaxUploadMb` | `AzureMl__MaxUploadMb` | `64` | Total size of the reference images of one submit. |

A missing or malformed required value stops the Azure ML provider with the key's name in the error. The compute resource ID is derived from these values, never configured.

## Storage and ComfyUi (local provider)

Used by the `comfyui` provider and local object storage; see [local-generation.md](local-generation.md). With `Studio:Regions` configured the object store is the control region's storage account and `Storage__*` is not used.

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `Storage:Backend` | `Storage__Backend` | `local` | `local` (folder or mounted share), `azurite` or `azure`. |
| `Storage:Path` | `Storage__Path` | unset | Folder for `local`; unset uses a `.dev-data` folder under the content root. |
| `Storage:Account` | `Storage__Account` | unset | Storage account name for `azure`. |
| `Storage:Container` | `Storage__Container` | `studio` | Container name. |
| `Storage:Connection` | `Storage__Connection` | unset | Connection string (a secret; for `azurite`). |
| `Storage:CreateContainer` | `Storage__CreateContainer` | `true` | `false` when someone else provisioned the container. |
| `ComfyUi:Url` | `ComfyUi__Url` | `http://localhost:8188` | ComfyUI base URL. |
| `ComfyUi:Retrieval` | `ComfyUi__Retrieval` | `api` | `api` (download through ComfyUI `/view`) or `share` (read ComfyUI's output folder from a mounted path). |
| `ComfyUi:OutputDir` | `ComfyUi__OutputDir` | unset | Output folder for `share`. |
| `ComfyUi:WorkflowsDir` | `ComfyUi__WorkflowsDir` | unset | Workflow templates folder; unset uses the `Workflows` folder shipped with the server. |
| `ComfyUi:SmbUser`, `ComfyUi:SmbPassword` | `ComfyUi__SmbUser`, `ComfyUi__SmbPassword` | unset | Credentials for an SMB share (the password is a secret). |
| `ComfyUi:PollSeconds` | `ComfyUi__PollSeconds` | `3` | Poll interval. |
| `ComfyUi:TimeoutSeconds` | `ComfyUi__TimeoutSeconds` | `3600` | Per-job timeout. |
| `ComfyUi:MaxOpenJobs` | `ComfyUi__MaxOpenJobs` | `50` | Ceiling on unfinished jobs; a submit above it is refused. |
| `ComfyUi:JobRetentionHours` | `ComfyUi__JobRetentionHours` | `24` | Finished jobs older than this leave memory (records stay in storage). |
| `ComfyUi:UnetHigh`, `UnetLow`, `Vae`, `Clip`, `LoraHigh`, `LoraLow`, `Unet5B`, `Vae5B` | `ComfyUi__<Name>` | the Wan 2.2 file names in `GenerationOptions` | Model file names the workflows reference. |

## Training, Trainers and the dataset builder

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `Training:MaxRunHours` | `Training__MaxRunHours` | `12` | Longest a local training run may take; then the container is removed and the run is marked failed. Also stopped on server shutdown. |
| `Training:CleanupTimeoutSeconds` | `Training__CleanupTimeoutSeconds` | `30` | Total budget for salvaging failed-run artifacts and recording the terminal status (clamped 0.1 to 300). |
| `Trainers:BuiltIn` | `Trainers__BuiltIn` | `../../trainers` (appsettings) | Folder of built-in trainers (`<name>/trainer.json`). |
| `Trainers:Directories:<n>` | `Trainers__Directories__<n>` | empty | Extra folders of customer trainers. |
| `FEATURE_DATASET_BUILDER_ENABLED` | same | `false` | Shows the vision dataset builder and starts training recovery. Keep `false` on the hosted site unless you intend to use it ([vision trainers](vision-trainers.md)). |

## Agent (Video Production Helper)

Section `Agent`; see [video-helper.md](video-helper.md). The helper is disabled while `Agent:Endpoint` is empty.

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `Agent:Endpoint` | `Agent__Endpoint` | unset | OpenAI v1-compatible base URL. Empty disables the helper. |
| `Agent:Model` | `Agent__Model` | unset | Model or Foundry deployment name. |
| `Agent:Auth` | `Agent__Auth` | `ApiKey` if a key is set, otherwise `AzureCli` for an Azure endpoint | `ApiKey`, `AzureCli`, `SystemAssignedManagedIdentity` or `UserAssignedManagedIdentity`. `AzureCli` is for local development; on App Service use `UserAssignedManagedIdentity`. |
| `Agent:ApiKey` | `Agent__ApiKey` | unset | Secret, for `ApiKey` auth. Use a Key Vault reference (`@Microsoft.KeyVault(SecretUri=...)`) on App Service. |
| `Agent:ManagedIdentityClientId` | `Agent__ManagedIdentityClientId` | unset | Client ID of the user-assigned identity, for `UserAssignedManagedIdentity`. |
| `Agent:WireApi` | `Agent__WireApi` | `responses` for Azure endpoints, otherwise `completions` | `completions` or `responses`. |
| `Agent:Vision` | `Agent__Vision` | `true` | `false` for text-only models. |
| `Agent:StateDirectory` | `Agent__StateDirectory` | a folder under the user's local app data | Isolated state folder for the Copilot runtime. |

## KeyVault

| Setting | Environment name | Default | Meaning |
| --- | --- | --- | --- |
| `KeyVault:Uri` | `KeyVault__Uri` | unset | `https://<vault>.vault.azure.net/` (must be https). When set, Key Vault is added as the highest-priority source using the credential above; the identity needs the `Key Vault Secrets User` role. A secret name uses `--` for `:`. When unset the server never contacts Key Vault. |

## Platform and logging

| Setting | Environment name | Meaning |
| --- | --- | --- |
| `WEBSITES_PORT` | `WEBSITES_PORT` | App Service container port; the image listens on `8080`. |
| `ASPNETCORE_ENVIRONMENT` | same | `Production` on a hosted site (image default). Controls the `Auth:Mode` default. |
| `AllowedHosts` | `AllowedHosts` | ASP.NET host filtering; `*` in `appsettings.json`. |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | `Information` by default. |

## Overriding defaults

* **Environment variables** (App Service application settings, container `env`, your shell): `AzureMl__MaxUploadMb=128`.
* **Key Vault**: set `KeyVault__Uri` and store secrets as `Auth--ClientSecret`. Use it for secrets only.
* **appsettings**: `appsettings.Production.json` beside the server, or edit `appsettings.json`. Never put secrets in either.

## Operator tooling

`WAN_STUDIO_PYPI_INDEX`, `WAN_STUDIO_LOG_LEVEL`, `WAN_STUDIO_CACHE`, `WAN_STUDIO_UPSTREAM_TEST_ROOT` and `WAN_STUDIO_TRAINERS_DIR` belong to the operator scripts; the server reads none of them. The deployment pipelines read their own settings document (`STUDIO_SETTINGS_JSON` / `STUDIO_SETTINGS_PATH`, `STUDIO_TFVARS_*`, `STUDIO_BACKEND_*`), which the web host never reads; see [private pipelines](operations/private-pipelines.md#explicit-configuration-and-identities).
