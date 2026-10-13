# Configuration reference

The server is configured like any ASP.NET Core app. There is no JSON-in-an-environment-variable and no `WAN_STUDIO_*` setting.

**Principle.** Environment variables carry only *connections to resources* and *identity*. Everything else is behavior with a default in `src\VideoSafetyStudio.Server\appsettings.json`.

## How a value is resolved

Later sources win:

1. `appsettings.json` (defaults, below), then `appsettings.<Environment>.json`.
2. Environment variables: `:` becomes `__` (`AzureMl:Armed` is `AzureMl__Armed`; App Service app settings are environment variables).
3. Azure Key Vault, only when `KeyVault:Uri` is set. A secret name uses `--` for `:` (`Auth--ClientSecret` is `Auth:ClientSecret`, `AzureMl--Release--EnvironmentId` is `AzureMl:Release:EnvironmentId`).

Under the Aspire AppHost, `AzureMl:*`, `Auth:*` and `KeyVault:Uri` set in the AppHost configuration (its environment, `appsettings.Development.json` or user secrets) are passed to the server as the flat `AzureMl__*`, `Auth__*` and `KeyVault__Uri` variables. Restart the AppHost after changing them.

## Identity (environment)

| Key | Env var | Default | What it does |
|---|---|---|---|
| (standard Azure SDK) | `AZURE_CLIENT_ID` | unset | Client id of the user-assigned managed identity the site runs as. When set it is the credential for the Azure SDK calls (ML workspace, blob store, Key Vault) and signs the federated assertion for Entra sign-in. When unset the default chain is used, which on a developer machine is your `az login`. |
| (standard Azure SDK) | `AZURE_TOKEN_CREDENTIALS` | unset | Narrows the default chain, for example `AzureCliCredential` (the local operator Portal sets it). |
| `Auth:ClientId` | `Auth__ClientId` | empty | Entra app registration client id. Required for `Auth:Mode=Entra`. |
| `Auth:TenantId` | `Auth__TenantId` | `AzureMl:TenantId` | Entra tenant. |
| `Auth:ClientSecret` | `Auth__ClientSecret` | unset | Optional local fallback when there is no managed identity. Keep it in Key Vault (`Auth--ClientSecret`), never in a file. |
| `Agent:ManagedIdentityClientId` | `Agent__ManagedIdentityClientId` | unset | See [video-helper.md](video-helper.md). |

## Resources (environment or Key Vault)

All required when `Generation:Provider=azureml`; a missing or malformed value stops the Azure ML provider with the key's name in the error. The compute resource id is derived, never configured.

| Key | Env var | Default | What it does |
|---|---|---|---|
| `AzureMl:SubscriptionId` | `AzureMl__SubscriptionId` | none | Subscription (canonical UUID). |
| `AzureMl:TenantId` | `AzureMl__TenantId` | none | Tenant (canonical UUID). |
| `AzureMl:ResourceGroup` | `AzureMl__ResourceGroup` | none | Resource group name. |
| `AzureMl:Workspace` | `AzureMl__Workspace` | none | Azure ML workspace name. |
| `AzureMl:Compute` | `AzureMl__Compute` | none | GPU compute name. Jobs for any other compute are refused. |
| `AzureMl:ComputeIdentityClientId` | `AzureMl__ComputeIdentityClientId` | none | Client id (UUID) of the user-assigned identity the job runs as. |
| `AzureMl:StorageAccount` | `AzureMl__StorageAccount` | none | Studio storage account; also the object store for videos and inputs. |
| `AzureMl:Container` | `AzureMl__Container` | none | Studio container in that account. |
| `AzureMl:Datastore` | `AzureMl__Datastore` | `workspaceblobstore` | Datastore over that container; job inputs and outputs must live under it. |
| `AzureMl:Release:EnvironmentId` | `AzureMl__Release__EnvironmentId` | empty | `azureml:<name>:<version>` or the workspace's environment version id (set by Publish). Empty means not prepared: generation is refused. |
| `AzureMl:Release:ModelsRef` | `AzureMl__Release__ModelsRef` | `azureml://datastores/{Datastore}/paths/models/wan/` | Models location: a datastore path or an `azureml:<name>:<version>` asset. |
| `AzureMl:Release:CodeUri` | `AzureMl__Release__CodeUri` | `azureml://datastores/{Datastore}/paths/code/{version}-wan/` | Job code location (a datastore path). `{version}` is the last segment of `EnvironmentId`. |
| `KeyVault:Uri` | `KeyVault__Uri` | unset | `https://<vault>.vault.azure.net/`. When set, Key Vault is added as the highest-priority source using the credential above (the identity needs the `Key Vault Secrets User` role). When unset the server never contacts Key Vault. |
| `Agent:Endpoint`, `Agent:Model`, ... | `Agent__*` | unset | Video helper; see [video-helper.md](video-helper.md). |
| `Storage:*`, `ComfyUi:*` | `Storage__*`, `ComfyUi__*` | see `appsettings.json` | The `comfyui` provider only; see [local-generation.md](local-generation.md). |

