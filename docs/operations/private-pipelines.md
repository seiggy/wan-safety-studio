# Private build, promotion, foundation and recovery pipelines

P4 defines these workflows; none has been dispatched or live-validated as part of
this refactor. Hosted lifecycle, regional admission and release selection remain
P5/P6 work. A verified release binding proves prepared assets, not Spot capacity.

## If you use a different CI or registry

The workflows below are the reference implementation. The scripts they call do not need GitHub: they need the same settings document, three environment variables and a private network route. Images go to your own Azure Container Registry, not GHCR. See [Select a release: where the artifact ID comes from](hosted-lifecycle.md#select-a-release-where-the-artifact-id-comes-from) for the prerequisites, the commands and the checks to make if a first run on another platform fails.

## Entry points and approvals

| Workflow | Protected environment | Result |
| --- | --- | --- |
| `release.yml` | `studio-build` | One independently selected server, GPU or YOLOX artifact; never deploys |
| `promote.yml` | `studio-publish` | The selected sealed GPU build (`component: gpu`) or trainer image (`component: yolox`) in every configured region; each binding is published last |
| `foundation.yml`, plan job | `studio-plan` | Non-destructive saved binary plan and matching JSON in private deployment storage |
| `foundation.yml`, apply job | `studio-apply` | Separately approved application of that exact saved binary plan |
| `deploy-server.yml` | `studio-server` | An approved server artifact's registry digest; no build or arming |
| `recover.yml` | `studio-recovery` | Independent verified release through `scripts\recovery\Stop-Studio.ps1` |

Only manual dispatches from the protected default branch are supported. All
third-party actions are pinned by commit. Ordinary PR/push CI has no Azure
identity and never uses private runners. Do not add `pull_request`,
`pull_request_target`, arbitrary checkout refs or automatic publication triggers
to these privileged workflows.

An owner must configure required reviewers, prevent self-review where supported,
restrict each environment to the protected default branch, and restrict the
runner group to these trusted workflows. The YAML cannot create or prove those
GitHub settings. Use **ephemeral Linux x64** runners labelled `wan-private`, with
PowerShell 7.4+, Azure CLI, Docker/Buildx, Git and adequate disk. GitHub setup
actions supply .NET 10/Python 3.12; foundation additionally pins Terraform 1.16.6.
Never put a private runner in the ordinary PR runner pool.

The runner must resolve and reach the customer's private Blob/File, ACR, AML,
Vault and App Service/SCM endpoints through existing private connectivity. All
resolved service addresses must be RFC1918, with TCP 443 reachable. ARM, Entra,
GitHub/OIDC, approved feeds, base images and explicitly approved source acquisition
need separately approved outbound connectivity. No workflow creates a VPN,
changes DNS forwarding or opens a public service endpoint.

## Explicit configuration and identities

Store the following identifiers and JSON inputs in the appropriate protected
environment's **secrets**, outside Git. They contain no passwords, but masking
keeps customer inventory out of workflow environment logs. Use compact
single-line JSON. Runner cache paths/approval flags use environment variables,
resolved only in the build step.

| Variable | Contract |
| --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | The environment's approved federated service principal and explicit scope |
| `STUDIO_SETTINGS_JSON` | release, promote, deploy-server and recover: one explicit settings document shaped like `appsettings`: a `Studio` section identical to the web app's ([configuration guide](../configuration.md#studio-hosted-multi-region-topology)) and a `Pipeline` section that the web host never reads ([Pipeline settings](#pipeline-settings)). Sample: [studio-settings.json.example](../samples/studio-settings.json.example) |
| `STUDIO_TFVARS_JSON` | Foundation plan and apply only: the reviewed Terraform customer inputs ([infra\README.md](../../infra/README.md)); all `compute_enabled` values false. Not used by any other workflow |
| `STUDIO_BACKEND_JSON` | Foundation jobs only: the private existing backend/artifact location described below |
| `STUDIO_SETTINGS_PATH`, `STUDIO_TFVARS_PATH`, `STUDIO_BACKEND_PATH` | Explicit outside-checkout file alternatives to the respective inline JSON secret; never set both |
| `STUDIO_UPSTREAM_SOURCE` (variable, optional) | GPU build only: an absolute path to a persistent folder that caches the pinned upstream commit. The build creates it and fetches the commit on first use, then reuses it. If unset, the commit is fetched from github.com on every run |
| `STUDIO_MODEL_CACHE`, `STUDIO_LICENSE_CACHE` (variables, optional, set both) | GPU build only: absolute paths to persistent folders that cache the model files and model cards. The build creates them, downloads only missing files from huggingface.co, and re-verifies every file's hash on each run. If not both set, everything is downloaded on every run |
| `STUDIO_APPROVE_TRAINER_WEIGHTS` (variable) | Set exactly `true` only after approving the YOLOX image's pinned release-weight acquisition |
| `STUDIO_NPMRC`, `STUDIO_NUGET_CONFIG`, `STUDIO_NETRC` (variables) | Optional paths to protected runner/user-level credential files; never credential values |

Foundation applies emit no machine-readable contract and the pipeline never
writes back to environment secrets: after an apply the operator reads the
informational Terraform outputs and updates the settings document and the web
app's settings by hand ([app-service-settings.md](app-service-settings.md)). The
pipeline does not create GitHub administration tokens to update environment
secrets automatically. The same `Studio` JSON feeds the web app (as App Service
application settings) and the pipelines (as `STUDIO_SETTINGS_JSON`), so keep one
reviewed copy. For larger regional inventories use the file alternatives: GitHub
limits each secret to 48 KiB. Mount the same approved read-only file on each
private runner and provide its explicit path; no code guesses a customer path,
fetches configuration from the Internet or silently falls back. These are the
same JSON contracts, not a second configuration format. The `Studio` values
carry no secrets; the pipelines never read the web host's other settings.
Owners configure workload identity federation separately, using the exact
repository/environment subject and Azure public-cloud audience. Routine workflows
do not register Entra applications, grant tenant consent or create credentials.
`Azure/login` obtains a short-lived federated **CLI session** in an isolated
`AZURE_CONFIG_DIR`; Terraform deliberately uses that CLI session, not another
provider credential chain. Child processes discard ambient `ARM_*`, `TF_VAR_*`,
service-principal secrets and GitHub tokens. Azure CLI tenant/subscription/cloud
and, in GitHub, the explicit client ID are checked before resource writes.
Regional audit records also bind the actual CLI token's principal claim to
`Pipeline:PublisherPrincipalId` (settings document; the Terraform input is `publisher_principal_id`); a configured name is not accepted as caller evidence.
Opaque/unidentifiable ARM tokens fail closed rather than acquiring Graph directory
permissions. Azure services, not this audit parser, authorize the actual tokens.

Use separate identities and scopes:

- Build/promotion: the configured publisher principal (`Pipeline:PublisherPrincipalId`), with release-container
  writes, registry push and AML asset registration; no job/compute/IAM writes.
  Its extra storage-account metadata permission is **only**
  `Microsoft.Storage/storageAccounts/read`, for live privacy/ownership checks.
- Server deployment: server catalog/release reads, private registry pull, storage
  metadata reads, and the one site's read/config-read/config-write/start/stop.
  Entra bootstrap and redirect/managed-identity setup are separate approved work.
- Foundation plan/apply: reviewed resource, scoped IAM, existing subnet join and
  supplied DNS-zone association permissions required by `infra\`. Plan permission
  is not tenant administration. Review the IAM changes as well as infrastructure.
- Recovery: read the selected workspaces/jobs; cancel jobs; read/delete their
  exact compute; read/delete owned NAT/PIP and write only explicitly selected
  dedicated GPU subnets when managed egress is enabled; read/stop the one shared
  site. No compute creation, registry push, tenant or Terraform-state rights are
  required. These additional recovery/site permissions must be granted separately;
  no routine workflow grants itself access.

All package operations use one approved feed per ecosystem. The workflow default
is the device's Azure Artifacts proxy; customer team feeds require an explicit
reviewed change, not a public fallback. Pipeline Python requirements are SHA-256
locked, including the P2 build dependencies. GPU feed URLs and authentication use
BuildKit secret mounts, not image-history build arguments. The trainer image
build in the workflow uses the same secret mount. No infrastructure credentials enter worker images
or CPU-probe containers.
Pipeline I/O reuses the P2 OpenTelemetry provider for tool, ARM, immutable-record,
snapshot and image-transfer spans. Exceptions/arguments/tokens are not attached
to those spans. Export remains opt-in; configure an approved collector and the
matching exporter separately rather than forwarding infrastructure credentials.

## Pipeline settings

The `Pipeline` section of the settings document is read only by the pipelines. None of these values is an App Service setting, and the web host never reads them.

| Setting | What it is | Required by | How to find it |
| --- | --- | --- | --- |
| `Pipeline:PublisherPrincipalId` | Object ID (GUID) of the identity the release and trainer publishing workflows sign in as. Publication checks that the authenticated Azure CLI caller is exactly this principal. Terraform input `publisher_principal_id`. | release (build and publish), trainer publishing | Azure portal > Microsoft Entra ID > Enterprise applications (or the managed identity) > Object ID; or `az ad sp show --id <client id> --query id -o tsv`. |
| `Pipeline:SiteId` | ARM ID of the one shared App Service the server deployment may change. | deploy-server, recover | Azure portal > the web app > Overview > JSON View > Resource ID; or `az webapp show -g <rg> -n <site> --query id -o tsv`. |
| `Pipeline:Regions:<region>:WorkspaceIdentityId` | ARM ID of the user-assigned identity the Azure ML workspace uses. Before publishing, `publish_region.py` and `publish_trainer.py` verify the workspace is wired to this identity. A missing value stops publication. | release publishing to the region, trainer publishing | Azure portal > the workspace's managed identity > Properties > Resource ID; or `az identity show -g <rg> -n <identity> --query id -o tsv`. |
| `Pipeline:Regions:<region>:KeyVaultId` | ARM ID of the key vault the Azure ML workspace uses. The same publishing stage verifies the workspace is wired to this vault. A missing value stops publication. | release publishing to the region, trainer publishing | Azure portal > the key vault > Properties > Resource ID; or `az keyvault show -g <rg> -n <vault> --query id -o tsv`. |

`<region>` is the same key used under `Studio:Regions`. The sample shows the shape.

## Artifact and regional release contracts

The server, GPU and trainer builds run independently. Region, deployment scope,
mutable timeouts and workflow run IDs do not enter their artifact manifests.
GPU code/models/licences/build inputs use the shared strict build contract.
Server/trainer catalogs contain schema version, component, source commit and
registry manifest digest; their ID is SHA-256 of canonical JSON.

A GPU build first pushes a Linux amd64, single-platform image to a private
**unready** registry tag. This is staging, not AML publication or a usable release.
The builder disables provenance/SBOM index wrapping, pulls that exact registry
manifest digest, then runs the P2 CPU probe with `--pull never`, no network/GPU,
readonly source and writable isolated temporary directories. A digest is never
substituted with a Docker image-config ID. Probe failure produces no sealed build
or regional binding. Unready image storage can remain after failure; removal is a
separately reviewed retention operation, not hidden cleanup.

Only a sealed GPU build can publish these immutable private release paths:

```text
builds/<artifact-id>/manifest.json
code/<code-records-sha256>/...
models/<model-records-sha256>/...
licenses/<licence-records-sha256>/...
buildInputs/<build-input-records-sha256>/...
releases/<artifact-id>/<regional-scope-fingerprint>.json
images/server/<server-artifact-id>.json
images/yolox/<trainer-artifact-id>.json
```

Snapshot IDs hash the sorted records, independently of code/image changes.
Promotion reuses the model snapshot/model name when only the adapter or image
changes. Model version is the positive integer string `1`; content hashes belong
in names. Every reused or uploaded blob is downloaded and SHA-256/size checked,
and unexpected blobs abort publication. This needs significant private bandwidth
and temporary disk (the WAN weights alone are about 41.6 GiB); it does not allocate
GPU compute.

Promotion validates the settings document's `Studio` section against live ownership/privacy, managed
release datastore, digest-preserving registry transfer and exact completed AML
environment/model registrations. It never overwrites an existing asset. The
regional binding is the last create-only write. A partial region has no new
binding; another region's failure does not cancel successful matrix members.
Retries retain the original verified binding timestamp after rechecking content.
No release is automatically selected by the hosted app. An administrator selects a promoted GPU release by its artifact ID in the
Administration page. The ID is printed by the `release.yml` GPU build (`Verified gpu artifact <id>`); see
[hosted-lifecycle.md](hosted-lifecycle.md#select-a-release-where-the-artifact-id-comes-from).

## Private saved-plan storage

Create and authorize the Terraform backend and a **separate private artifact
container** through the customer's existing deployment/state administration.
The workflows never create or take ownership of them. Example variable shape
(all names are placeholders):

```json
{
  "resource_group_name": "customer-deployment",
  "storage_account_name": "customerstateaccount",
  "container_name": "terraform-state",
  "key": "wan-studio.tfstate",
  "artifact_container_name": "deployment-artifacts"
}
```

Both containers must be private, in the selected subscription, on an
Entra-only/private storage account. Use backend-container data access for
plan/apply and artifact-container access for reviewers; recovery does not need
state-container access.

Terraform runs in a new temporary source-only directory, excluding checkout
tfvars/state/cache. The generated backend explicitly uses Entra and CLI auth.
All deletes/replacements and forbidden compute/egress/customer-network owners are
rejected. Complete P3's verified-OFF legacy state migration manually first: the
routine workflow does not authorize even the migration's special GPU RBAC
replacement.

Saved binary/JSON plans and provenance are create-only under
`plans/<run-id>-<attempt>/` in the private artifact container. Before approving
`studio-apply`, inspect **that run's exact `reviewed.json`** with private storage
access; do not approve from the short resource-type log alone. The application job
checks source commit, run/attempt, the customer Terraform inputs (`STUDIO_TFVARS_*`), backend, provider lock and both
plan hashes, regenerates JSON from the saved binary, reruns safety checks, then
applies **without re-planning**. Terraform's backend lock/stale-state protection
still applies. Rerunning only failed jobs in a new attempt is deliberately
rejected; rerun the full workflow for a new review.

Plans/state/config are never uploaded as public GitHub artifacts or printed into
job summaries.

## Emergency release

Pause deployment/publication writers and any other privileged operator sessions
before recovery. The recovery workflow has its own concurrency group so a stuck
build/plan does not hold emergency release hostage. **P4 does not yet have P5's
durable cross-operation fence/serialization**: concurrent privileged writers must
be quiesced manually. Never treat an uncoordinated competing Start/deploy as safe.

Run with the settings document (the same `Studio` and `Pipeline` JSON the release pipelines use), kept outside Git:

```powershell
az login --tenant <explicit-tenant>
az account set --subscription <explicit-subscription>
pwsh -NoProfile -File scripts\recovery\Stop-Studio.ps1 `
  -SettingsPath <outside-git-studio-settings.json>
```

The same script is used by `recover.yml` (which reads `STUDIO_SETTINGS_JSON` or
`STUDIO_SETTINGS_PATH`). It needs only PowerShell, Azure CLI, the shipped JSON
schema and ARM connectivity, not Python, .NET SDK/server assemblies, Terraform,
Docker, weights, pricing or fresh Terraform output.

It rejects scope/ID/ownership mismatches before mutation, stops/verifies the
shared submission host, attempts every region, cancels relevant jobs, deletes
exact owned compute with `underlyingResourceAction=Delete`, and proves compute
absence/job termination before egress deletion. Only its own NAT attachment is
removed, using an ETag and preserving unrelated subnet properties. A foreign
attachment/resource is an error, never adopted or detached. Cancellation errors
become warnings only after fresh terminal reads. A final cross-region audit
checks absence, terminal jobs and the host's stopped state again.

Errors are aggregated and return failure; a partial release never prints
`VERIFIED OFF`. Repeating Stop is safe. Terraform owns no ephemeral resources, so
there is no state-forgetting/reconciliation step. A legacy Terraform-owned
compute must use its retained v1 recovery procedure and P3 migration, not this v2
script. The host stays stopped; restoring service is a separately approved action.

## Offline verification

These commands use fixtures, no Azure login/resources, images or weights:

```powershell
uv venv --python (Get-Command python).Source .artifacts\pipeline-checks
uv pip install --python .artifacts\pipeline-checks\Scripts\python.exe `
  --require-hashes --default-index https://packagefeedproxy.microsoft.io/pypi/simple `
  -r scripts\ci\requirements.lock
$pipelinePython = '.\.artifacts\pipeline-checks\Scripts\python.exe'
uv pip check --python $pipelinePython
dotnet build tools\VideoSafetyStudio.Contracts --artifacts-path .artifacts -v quiet
$env:STUDIO_CONTRACT_TOOL = (Resolve-Path '.artifacts\bin\VideoSafetyStudio.Contracts\debug\VideoSafetyStudio.Contracts.dll').Path
& $pipelinePython -m unittest discover -s scripts\ci -p test_pipeline.py -v
pwsh -NoProfile -File scripts\recovery\Test-StudioRecovery.ps1
pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1
python -m unittest discover -s infra -p 'test_*.py' -v
& $pipelinePython -m unittest discover -s build\gpu -p 'test_*.py' -v
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts
actionlint
```

Those local environment paths are Windows examples; Linux workflow setup uses
Python/pip directly. Linux/macOS execution has not been validated locally.
The contract tool is validation-only and shares Application's contract/rules;
recovery has independent native guards tested against the same settings fixtures.
No duplicate Python operational control plane is introduced.
