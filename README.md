# WAN Safety Studio

A portable, private Azure ML WAN/ComfyUI baseline for an **existing customer landing zone**, provisioned with Terraform. Creators use an Entra/MSAL-protected studio: either the operator's loopback portal at **http://localhost:51881/**, or an optional [private App Service](docs/app-service.md) reachable only through a private endpoint. Do not run the upstream public deployment.

The application is pinned to MIT-licensed [`jakeatmsft/azureml_vidgen_comfyui@dc0d29031b73a5ba7376d910adb7a58c206f32b1`](https://github.com/jakeatmsft/azureml_vidgen_comfyui/tree/dc0d29031b73a5ba7376d910adb7a58c206f32b1).
This repository contains the adapter, safety patch, model manifest and Terraform, not the upstream checkout, images or weights.

**New CSA / customer setup:** Start with the [customer onboarding guide](docs/customer-onboarding.md). It explains the approvals, landing-zone details, and Entra prerequisites that must be collected before any deployment command is run.

## What is provisioned

```text
Customer workstation -- existing private connectivity/DNS -- customer PE subnet
  loopback portal                                           |
  Azure CLI + PowerShell                                    +-- Blob / File
  CPU preparation + Docker                                  +-- Key Vault
                                                            +-- Premium ACR
                                                            +-- Azure ML API / notebooks
                                                                 |
Customer dedicated GPU subnet <------------------------------ Azure ML workspace
  owned or customer-managed NSG                             workspace identity
  optional owned NAT + public egress IP                      compute identity
  one private Spot A100 node, only after Start               App Insights -> supplied Log Analytics
```

Terraform creates an owned resource group, private storage, an RBAC Key Vault, Premium ACR, App Insights, two managed identities, scoped role assignments and private endpoints. By default it also creates and associates a GPU NSG; set `existing_gpu_nsg_id` to use an already attached customer-managed NSG without managing its rules or association. With the optional `portal` object it also creates a one-instance Linux App Service (public access disabled), its private endpoint, and a portal managed identity with only blob-data and workspace data-scientist access. **It does not create or own your VNet, subnets, private DNS zones, DNS links/resolver, VPN or Log Analytics workspace.** Existing DNS zones receive private endpoint zone groups; existing subnet associations are changed only under the dedicated-subnet contract.

Only Azure public cloud, one subscription and the exact `Standard_NC24ads_A100_v4` SKU are supported. There is no larger-SKU, Dedicated, public-endpoint or alternate-identity fallback.

## Prerequisites

- PowerShell **7.4+**, Azure CLI, Terraform **1.9+ (below 2.0)**, Git, Python 3.12 and uv. The dependency lock pins AzureRM 5.6.0 and AzAPI 2.12.0. `Prepare` additionally needs Linux Docker, substantial local disk (WAN weights alone are about **33.1 GiB**), and model-license acceptance/access where required.
- Normal `az login --tenant <tenant-id>` and `az account set --subscription <subscription-id>`. The operator refuses a tenant/subscription mismatch rather than switching accounts. Operator-side Python uses Azure CLI credentials only (the optional App Service host uses its own managed identity); Terraform child processes cannot inherit service-principal secrets, managed-identity/OIDC switches or `TF_VAR_*` overrides.
- A **dedicated GPU subnet** and a distinct private endpoint subnet in the configured subscription/region. Supply the exact GPU CIDR and explicitly acknowledge dedication. The GPU subnet must have `defaultOutboundAccess=false`, no service delegation and no unrelated NAT association. An existing NSG must be explicitly selected with `existing_gpu_nsg_id`; otherwise only an absent or accelerator-owned NSG is accepted. Follow the [customer NSG handoff](docs/customer-nsg.md) when policy manages the NSG. Existing route tables and other subnet properties are preserved.
- Existing private DNS zones for `privatelink.blob.core.windows.net`, `privatelink.file.core.windows.net`, `privatelink.vaultcore.azure.net`, `privatelink.azurecr.io`, `privatelink.api.azureml.ms` and `privatelink.notebooks.azure.net`, linked/resolved by your landing zone. Supply their IDs; this solution will not create zones or configure workstation DNS.
- An existing Log Analytics workspace. Your deployment identity needs permissions to create the owned resources/role assignments, join the supplied subnets, and associate private endpoints with the supplied zones. Your operator principal receives service-scoped data access, not subscription Owner.
- Private workstation connectivity to Blob/File, Vault, ACR and AML endpoints. The operator checks private DNS resolution and HTTPS reachability before preparing or starting. GPU outbound access must satisfy [Azure ML network requirements](https://learn.microsoft.com/azure/machine-learning/how-to-secure-training-vnet?view=azureml-api-2). Use customer-controlled routing/firewall egress, or explicitly opt into this deployment's temporary NAT. NAT provides outbound connectivity, **not destination filtering**.
- AML LowPriority quota for 24 vCPUs, A100 family quota and regional Spot capacity. An unrestricted SKU and quota do not guarantee capacity.

Resource-provider registration is deliberately disabled. The customer must already have `Microsoft.Network`, `Microsoft.ManagedIdentity`, `Microsoft.Storage`, `Microsoft.KeyVault`, `Microsoft.ContainerRegistry`, `Microsoft.Insights` and `Microsoft.MachineLearningServices` registered; the supplied logging workspace also requires `Microsoft.OperationalInsights`, and the optional App Service portal requires `Microsoft.Web`.

The code uses PowerShell 7 executable paths on Windows/Linux/macOS and user-local caches. **Local validation was performed on Windows only**; Linux/macOS process identity and end-to-end operation remain unverified.

## Configure once

Use **`infra/terraform.tfvars.json`** as the single customer settings file shared by Terraform, PowerShell and Python:

```powershell
Copy-Item .\infra\terraform.tfvars.json.example .\infra\terraform.tfvars.json
# Edit every placeholder in terraform.tfvars.json before proceeding.
az login --tenant <tenant-id>
az account set --subscription <subscription-id>
terraform -chdir=infra init -backend=false
terraform -chdir=infra validate
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Check
```

Required inputs are deployment name, tenant/subscription, location, operator principal, dedicated GPU subnet ID/CIDR, PE subnet ID, six private DNS zone IDs and Log Analytics workspace ID. `manage_compute_egress` defaults to false; set true only to authorize this deployment's temporary NAT/PIP association. `max_payg_hourly_usd` defaults to **4**. The example JSON intentionally has `compute_enabled=true` for the configured scale-to-zero target. For a brand-new environment, temporarily set it to `false` for the initial foundation Deploy, then restore it to `true` and use the explicitly approved compute-enabled Deploy. The module's omitted-variable default remains `false`.

`egress_public_ip_tags` is an optional object of Azure public-IP policy tags, distinct from ordinary resource tags. Ask the network owner for required values before enabling NAT. For example, MCAPS policy appends `{"FirstPartyUsage":"/Unprivileged"}`; record that exact value in the customer JSON rather than letting a later plan remove it and replace the public IP. Other customers should leave `{}` unless their own policy requires tags.

`existing_gpu_nsg_id` defaults to `null` (owned NSG). For a customer-managed NSG, copy its full same-subscription resource ID into this field. Deploy can provision the foundation before its rules are ready; export `terraform -chdir=infra output -json gpu_nsg_rules` for the security team. Do not Start until they have applied/reconciled the rules; each Start then also requires `-ApproveCustomerNsgRules`. This acknowledgment does not automatically verify rules or firewall policy.

The operator rejects additional automatically loaded `.tfvars`/`.auto.tfvars` files rather than allowing hidden overrides. Use `-ConfigPath <absolute JSON path>` to select the input JSON; keep only that input file in Terraform's automatic load paths. For another deployment, use a separate repository checkout/Terraform working directory and state. Never reuse one state for two customers.

Terraform's `studio` output is the cross-layer contract: names, resource IDs, ownership tags, network inputs and limits. After deployment, the operator validates and caches it as `foundation.json`. Python receives the explicit config and receipt paths, not hard-coded names. Owned resources require `application=wan-safety-studio`, `deployment=<deployment_name>`, `managedBy=terraform`; a customer NSG is validated against the explicitly selected ID instead and retains its customer's tags.

The cache is outside the repository: `%LOCALAPPDATA%\wan-safety-studio` on Windows, `$XDG_CACHE_HOME/wan-safety-studio` (or `~/.cache/wan-safety-studio`) on Linux, and `~/Library/Caches/wan-safety-studio` on macOS, partitioned by subscription and deployment. Override with `-CacheDirectory` or `WAN_STUDIO_CACHE`; use the same override for every lifecycle command. Restrict cache access to the operator. It contains prepared assets, the release receipt and local submission/process state.

## Lifecycle

For the authenticated local UI, follow [local dashboard setup](docs/local-dashboard.md). It includes the required, repeatable Entra app/group/Key Vault setup and starts the portal separately from GPU compute. To give creators a hosted, private URL instead, follow [private App Service](docs/app-service.md): add `portal` to the JSON, then Deploy, rerun `Initialize-PortalAuth.ps1`, and run `-Action Publish` before Start.

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Deploy -ApprovePersistentCosts
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Prepare -Profile wan
# Optional: reuse checksum-verified local weights, without uploading unchanged model snapshots.
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Prepare -Profile wan -LocalModelsPath <models-directory>

.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges
# If tenant policy requires admin consent, the authorized Entra admin also runs:
# .\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges -ApproveAdminConsent
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Portal -Profile wan
# http://localhost:51881/; foreground server, Ctrl+C stops only the UI. No GPU is enabled.

.\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan -ApproveGpuSpend
# Customer-managed NSG: also pass -ApproveCustomerNsgRules after security-team sign-off.
# Start includes a portal; use -NoPortal if the separate Portal action is already running
# or creators use the hosted App Service. Start/Stop also arm/disarm the hosted portal.
# Hosted portal only (docs/app-service.md), after Deploy/Prepare/Initialize-PortalAuth:
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Publish -Profile wan
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Stop
.\scripts\Invoke-WanSafetyStudio.ps1                 # default: live Status
```

**The initial compute-disabled Deploy leaves compute/NAT/PIP absent.** Deploy requires explicit persistent-cost approval. After the foundation exists, set `compute_enabled=true` and, for the approved NAT design, `manage_compute_egress=true`, then run:

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Deploy -ApprovePersistentCosts `
    -ApproveGpuSpend -ApproveCustomerNsgRules
```

Omit the NSG acknowledgment only for accelerator-owned NSGs. This path honors the selected compute flag, checks live price/quota, private connectivity and no queued/running jobs, and provisions a private min-zero/max-one target. It **does not arm submissions or submit a job**. The NAT/public IP is for outbound SNAT only, with service-tag-restricted NSG access. The user must approve its standing cost while zero GPU nodes are allocated.

Prepare downloads/verifies code and model assets, builds/tests the image on local CPU, pushes through Entra authentication and registers immutable private AML inputs. It does not allocate a GPU. Preparation accepts either absent compute or a verified zero-node cluster with no active jobs and no armed submission gate; it refuses an active/armed deployment.

`Portal` serves the prepared UI without creating an `armed.json` submission gate or changing compute. Authentication setup is owned by `Initialize-PortalAuth.ps1`, not the foundation Terraform. Only members assigned the `VideoCreator` application role may sign in; browser identity never receives the operator's Azure CLI tokens. Local sessions are single-process and temporary; the App Service host is likewise pinned to one instance.

Start requires explicit GPU-spend approval, a verified prepared profile, private connectivity, no nonterminal jobs, an exact fresh regional Linux PAYG price under the configured threshold, SKU availability and LowPriority quota. It creates a **LowPriority cluster with min=0/max=1, 120-second idle scale-down, no node public IP and no public SSH**. Jobs allocate the node; Start is not GPU warmup.

If Deploy already provisioned the cluster, Start performs read-only cloud checks and atomically arms the verified release; **it does not rerun Terraform or replace infrastructure**. Managed-egress checks also verify the NAT/public-IP/subnet links and declared IP policy tags. Failure while arming an existing target closes the submission gate without deleting that target. If Start must provision missing compute, it reviews a saved Terraform plan first and refuses any deletion/replacement; only a failed apply that actually started provisioning triggers emergency resource release.

Deploy also applies a reviewed saved plan and refuses deletions/replacements. Resolve unexpected policy drift instead of approving a destructive plan just to enable submissions. Stop still deletes owned compute/NAT/PIP; with `compute_enabled=true` left in the JSON, a later approved Deploy intentionally recreates them. Setting the flag is configuration, not approval to submit jobs.

For a single smoke generation using your synthetic 512x512 PNG:

If using a customer-managed NSG, add `-ApproveCustomerNsgRules` to the Start command below after completing the security-team handoff.

```powershell
try {
    .\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan -ApproveGpuSpend -NoPortal
    .\scripts\Invoke-WanSafetyStudio.ps1 -Action Submit -Profile wan `
        -InputImage .\synthetic.png -TimeoutMinutes 30 -Wait -DownloadDirectory .\outputs
} finally {
    .\scripts\Invoke-WanSafetyStudio.ps1 -Action Stop
}
```

Submit uses 512x512, four steps, batch one and a one-second workflow request. The explicit blank negative prompt stays blank instead of restoring workflow defaults. Azure ML receives one instance and a **server-side timeout of at most 7200 seconds** (30 minutes in this example). Both submission factories perform a fresh server read to verify compute, instance count and timeout. Missing/mismatched controls trigger cancellation and an error, never resubmission.

`-Wait` downloads only into the current job's directory. WAN's native WebM is preserved, then converted on local CPU to H.264 MP4 and inspected with ffprobe for positive duration and 512x512 dimensions. The existing local image can provide CPU ffmpeg/ffprobe when host tools are absent; no GPU or additional image pull is used. Without `-Wait`, inspect the returned job ID/status and `last-job.json`, then Stop.

## Cost and emergency release

**The PAYG threshold is a Start-time guard, not a billing cap, maximum Spot bid or global time budget.** Spot price varies. The 120-minute limit applies per job; multiple jobs, queues, provisioning and deallocation can add charges. No auto-resubmission is performed.

The guard matches the exact A100 SKU/region and Virtual Machines service, accepting both the legacy `Virtual Machines NCads A100 v4 Series` and current `NCads A100 v4 Series Linux` product names. Windows, Spot/Low Priority meters, unknown OS names and ambiguous results remain rejected. A catalog label change is not a reason to raise the configured price ceiling.

**Stop does not make the whole solution free.** Premium ACR has a standing charge (often around $50/month, region/pricing dependent); private endpoints, storage/models/videos, App Insights/Log Analytics ingestion and retained customer networking can also incur charges. When enabled, owned NAT/PIP exist until Stop verifies their removal.

Emergency Stop uses **Azure CLI authentication + native ARM + the cached output receipt**. It does not require Python, Docker, prepared models, a working pricing endpoint or a Terraform plan. It:

1. Removes the local submission gate and stops only the recorded portal PID after validating its start time, script and executable.
2. Validates configured scope and exact owned resource IDs/tags, attempts to cancel jobs targeting only this compute, deletes compute with `underlyingResourceAction=Delete`, and waits for fresh ARM absence.
3. Detaches **only its own NAT** from the exact dedicated subnet using an ETag-guarded update; preserves routing/NSG properties; deletes owned NAT/PIP and verifies absence.
4. Requires fresh reads proving all owned-compute jobs terminal, then reconciles Terraform state. Cancellation API errors become warnings only after job termination is independently verified.

State recovery checks the cloud absence and the exact state address/ID before forgetting a released compute/NAT/PIP/association. It preserves unrelated state, then uses a **refresh-only** operation with compute disabled; it does not recreate resources or run a general deployment. No blanket lifecycle ignore or forced destroy is used. Repeated Stop is safe. Any unverified release, remaining job, portal identity error or failed reconciliation remains an error and leaves a local marker blocking Start/Deploy; rerun Stop after correcting access/tooling. Native resource release precedes Terraform reconciliation even when Terraform is broken.

Keep the cached `foundation.json` receipt: Stop intentionally refuses to guess resource names if it is missing. If lost, with the correct config/state selected, recover it using `terraform -chdir=infra output -json studio` into the deployment cache's `foundation.json` (UTF-8), then rerun Stop. Do not copy a receipt between deployments.

## Terraform state and removal

The initial backend is local. Protect and back up `infra/terraform.tfstate` and the cache receipt; neither belongs in git. Before collaborative/customer operation, configure an approved encrypted remote backend with locking and Entra authentication, then explicitly migrate state. No backend storage, credentials or private paths are bundled here.

The supplied landing-zone resources are input/data references, not managed resource declarations. **Default `terraform destroy` is blocked** by `prevent_destroy` on the owned resource group, workspace, storage/container and vault. It cannot destroy the supplied VNet/subnets, DNS zones or logging workspace. Vault purge protection and provider non-purge behavior also preserve soft-deleted vault data.

Customer-managed NSGs and their rules/associations are also outside Terraform ownership. The counted owned-NSG resources include state address moves to preserve existing default deployments. Do not switch an already deployed owned NSG to customer-managed mode just by changing the JSON: review ownership/state migration with the infrastructure owner first to avoid scheduling the old NSG or association for deletion.

For intentional foundation retirement, export needed models/videos, run Stop, and have the customer owner explicitly review retention and remove the relevant lifecycle protections before reviewing a destroy plan. That is a separate destructive operation, not an operator action or forced-destroy shortcut. Owned subnet associations must be removed without deleting the supplied subnet. This task does not execute destroy or deploy.

Direct `terraform apply -var=compute_enabled=true` bypasses the local spend/preparation checks. Restrict state/deployment access and use the operator. The submission gate is an operator safeguard, not a substitute for Entra access controls against another authorized workspace user.

Both providers explicitly enable CLI authentication and disable MSI/OIDC/AKS workload identity, but their schemas cannot exclude ambient client-secret/certificate credentials entirely. **Use the operator for live operations**: its sanitized Terraform child environment removes those credentials and credential-file selectors. Running Terraform directly in a credential-bearing shell does not provide that guarantee. The documented mock tests do not authenticate to Azure.

## Integrity, packages and provenance

The exact upstream revision, checked patch, adapter files, resolved Python lock and manifest participate in content-addressed asset versions. AML **model names** contain the content hash and model versions are positive integer strings (`"1"`); environments have their separate version. Images use an immutable digest. Storage shared keys, ACR admin/anonymous access and public service access are disabled. Code/models/images and uploaded input images use scoped managed-identity/datastore inputs, not embedded secrets or image SAS arguments.

Source staging excludes asset roots, weights, environment files, VCS metadata and caches without dropping real packages such as `comfy.ldm.models` or `comfy_api.input`. The CPU workflow-node probe imports `main` before `nodes` from the **actual staged snapshot**, with custom nodes disabled, writable scratch, read-only source, no network and no GPU. WAN model snapshots are reused across adapter-only changes after SHA256/size/path verification; source/environment changes do not force another weight upload.

The four entries in `app/wan-pinned-manifest.json` preserve immutable Hugging Face URLs, exact sizes and SHA256 (35,579,207,879 bytes). Revision-pinned model cards and license provenance are retained with the staged assets. Gated models fail before Start; tooling does not accept licenses for you. Optional LTX/H3 paths are retained but are **not validated customer profiles**.

Public **PyPI** is the default in the committed `pyproject.toml` and `uv.lock`. TLS verification remains enabled; native OS trust is supported. Helper processes receive an environment allowlist and cannot inherit arbitrary infrastructure/GitHub credentials or insecure package flags. No private package feed or global package configuration is required.

An explicit credential-free HTTPS `WAN_STUDIO_PYPI_INDEX` override selects a customer mirror for both Docker and local installation. The operator exports exact versions/hashes from the unchanged public lock, then installs mirror wheels with `uv pip sync --require-hashes --only-binary :all: --strict`; it does not rewrite the lock or permit different artifacts. Ambient UV/PIP indexes are not forwarded and `--no-config` disables implicit local/global uv settings. Authentication-bearing mirror URLs are unsupported. Direct public artifact TLS failed on the validation workstation even with native trust; an approved local-only mirror supplied hash-matched packages for SDK validation. This limitation does not change the customer default, disable TLS or embed a mirror URL in the repository.

## Local checks and validation boundary

```powershell
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra init -backend=false -input=false
terraform -chdir=infra validate
terraform -chdir=infra test
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Check
```

Terraform tests use provider mocks: compute off/on, private-service properties, one-node Spot limits, supplied-resource boundaries and rejected unsafe inputs. Framework-free Python/PowerShell checks exercise configuration/receipt scope, secret filtering, Start failures, price/quota validation, submission guards and exact-owned native Stop ordering/state reconciliation. They do not authenticate to or provision Azure, download weights or build/push images.

`infra/tests/fixtures/studio-off.json`, `studio-on.json` and `studio-customer-nsg.json` are synthetic outputs captured from actual native Terraform mock plans, not hand-maintained guesses. Both consumers validate this contract. Regenerate them with `pwsh -NoProfile -File .\infra\tests\Export-MockedContracts.ps1`; the exporter refuses unknown values and non-synthetic subscriptions.

For the pinned upstream factory check, set `WAN_STUDIO_UPSTREAM_TEST_ROOT` to a local checkout at the revision above. The framework-free tests use isolated patched source with SDK fakes and no cloud calls. `app/test_sdk_controls.py` additionally exercises both factories using the installed Azure ML SDK and the actual staged snapshot:

```powershell
uv sync --project .\app --python 3.12 --locked --native-tls
$env:WAN_STUDIO_UPSTREAM_TEST_ROOT = '<absolute-pinned-upstream-checkout>'
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Check
# Use app/.venv/bin/python on Linux/macOS.
.\app\.venv\Scripts\python.exe .\app\test_sdk_controls.py `
    --upstream <pinned-upstream-checkout> --scratch <new-temporary-directory>
```

Use a new scratch directory and remove that specific directory afterward. These checks require no private source checkout and download no weights.

**The Terraform foundation was live-deployed in East US 2 on 2026-09-21 using customer-managed NSG mode.** Apply completed with 24 Terraform resources added (including the local landing-zone guard), none changed or destroyed. Five private endpoints were approved and provisioned; Storage, ACR, Key Vault and Azure ML retained disabled public access. The identity-based datastore and cached operator receipt were verified. The customer NSG's ETag/rules and association remained unchanged, and live Status confirmed no compute, NAT, public IP or active jobs. The [handoff procedure](docs/customer-nsg.md) documents deployment, live checks, rule exports and partial-deployment recovery.

This validates foundation provisioning, not end-to-end generation or the optional hosted App Service portal. Model/image preparation, workstation DNS/routing, GPU egress, customer NSG rules, quota/capacity and video quality remain to be verified on this deployment. The predecessor application completed a private Spot A100 WAN job on 2026-09-17 with `PT30M`, one instance, 512x512 WebM (17 frames at 16 fps) and a CPU-converted H.264 MP4 (1.063 seconds), with full decoding and final compute/NAT/PIP release verified; that remains application provenance, not a generation test of this Terraform deployment.

On 2026-09-23, the customer NSG owner applied the **18-rule restricted service-tag baseline** (no blanket Internet allow). The approved compute-enabled Terraform deployment then provisioned private Spot A100 min-zero/max-one compute plus an owned NAT/public IP. ARM readback confirmed zero allocated nodes, no active jobs, the intended identity/subnet/idle/SSH controls, the correct NAT association, and unchanged customer NSG ETag. CPU model/image preparation and local MSAL creator sign-in also succeeded. These observations still do not constitute a GPU generation/egress test; no job was submitted by the deployment.

## Attribution

Upstream MIT: **Copyright 2022 (c) Microsoft Corporation.** Its original [LICENSE](https://github.com/jakeatmsft/azureml_vidgen_comfyui/blob/dc0d29031b73a5ba7376d910adb7a58c206f32b1/LICENSE) and `NOTICE.txt` are preserved in staged code. The local patch is not an upstream release.

WAN metadata comes from `Comfy-Org/Wan_2.2_ComfyUI_Repackaged@c4f60d30c55a624e35427060fdd217579a6c1d77` and `Comfy-Org/Wan_2.1_ComfyUI_repackaged@617a7633e636506f850e043bc4605f290a466a8e`; their model cards identify Apache-2.0. Model licenses are separate from application licensing.

ARM references: [compute deletion](https://learn.microsoft.com/rest/api/azureml/compute/delete?view=rest-azureml-2024-04-01), [job cancellation](https://learn.microsoft.com/rest/api/azureml/jobs/cancel?view=rest-azureml-2024-04-01), [AML quota](https://learn.microsoft.com/rest/api/azureml/usages/list?view=rest-azureml-2024-04-01), [keyless datastore](https://learn.microsoft.com/rest/api/azureml/datastores/create-or-update?view=rest-azureml-2024-04-01).