## Behavior (defaults in appsettings.json)

| Key | Env var | Default | What it does |
|---|---|---|---|
| `Generation:Provider` | `Generation__Provider` | `azureml` | `azureml` or `comfyui`. The AppHost sets `comfyui` for local development. |
| `AzureMl:Armed` | `AzureMl__Armed` | `false` | Whether jobs may be submitted. Read on every submit through `IOptionsMonitor`, so a configuration reload applies at once and Stop halts a running batch. Start sets it `true`, Stop `false` (an App Service setting change restarts the site). |
| `AzureMl:PollSeconds` | `AzureMl__PollSeconds` | `5` | Job status polling interval. |
| `AzureMl:MaxConcurrency` | `AzureMl__MaxConcurrency` | `25` | Jobs submitted at once; Azure ML queues them on the single node. |
| `AzureMl:MaxJobMinutes` | `AzureMl__MaxJobMinutes` | `120` | Job timeout in minutes, 1 to 120 (the safety guard rejects more). |
| `AzureMl:MaxUploadMb` | `AzureMl__MaxUploadMb` | `64` | Total size of the reference images of one submit. |
| `Training:MaxRunHours` | `Training__MaxRunHours` | `12` | Longest a local training run may take; after that the container is removed and the run is marked failed. Also stopped on server shutdown. |
| `Auth:Mode` | `Auth__Mode` | `None` in Development, `Entra` otherwise | `None`: no sign-in, one fixed local identity. `Entra`: Entra sign-in, `VideoCreator` role check and CSRF. An explicit value always wins; `None` outside Development is allowed (for a gateway that authenticates) and logs a warning at start. |
| `Auth:PublicOrigin` | `Auth__PublicOrigin` | empty | The site's public `https://host` origin; required for `Entra` (builds the redirect URI and pins the host and CSRF origin). Set by Publish, since Terraform cannot know the site's own hostname. |
| `Auth:KeysPath` | `Auth__KeysPath` | `/home/.wan-studio/keys` on App Service | Where data-protection keys persist so sessions survive restarts. |
| `FEATURE_DATASET_BUILDER_ENABLED` | same | `false` | Shows the vision dataset builder. |

`Entra` needs `Auth:ClientId`, `Auth:PublicOrigin`, a tenant (`Auth:TenantId` or `AzureMl:TenantId`) and a client credential (`AZURE_CLIENT_ID` or `Auth:ClientSecret`); anything missing stops the site at start.

## Overriding defaults

* **Environment variables** (App Service app settings, container `env`, your shell): `AzureMl__MaxUploadMb=128`.
* **Key Vault**: set `KeyVault__Uri` and store secrets as `AzureMl--MaxUploadMb`. Use it for anything secret (`Auth--ClientSecret`); resources and behavior rarely need it.
* **appsettings**: add `appsettings.Production.json` beside the server, or edit `appsettings.json`. Never put secrets in either.

## Operator tooling

`WAN_STUDIO_PYPI_INDEX`, `WAN_STUDIO_LOG_LEVEL`, `WAN_STUDIO_CACHE`, `WAN_STUDIO_UPSTREAM_TEST_ROOT` and `WAN_STUDIO_TRAINERS_DIR` belong to the operator scripts; the server reads none of them.
