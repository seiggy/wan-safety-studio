# WAN Safety Studio

A portable, private Azure ML WAN/ComfyUI baseline for an **existing customer landing zone**, provisioned with Terraform. Creators use an Entra-protected studio: the .NET server in a [private App Service](docs/app-service.md) reachable only through a private endpoint, or the server run locally under Aspire for development ([local use](docs/local-dashboard.md)). Do not run the upstream public deployment.

The GPU application is pinned to MIT-licensed [`jakeatmsft/azureml_vidgen_comfyui@dc0d29031b73a5ba7376d910adb7a58c206f32b1`](https://github.com/jakeatmsft/azureml_vidgen_comfyui/tree/dc0d29031b73a5ba7376d910adb7a58c206f32b1).
This repository contains the .NET server, the GPU image build, the safety patch, the model manifest and Terraform, not the upstream checkout, images or weights.

**New CSA / customer setup:** Collect approvals, landing-zone details and Entra prerequisites using the [customer onboarding guide](docs/customer-onboarding.md), then use the [regional schema and migration guide](infra/README.md).

**Status.** The refactor into the three boundaries below is implemented and tested offline. No workflow has been dispatched, no hosted lifecycle route has been invoked against Azure, and no customer state, config or running Azure resource has been migrated. Everything that involves real Azure is unverified live.

## Execution boundaries

| Boundary | Owns | Where documented |
| --- | --- | --- |
| .NET App Service | Hosted status, release selection, Start/Stop, regional generation routing, the Administration page (`/api/studio/*`), the guards on job submission (`AzureMlGuard`) | [Hosted lifecycle](docs/operations/hosted-lifecycle.md), [App Service](docs/app-service.md) |
| GitHub Actions | Build, regional promotion, foundation plan/apply, digest-only server deployment, emergency recovery | [Private pipelines](docs/operations/private-pipelines.md) |
| Python/ComfyUI containers run by Azure ML jobs | Generation only: the job runs the upstream ComfyUI runner | [build/gpu](build/gpu/README.md) |

There is no Python control plane or local Python portal. The earlier PowerShell lifecycle (`Deploy`, `Prepare`, `Portal`, `Publish`, `Start`), `Initialize-PortalAuth.ps1`, `Initialize-LocalDev.ps1`, the git-hook and docs scripts, the `upload-wan-*.sh` copy scripts and the whole `azureml\` folder were removed. `scripts\Invoke-WanSafetyStudio.ps1` remains as a frozen legacy tool that accepts only `-Action Status`, `Stop` and `Check`, solely to release a pre-migration (v1) deployment; it is deleted after migration is verified.

## What is provisioned

```text
Creator browser -- existing private connectivity/DNS -- customer PE subnet
                                                           |
Shared App Service (control_region, optional)              +-- Blob / File
  .NET server, Entra sign-in, Administration page          +-- Key Vault
  hosted lifecycle (Start/Stop, release selection)         +-- Premium ACR
                                                           +-- Azure ML API / notebooks
Customer dedicated GPU subnet <---------------------------- Azure ML workspace (one per region)
  owned or customer-managed NSG                            workspace identity
  optional owned NAT + public egress IP (created by Start) compute identity
  one private Spot A100 node, only after Start             App Insights -> supplied Log Analytics
```

For each configured region, Terraform creates an owned resource group, private storage, an RBAC Key Vault, Premium ACR, App Insights, two managed identities, scoped roles and private endpoints. It creates an owned GPU NSG only when no already-attached customer NSG is selected. One optional shared private App Service remains in `control_region`; its identity has run-data access, release reads and explicit scoped operational grants. **Terraform does not own compute/egress, customer VNets/subnets, DNS zones/links/resolver, VPN or Log Analytics.** The control container is inaccessible to GPU identities.

Only Azure public cloud, one subscription and the exact `Standard_NC24ads_A100_v4` SKU are supported. There is no larger-SKU, Dedicated, public-endpoint or alternate-identity fallback.

## Prerequisites

- For Terraform and workflow work: Terraform **1.9+ (below 2.0)** (the foundation workflow pins 1.16.6), PowerShell **7.4+**, Azure CLI, Git, .NET 10 SDK, Python 3.12. The dependency lock pins AzureRM 5.6.0 and AzAPI 2.12.0. The GPU build runs on an ephemeral Linux x64 runner with Docker/Buildx and substantial disk (the eight pinned model files alone are about **41.6 GiB**), and needs model-license access where required ([private pipelines](docs/operations/private-pipelines.md)).
- Normal `az login --tenant <tenant-id>` and `az account set --subscription <subscription-id>` for manual work. Tools refuse a tenant/subscription mismatch rather than switching accounts. Workflows use federated CLI sessions; Terraform child processes cannot inherit service-principal secrets, managed-identity/OIDC switches or `TF_VAR_*` overrides.
- A **dedicated GPU subnet** and a distinct private endpoint subnet per region. Supply the exact GPU CIDR and explicitly acknowledge dedication. The GPU subnet must have `defaultOutboundAccess=false`, no service delegation and no unrelated NAT association. An existing NSG must be explicitly selected with `existing_gpu_nsg_id`; otherwise only an absent or accelerator-owned NSG is accepted. Follow the [customer NSG handoff](docs/customer-nsg.md) when policy manages the NSG. Existing route tables and other subnet properties are preserved.
- Existing private DNS zones for `privatelink.blob.core.windows.net`, `privatelink.file.core.windows.net`, `privatelink.vaultcore.azure.net`, `privatelink.azurecr.io`, `privatelink.api.azureml.ms` and `privatelink.notebooks.azure.net`, linked/resolved by your landing zone. Supply their IDs; this solution will not create zones or configure DNS.
- An existing Log Analytics workspace. The deployment identity needs permissions to create the owned resources/role assignments, join the supplied subnets, and associate private endpoints with the supplied zones.
- Private connectivity from the build/deployment runner and the hosted site to Blob/File, Vault, ACR and AML endpoints. GPU outbound access must satisfy [Azure ML network requirements](https://learn.microsoft.com/azure/machine-learning/how-to-secure-training-vnet?view=azureml-api-2). Use customer-controlled routing/firewall egress, or explicitly opt into this deployment's temporary NAT. NAT provides outbound connectivity, **not destination filtering**.
- AML LowPriority quota for 24 vCPUs, A100 family quota and regional Spot capacity. An unrestricted SKU and quota do not guarantee capacity.
- Entra sign-in work done once by an identity owner: [Entra bootstrap](docs/operations/entra-bootstrap.md).

Resource-provider registration is deliberately disabled. The customer must already have `Microsoft.Network`, `Microsoft.ManagedIdentity`, `Microsoft.Storage`, `Microsoft.KeyVault`, `Microsoft.ContainerRegistry`, `Microsoft.Insights` and `Microsoft.MachineLearningServices` registered; the supplied logging workspace also requires `Microsoft.OperationalInsights`, and the optional App Service host requires `Microsoft.Web`.

Local validation was performed on Windows only; Linux/macOS behavior and end-to-end operation are unverified.

## Configure once

The Terraform schema uses global scope, `control_region`, optional `legacy_region`, and a **`regions` map**. Each region supplies its own landing zone and runtime policy. Start from `infra/terraform.tfvars.json.example`; the IDs and region there are synthetic examples, not a region requirement. See [infra/README.md](infra/README.md) before initialization or any real plan.

**Configuring the studio.** Terraform only creates infrastructure: it emits no machine contract and sets no studio setting. The server is configured through .NET options (`Studio`, `Auth`, ...) supplied as environment variables, which on Azure App Service are application settings; the operator copies resource IDs from the informational Terraform outputs. Limits administrators change while it runs (concurrency, job timeout, priority, queue wait) are edited in the Administration page. Start with the [configuration guide](docs/configuration.md#configure-the-app-in-azure-app-service) and the [App Service settings runbook](docs/operations/app-service-settings.md); a complete example is [docs/samples/studio-settings.json.example](docs/samples/studio-settings.json.example).

Optional features (the video helper, the dataset builder flag, Key Vault) are application settings (`Agent__*`, `FEATURE_DATASET_BUILDER_ENABLED`, `KeyVault__Uri`); see [app-service.md](docs/app-service.md#optional-video-helper-and-dataset-builder). To host the server on an App Service your own automation creates, use the [container guide](docs/app-service-docker.md).

## Lifecycle

Each step is a manually dispatched, protected-environment workflow or a hosted action. Approvals and configuration are in [private pipelines](docs/operations/private-pipelines.md); none has been run live.

| Step | How |
| --- | --- |
| Foundation (Terraform) | `foundation.yml`: a plan job saves the plan, then a separately approved apply job applies that exact saved plan. Legacy `Deploy` is retired and refused against this root. |
| GPU release | `release.yml` (component `gpu`): build-only GPU image plus verified model/licence snapshot, sealed immutable artifact. Then `promote.yml` per region, which writes the create-only regional binding last. Large model files are staged by the build, not uploaded by shell scripts. |
| Server image and deployment | `release.yml` (component `server`), then `deploy-server.yml`: digest-only deployment that neither builds nor arms. |
| Sign-in | One-time manual [Entra bootstrap](docs/operations/entra-bootstrap.md) by an identity owner. |
| Start / Stop / Status / release selection | Hosted lifecycle: Administration page and `/api/studio/*` routes, `StudioAdmin` role ([hosted lifecycle](docs/operations/hosted-lifecycle.md)). Start needs explicit spend approval (and the customer-NSG acknowledgment when an existing NSG is used). Stop works without Python, Docker or Terraform. |
| Emergency release | `scripts\recovery\Stop-Studio.ps1`, or the `recover.yml` workflow; independent of the host. For an un-migrated v1 deployment only: `scripts\Invoke-WanSafetyStudio.ps1 -Action Stop` or `-Action Status`. |

A region's Start creates a **LowPriority cluster with min=0/max=1, 120-second idle scale-down, no node public IP and no public SSH**, and arms generation through the durable fence. Jobs allocate the node; Start is not GPU warmup. Creators choose 1–5 scenes, an aspect ratio, a 5–10 second duration and 1, 3 or 5 videos per scene; each video is one GPU job ([creating videos](docs/local-dashboard.md#5-create-videos)).

The server submits each job to Azure ML with exactly one instance and a **server-side timeout of the region's `max_job_minutes`** (policy maximum 120 minutes, configurable per region within that). It reads the job back to verify compute, instance count and timeout; missing or mismatched controls cancel the job with an error, never a resubmission.

## Cost and emergency release

For current infrastructure configuration, address migration and the legacy emergency release boundary, use the [regional foundation guide](infra/README.md).

**The PAYG threshold is a Start-time safety check, not a billing cap, maximum Spot bid or global time budget.** Spot price varies. The per-job limit applies per job; multiple jobs, queues, provisioning and deallocation can add charges. No auto-resubmission is performed.

The guard matches the exact A100 SKU/region and Virtual Machines service, accepting both the legacy `Virtual Machines NCads A100 v4 Series` and current `NCads A100 v4 Series Linux` product names. Windows, Spot/Low Priority meters, unknown OS names and ambiguous results remain rejected. A catalog label change is not a reason to raise the configured price ceiling.

**Stop does not make the whole solution free.** Premium ACR has a standing charge (often around $50/month, region/pricing dependent); private endpoints, storage/models/videos, App Insights/Log Analytics ingestion and retained customer networking can also incur charges. Owned NAT/PIP exist until Stop verifies their removal.

Stop and the independent recovery path use Azure CLI or managed-identity authentication with native ARM calls and the `Studio` settings document (`scripts\recovery\Stop-Studio.ps1 -SettingsPath`). They do not need Python, Docker, prepared models, a working pricing endpoint or a Terraform plan. They cancel jobs targeting only the recorded compute, delete the compute with `underlyingResourceAction=Delete`, detach only the studio's own NAT from the exact dedicated subnet with an ETag-guarded update, delete the owned NAT/PIP and require fresh reads proving absence and terminal jobs. Repeated Stop is safe; an unverified release stays an error. Details: [hosted lifecycle](docs/operations/hosted-lifecycle.md#stop-and-release-semantics) and [private pipelines](docs/operations/private-pipelines.md#emergency-release). Keep the settings document outside Git: recovery acts only on the exact resource IDs it lists and refuses to guess names.

## Terraform state and removal

Protect and back up Terraform state outside git. Before collaborative/customer operation use an approved encrypted remote backend with locking and Entra authentication; the foundation workflow expects one ([private pipelines](docs/operations/private-pipelines.md#private-saved-plan-storage)). No backend storage, credentials or private paths are bundled here.

The supplied landing-zone resources are input/data references, not managed resource declarations. **Default `terraform destroy` is blocked** by `prevent_destroy` on the owned resource group, workspace, storage/container and vault. It cannot destroy the supplied VNet/subnets, DNS zones or logging workspace. Vault purge protection also preserves soft-deleted vault data. Customer-managed NSGs and their rules/associations are outside Terraform ownership. Do not switch an already deployed owned NSG to customer-managed mode just by changing the configuration: review ownership/state migration with the infrastructure owner first.

To retire a deployment for good, first save any models or videos you need; the owner must agree that all of its data can be deleted. This procedure is unverified live:

1. Stop every region (Administration page or `scripts\recovery\Stop-Studio.ps1`) and confirm release is verified.
2. Delete the Entra objects created by the [Entra bootstrap](docs/operations/entra-bootstrap.md): the `WAN Safety Studio` registration and the creators and administrators groups. Both stay restorable for 30 days.
3. Delete each owned resource group deliberately (for example `az group delete -n rg-<stem> --yes`), which bypasses `prevent_destroy`. Only owned groups are deleted; supplied VNets, subnets, NSGs, DNS zones and logging workspaces stay untouched. The Key Vault and Azure ML workspace are soft-deleted, and purge protection keeps the vault's name reserved, so a new deployment needs a new `deployment_name`.
4. Remove the Terraform state and any cached settings documents, and tell the network owner that NAT associations and private endpoints are gone.

Direct `terraform apply` bypasses the hosted spend and release checks. Restrict state/deployment access. The submission gate is a safeguard, not a substitute for Entra access controls against another authorized workspace user. Both providers explicitly enable CLI authentication and disable MSI/OIDC/AKS workload identity, but their schemas cannot exclude ambient client-secret/certificate credentials entirely; the workflows' sanitized child environment removes them, so run live Terraform through them rather than from a credential-bearing shell.

## Integrity, packages and provenance

The exact upstream revision, checked patch, adapter files, resolved Python lock and manifest participate in content-addressed asset versions. AML **model names** contain the content hash and model versions are positive integer strings (`"1"`); environments have their separate version. Images use an immutable digest. Storage shared keys, ACR admin/anonymous access and public service access are disabled. Code/models/images and uploaded input images use scoped managed-identity/datastore inputs, not embedded secrets or image SAS arguments.

The GPU image is built by [build/gpu](build/gpu/README.md) from the pinned upstream source. Cost and submission guards live in the .NET server (`AzureMlGuard`); the job itself runs only the upstream ComfyUI runner. The CPU workflow-node probe imports `main` before `nodes` from the **actual staged snapshot**, with custom nodes disabled, writable scratch, read-only source, no network and no GPU. WAN model snapshots are reused across adapter-only changes after SHA256/size/path verification.

The eight entries in `build/gpu/wan-pinned-manifest.json` (the 14B high/low-noise pair, text encoder, VAE, the two lightx2v 4-step LoRAs, the Wan 2.2 VAE and the 5B TI2V checkpoint, so Draft, Fast, Turbo and Prime all run) preserve immutable Hugging Face URLs, exact sizes and SHA256 (44,719,819,337 bytes). Revision-pinned model cards and license provenance are retained with the staged assets. Gated models fail the build; tooling does not accept licenses for you. Optional LTX/H3 paths are retained but are **not validated customer profiles**.

Package restores use one approved feed per ecosystem; Python build requirements are SHA-256 locked, and no credentials or insecure package flags are baked into images (details in [private pipelines](docs/operations/private-pipelines.md#explicit-configuration-and-identities)). TLS verification stays enabled.

## Local checks and validation boundary

None of these authenticates to or provisions Azure, downloads weights or builds/pushes images:

```powershell
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts
python -m unittest discover -s build\gpu -p "test_*.py"
python -m unittest discover -s infra -p "test_*.py"
pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1
pwsh -NoProfile -File scripts\recovery\Test-StudioRecovery.ps1
pwsh -NoProfile -File scripts\Test-LegacyLifecycle.ps1      # frozen legacy Stop, until it is deleted
```

The pipeline tests (`python -m unittest discover -s scripts\ci -p test_pipeline.py`) need `scripts\ci\requirements.lock` installed with `--require-hashes` and `STUDIO_CONTRACT_TOOL` set to the built `tools\VideoSafetyStudio.Contracts` dll for the cross-language cases. `.github\workflows\ci.yml` runs these checks. `WAN_STUDIO_P2_UPSTREAM` optionally points the GPU build tests at an exact pinned Git source fixture.

Terraform tests use provider mocks: private-service properties, one-node Spot limits, supplied-resource boundaries and rejected unsafe inputs. `terraform test` auto-loads `infra\terraform.tfvars.json`, so run the mocked suite through `Export-MockedContracts.ps1` (it uses a clean fixture) rather than from a directory holding customer values. `infra/tests/fixtures` holds synthetic outputs captured from native Terraform mock plans; the exporter refuses unknown values and non-synthetic subscriptions.

**Reference validation (v1, retired tooling).** An earlier single-region version was exercised in a reference landing zone with a customer-managed NSG: foundation apply with every private endpoint approved, an 18-rule restricted NSG baseline applied by the network owner, private Spot A100 min-zero/max-one compute with an owned NAT, CPU model and image preparation, local sign-in, a WAN generation job saved to the library, Stop releasing compute/NAT/public IP, and a retirement. That used the retired PowerShell/Python operator. The current workflows, regional promotion, hosted lifecycle and the hosted App Service studio have not been validated end to end. Every customer deployment must verify its own DNS and routing, quota, capacity and video quality.

## Attribution

Upstream MIT: **Copyright 2022 (c) Microsoft Corporation.** Its original [LICENSE](https://github.com/jakeatmsft/azureml_vidgen_comfyui/blob/dc0d29031b73a5ba7376d910adb7a58c206f32b1/LICENSE) and `NOTICE.txt` are preserved in staged code. The local patch is not an upstream release.

WAN metadata comes from `Comfy-Org/Wan_2.2_ComfyUI_Repackaged@c4f60d30c55a624e35427060fdd217579a6c1d77` and `Comfy-Org/Wan_2.1_ComfyUI_repackaged@617a7633e636506f850e043bc4605f290a466a8e`; their model cards identify Apache-2.0. Model licenses are separate from application licensing.

ARM references: [compute deletion](https://learn.microsoft.com/rest/api/azureml/compute/delete?view=rest-azureml-2024-04-01), [job cancellation](https://learn.microsoft.com/rest/api/azureml/jobs/cancel?view=rest-azureml-2024-04-01), [AML quota](https://learn.microsoft.com/rest/api/azureml/usages/list?view=rest-azureml-2024-04-01), [keyless datastore](https://learn.microsoft.com/rest/api/azureml/datastores/create-or-update?view=rest-azureml-2024-04-01).

## Local development with Aspire

Needs .NET 10 SDK, Aspire CLI 13.6, Node 24, ffmpeg and (for training) Docker.

```powershell
aspire start      # AppHost: src\VideoSafetyStudio.AppHost; dashboard URL is printed
# UI: http://localhost:51881   server: http://localhost:51882
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts
```

The AppHost runs the .NET 10 minimal-API server (`src\VideoSafetyStudio.Server`: vision datasets, ComfyUI generation, training runs, SSE push at `/api/events`) and the React/TypeScript UI in `web\` (Vite). `dotnet publish src\VideoSafetyStudio.Server` builds the UI and hosts it from the server's `wwwroot`. Storage (Azurite default, folder/LAN share, Azure), ComfyUI and models are covered in [local generation](docs/local-generation.md); configure with a hand-written, git-ignored `src\VideoSafetyStudio.AppHost\appsettings.Development.json` or environment variables. The Create page has an optional bring-your-own-key [Video Production Helper](docs/video-helper.md) (AG-UI, Microsoft Agent Framework, GitHub Copilot harness). Sign-in is Entra OIDC and is required whenever the environment is not Development (`Auth:Mode` defaults to `None` in Development and `Entra` otherwise; a hosted site can opt out only with `Auth__Mode=None` behind a gateway that authenticates); under Aspire with ComfyUI there is none. `Generation__Provider=azureml` (the default for the server, set to `comfyui` by the AppHost) submits Azure ML jobs from the server itself. The hosted container is built from `src\VideoSafetyStudio.Server\Dockerfile` ([deployment](docs/app-service.md)). The vision dataset builder is hidden until `FEATURE_DATASET_BUILDER_ENABLED=true` is set in the environment that starts the AppHost.
