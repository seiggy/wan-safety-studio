# Execution boundaries and multi-region Azure ML: phased implementation plan

Status: implementation approved on 2026-10-21. P0-P10 are complete locally (P9 keeps one user-approved legacy residue). Offline acceptance passed; live acceptance has not been authorized or run.
Deployments, workflow execution, GPU spend, commits and pushes are not authorized.

Tracking issue: [execution-boundary implementation](https://github.com/seiggy/wan-safety-studio/issues/39).

This is the implementation tracker for separating release engineering, hosted operations,
and Azure ML execution, including the previously requested multi-region routing and
per-region job timeouts. It supersedes the earlier session-only multi-region plan,
particularly its proposal to build images and download models inside App Service.

## 1. Outcome and scope

The supported execution boundaries will be:

| Boundary | Responsibility | Must not do |
| --- | --- | --- |
| App Service: .NET server and React UI | Authenticated administration, regional settings, approved release selection, compute lifecycle, admission/routing, job submission/recovery, media/library and dataset APIs | Git checkout, package restoration, Docker builds, model acquisition, Terraform execution, Entra/bootstrap privilege escalation |
| GitHub Actions: build and deployment | Validate code/contracts, build immutable server/GPU/trainer artifacts, prepare models and licences, promote releases into regional workspaces, apply reviewed infrastructure plans | Start generation compute, train models, run a GPU smoke test, or mutate Azure from an untrusted pull request |
| Azure ML job | Run pinned ComfyUI/Python/CUDA generation or an approved trainer against managed inputs and outputs | Create infrastructure, submit another job, acquire unprepared weights/packages, or use operator credentials |
| Manual documentation | Landing-zone preparation, Entra consent/bootstrap, quota requests, local development and exceptional recovery procedures | Grow into another platform-management implementation or a speculative local TUI |

Retain Python where it is the existing build/worker implementation. Do not translate
working model-download or PyTorch code into C# just to eliminate Python from the tree.
Remove duplicate operational logic once its replacement and recovery coverage exist.

No new TUI is planned. Reconsider only if actual manual procedures later justify a
shared local tool; a list of possible commands is not sufficient justification.

## 2. Current evidence and starting point

- Working branch: `refactor/execution-boundaries`, created from updated `main` at
  `3174901`. Earlier timeout/Submit-cleanup changes were stashed, restored exactly,
  and remain uncommitted. The recovery stash is retained locally.
- The carried-forward patch has whitespace warnings in `azureml\Test-Controls.ps1`.
  Preserve it unchanged during planning; review formatting with the first approved
  change to those tests rather than silently altering the restored patch.
- `scripts\Invoke-WanSafetyStudio.ps1` is currently the lifecycle/build/deploy multiplexer.
- `azureml\runtime.py` builds and publishes releases; `models.py` stages verified weights.
- Production WAN jobs are submitted by .NET and execute the pinned upstream
  `azureml/run_workflow_job.py`, not this repository's Python operator.
- The upstream WAN runner imports `aml_project_layout.py`, `workflow_profiles.py`,
  and `workflow_utils.py`; it also uses `extra_model_paths.yaml` and upstream ComfyUI
  `src\`. The copied operator config/guard and upstream web/submit modules are not
  WAN execution dependencies.
- H3 preparation imports an upstream web module just for a revision constant.
  LTX preparation imports bootstrap metadata. These couplings must be separated
  before deleting legacy upstream helpers or changing experimental-profile support.
- `.github\workflows\release.yml` currently publishes only the server image.
  Regional GPU release preparation/promotion is not implemented.
- Training currently launches local Docker through `DockerComputeTarget`; it is
  not an Azure ML execution path. The dataset-builder feature is gated off by default.

Primary source anchors:

| Area | Existing files |
| --- | --- |
| Prepare, snapshot and verification | `azureml\runtime.py`, `azureml\models.py`, `azureml\config.py` |
| Operational lifecycle and emergency release | `scripts\Invoke-WanSafetyStudio.ps1` |
| Current job contract and guards | `src\VideoSafetyStudio.Infrastructure\AzureMl\AzureMlJobSpec.cs`, `AzureMlGuard.cs`, `AzureMlBackend.cs`, `AzureMlClient.cs` |
| Durable job recovery | `src\VideoSafetyStudio.Application\Generation\JobRecords.cs`, `src\VideoSafetyStudio.Server\Workers\GenerationWorker.cs` |
| Infrastructure and permissions | `infra\foundation.tf`, `infra\compute.tf`, `infra\portal.tf`, `infra\outputs.tf` |
| Training and CPU media tools | `src\VideoSafetyStudio.Application\Training\TrainingService.cs`, `src\VideoSafetyStudio.Infrastructure\Compute\DockerComputeTarget.cs`, `src\VideoSafetyStudio.Infrastructure\Media\FfmpegMediaTools.cs`, `trainers\` |

The original WAN application has historical live evidence. The new boundaries,
state migration and multi-region router do not. Offline checks must never be
presented as a live capacity, identity, networking or GPU validation.

## 3. Non-negotiable invariants

- Code implementation is approved. Deployment, workflow launches, real model/image
  acquisition, commits, pushes and GPU allocation still require separate authorization.
- Customer subnets, DNS zones, logging and customer-managed NSGs remain outside
  studio infrastructure ownership. Never replace/adopt/detach unrelated networking.
- Compute is private Linux Spot/LowPriority on the approved A100 SKU. No dedicated,
  larger-SKU or public-node/SSH fallback. Default ceiling remains one node per region;
  any higher configured ceiling requires explicit review and spend approval.
- Keep minimum nodes zero, the 120-second idle scale-down policy and exactly one
  instance per command job. Start/host restart must not introduce paid warm nodes.
- Support N regions without a fixed two-region limit, initially within one explicit
  tenant/subscription. Cross-subscription federation is not part of this work.
- Global and regional limits apply across all workloads using the same compute.
- Per-region job timeout is a whole number from 1 to 120 minutes. The effective
  timeout drives both Azure ML `limits.timeout` and runner `--run-timeout`.
- Start requires explicit operator spend approval, a valid prepared release, live
  regional price/quota/SKU checks and private connectivity. Quota and SKU availability
  are not predictions of Spot capacity. A retail threshold is not a billing cap.
- Persist the workspace and client-chosen remote job name before submission.
  An ambiguous response is reconciled by reading that identity, never blind resubmission.
- Never have two live attempts of the same studio job. Never migrate a completed
  job and regenerate its already-produced output.
- Stop first fences submissions, attempts every configured region independently,
  and reports success only after fresh reads prove release and terminal jobs.
- Emergency release must not require App Service, Python, Docker, prepared assets,
  retail-price availability or a working Terraform installation.
- All mutations validate explicit tenant/subscription, resource IDs, ownership and
  allowed scope. Cached receipts are not trusted merely because a file exists.
- Keep model SHA-256/size checks, positive integer model versions, immutable image
  digests, managed inputs/outputs, source pins and licence provenance.
- Keep local ComfyUI generation, existing WAN modes, library/archive behavior and
  historical job recovery unless a specific compatibility change is approved.

## 4. Approved decisions

The user approved these boundaries on 2026-10-21. They become implemented only when
the corresponding phase's exit gate is satisfied.

| Decision | Approved approach | Why / review gate |
| --- | --- | --- |
| Prepare | Build and promote in GitHub Actions; App Service verifies/selects approved releases | No Docker, source checkout, operator venv or large model downloads in the web host |
| Foundation ownership | Terraform owns durable regional services, identities/RBAC and the host | One infrastructure authority; existing landing-zone services remain references |
| Operational compute ownership | .NET owns ephemeral AML compute and optional owned NAT/PIP after a verified-OFF migration out of Terraform ownership | Avoid running Terraform in the server or requiring state repair after every runtime Stop |
| Optional managed NAT | Only with explicit consent and narrowly scoped permission on the dedicated GPU subnet, preserving all unrelated properties with concurrency checks | Attaching NAT requires a subnet write; do not falsely claim this mode needs no customer-subnet permissions |
| Hosted administration | `StudioAdmin` authorization, audited operations and narrow resource-scoped grants; ordinary creator endpoints cannot administer compute/releases/settings | The existing portal identity is intentionally job-submit-only |
| Runtime state | Reuse private storage and existing .NET services for durable operation/attempt records and a submission fence | Do not introduce a workflow engine or another database without demonstrated need |
| Release identity | Separate immutable build identity from regional bindings and mutable runtime settings | Changing timeout, concurrency or priority must not rebuild images or recopy weights |
| Queue routing | Observe queued jobs; wait, cancel and prove safe termination before moving | No AML API guarantees that a queued job will obtain Spot capacity |
| Experimental profiles | Preserve them as explicitly experimental/manual build recipes initially; remove only unused packaging, not unapproved features | H3/LTX are not production WAN dependencies, but are not automatically dead |
| Local tools | Document manual procedures; keep only workflow-used single-purpose entry points and independent recovery | No TUI and no replacement lifecycle multiplexer |

If the ownership handoff or permissions proposal is rejected, revise this plan before
implementing hosted Start/Stop. Do not quietly choose a second state authority.

## 5. Target contracts and layout

Use existing .NET layers and helpers. Add files/projects only when they implement an
actual boundary; no generic provider framework, local-tool framework or plugin host.

Proposed layout:

```text
src\                              .NET application and hosted operational control
web\                              React creator and StudioAdmin interfaces
infra\modules\region\             durable services for each configured region
contracts\                        versioned JSON contracts and small shared fixtures
build\gpu\                        release preparation code, locks and pinned inputs
trainers\                         trainer worker implementations and image definitions
scripts\ci\                       single-purpose entry points actually used by Actions
scripts\recovery\                  single-purpose emergency release used by recovery Actions
docs\operations\                  manual setup, migration, recovery and local-run guides
docs\plans\                       this phased tracker
```

The repository's current `azureml\` operator directory can disappear after its
contents are migrated. The mounted job payload may still contain
`azureml/run_workflow_job.py`: that is the upstream runtime namespace, not a second
copy of our operator. Do not vendor another full ComfyUI checkout just to rename it.

Required contracts:

| Contract | Required content and consumers |
| --- | --- |
| Foundation receipt v2 | Explicit tenant/subscription/deployment, per-region workspace/resource IDs, names, identities, owner tags, landing-zone references, egress mode, approved SKU/node ceilings, timeout policy and host binding; consumed by deployment, .NET and emergency recovery |
| Build manifest | Source commit, runtime-file selection and file hashes, runtime patch/input hashes, dependency/base-image identities, model SHA-256/size/source/licence records, image digest and artifact identity; no customer IDs or build-machine paths |
| Regional release binding | Build identity plus exact workspace environment/model versions, code/model URIs, registry digest, scope/ownership, verification result and publication identity; published only after all regional checks succeed |
| Operational settings | Global admission limit, per-workspace limits, effective job minutes, region priority and queue-wait policy, with version/concurrency control and audit history |
| Job/operation record | Stable studio job ID, region/workspace, remote attempt ID, effective settings/release, submission intent/outcome, timestamps, active attempt and terminal/output state |

Persist operational metadata where GPU workloads and creator uploads cannot overwrite
it. Specify the smallest required storage grants in Phase 1; release write access
stays with the deployment/publishing identity, not the ordinary web host.

Read legacy receipts/job records through explicit migration adapters only where needed
for recovery. Never guess a legacy job's region from the currently selected workspace.

## 6. Phase tracker

Mark a phase complete only when its exit gate is met. Approval of the plan is not
evidence that a phase has been implemented or live-validated.

| Phase | Focus | Prerequisites | Status |
| --- | --- | --- | --- |
| P0 | Review and baseline | None | Complete |
| P1 | Contracts, ownership and safety fixtures | P0 approval | Complete |
| P2 | Minimal GPU payload and build-only preparation | P1 | Complete (offline/source checks; real image probe remains a publication gate) |
| P3 | Regional Terraform foundation and state migration | P1 | Complete (offline/mocked; no customer migration or deployment executed) |
| P4 | Release/deployment/recovery Actions | P2, P3 | Complete (offline/mocked; no workflow dispatched or Azure publication executed) |
| P5 | Hosted .NET operational control | P1, P3, P4 contracts | Complete (offline/mocked; no live Azure or Entra role validation) |
| P6 | Regional generation, routing and recovery | P4, P5 | Complete locally |
| P7 | StudioAdmin UI and live settings | P5; routing controls require P6 | Complete locally |
| P8 | Remote training and hosted CPU media support | P2, P4, P5, P6 | Complete locally |
| P9 | Final script/dependency removal and manual documentation | P4-P8 exit gates | Complete locally (legacy Status/Stop retained) |
| P10 | Integrated verification and optional live acceptance | P9 | Offline complete; live acceptance pending |

P2 and P3 can proceed independently after contracts are approved. UI work can use
contract fixtures after P5, but must not imply unavailable routing is operational.
Parallel execution is an implementation choice, not authorization to launch agents.

### P0 - Review and preserve the baseline

- [x] Stash the existing changes, fast-forward `main`, create the new branch and restore the exact patch.
- [x] Retain the recovery stash; do not commit or publish existing work.
- [x] Save this complete phased plan in the repository.
- [x] User reviews the proposed ownership, permission, compatibility and workload boundaries.
- [x] After approval, record a fresh baseline of relevant checks and map accepted work to GitHub tracking issues.

Fresh baseline: .NET 347 passed / 2 skipped; web build and 40 tests passed;
isolated Terraform fmt/validate and 45 mocked runs passed; Python 33 tests run
with one optional upstream-fixture skip; PowerShell offline controls passed.
Terraform used cached providers in a separate current-source fixture without
customer tfvars/state. Tools: .NET SDK 11.0.100-rc.2 with net10.0/runtime 10.0.13,
Terraform 1.16.6, Python 3.12.10, Node 24.11.0 and npm 11.12.1.

Exit gate: explicit plan approval. This document alone is not deployment or GPU-spend approval.

### P1 - Define executable contracts and deletion evidence

- [x] Finalize the five contracts above with validation rules and small golden fixtures shared by consumers.
- [x] Define operational compute ownership, exact verified-OFF migration mapping and rollback prerequisites.
- [x] Inventory each existing script/helper/test against its real caller and destination; do not treat test-only references as production use.
- [x] Define `StudioAdmin` authorization and a permission matrix for creator, operator, publisher, deployer, job and recovery identities.
- [x] Identify conditional subnet read/join/association permissions from documented Azure behavior; no guessed role actions or provider fields.
- [x] Define how legacy receipts, jobs, archive entries and output locations retain their original workspace/storage binding.
- [x] Separate mutable timeouts/limits from build hashes. Capture effective settings per attempt; updates affect future attempts, not an already-created job.
- [x] Define single-coordinator behavior and durable fencing for the initial host topology; no unsupported multi-instance safety claims.

Exit gate: fixtures reject wrong scope/ownership, malformed URIs, unsafe ceilings and
stale writes; a caller-to-replacement checklist exists for every planned deletion.

P1 implementation: typed readers/rules and schema exporter in
`src\VideoSafetyStudio.Application\Operations\`; five generated schemas and shared
fixtures in `contracts\`; contextual regression cases in `StudioContractTests`;
single-purpose structural fixture check wired into existing CI.
`contracts\README.md` records content identity, callers/replacements and coordination;
`docs\operations\ownership-and-permissions.md` records role/scope evidence and the
verified-OFF handoff. Permission names were checked through read-only provider
metadata, not by creating resources or grants. Existing production lifecycle and
submission paths are unchanged; storage fencing and legacy adapters are later phases.

Verification: 74 contract cases; full .NET suite 421 passed / 2 skipped; five shared
schemas also accepted by the native PowerShell validator. Live Azure behavior,
state migration, Docker/GPU execution and role-assignment sufficiency remain unverified.

### P2 - Extract build preparation and minimize the GPU payload

- [x] Move preparation into single-purpose build tools and the shared model/download/licence/tracing implementation into `build\gpu\`, with its own hash-locked build dependencies. Keep forwarding imports for the transitional publisher.
- [x] Split local artifact construction from Azure publication. The build stage emits a portable manifest and deterministic code bundle.
- [x] Select upstream runtime files explicitly: the WAN runner, its three helpers, model-path YAML, required ComfyUI source and licence/notice files.
- [x] Remove upstream portal/submission/configuration modules from the new WAN payload; never copy customer config, local receipts, `.git`, caches or weights into code.
- [x] Extract H3 revision/build metadata from the web module. Retain any LTX no-download safety patch required by documented experimental execution.
- [x] Preserve the source pin, reviewed patch identity, legitimate Comfy package directories and model cache reuse.
- [x] Pin/lock GPU image dependencies appropriately. The operator `uv.lock` is not a lock for the upstream GPU image's package ranges.
- [x] Keep model hashes and licences independently reusable when only code or operational settings change.
- [x] Implement the CPU-probe gate against the actual staged source for all served .NET-rendered WAN modes. Real image execution still needs separate approval and working Docker.
- [x] Add replacement payload/import-closure, model-integrity, no-download and executable staged-import checks. Retain legacy factory controls only while their transitional publication path remains; remove them with that path in P9.

Exit gate: offline tests use tiny assets; the production WAN bundle excludes all
operator/submission/UI modules; a separately approved image build can run the staged
CPU probe without network/GPU. Do not download real weights or build images now.

P2 implementation (2026-10-23):

- `build\gpu\prepare.py` exports committed source without LFS smudge/lazy fetching,
  normalizes the archive/build inputs and stages every served WAN probe workflow.
  The exact upstream payload is 621 files / 38,922,311 bytes; file count is evidence,
  not a hardcoded future threshold. Disabled custom nodes, tests and blueprint collections
  are excluded, while legitimate nested packages and empty package markers remain.
- `build\gpu\models.py` is the shared model implementation. The model manifest and
  tracing helper moved alongside it; the legacy publisher keeps small forwarding imports.
  No new build tool imports an Azure SDK or reads customer configuration.
- Runtime dependencies resolve to 84 exact packages and SHA-256 hashes for Linux
  x86_64 / CPython 3.11. Build tooling has a separate six-package tracing lock.
  Locks contain no registry URLs or credentials. The same approved feed resolved
  blake3 through source metadata after its wheel metadata returned 404; image
  installation still requires binary wheels.
- `build\gpu\verify.py` seals a manifest only after staged-input checks and the
  network/GPU-disabled CPU probe, then rehashes inputs. Incomplete model/licence
  snapshots and failed/changed probes cannot produce a manifest.
- The pre-deployment build contract now includes `buildInputs`; code files may
  legitimately be empty, but models/licences/build inputs cannot. Golden fixtures
  and schemas were regenerated and cross-language identity checks pass.
- Existing CI runs the new build-boundary checks, but no workflow was launched.
  Server Docker context excludes build tools. Lifecycle/publication remains
  transitional; the new minimal payload has not replaced deployed assets.

P2 commands/results:

| Command | Result |
| --- | --- |
| `python -m unittest discover -s build\gpu -p 'test_*.py' -v` with `WAN_STUDIO_P2_UPSTREAM` set to the pinned Git source fixture | 25 passed, including exact source export and execution of the patched LTX missing-model guard |
| `dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore` | 423 passed, 2 existing environment-dependent skips |
| `python azureml\test_controls.py --scratch <new-empty-directory>` | 33 run, 1 optional old upstream-factory fixture skip |
| `pwsh -NoProfile -File azureml\Test-Controls.ps1 -PowerShellOnly` | PASS; mocked lifecycle, no Azure mutation |
| `pwsh -NoProfile -File scripts\ci\Test-StudioContracts.ps1` and `python scripts\ci\test_build_identity.py` | PASS |
| In `docs-site\`: `npm run build` | PASS |

Tools: Python 3.12.10 and uv 0.9.5; resolve local executable paths with
`Get-Command python,uv,dotnet,pwsh -CommandType Application`.
.NET uses the installed preview 11.0.100-rc.2 SDK targeting net10.0.
The new build/contract files pass the private-scope/profile/feed/retired-key scan.
P2-scoped whitespace checks pass; carried-forward PowerShell whitespace warnings
remain untouched. Details and reproducible lock-generation/source-fixture commands
are in `build\gpu\README.md`.

Limitations: Docker was not running; no image build/pull, real container CPU probe,
Linux/GPU run, model-weight download, registry publication or Azure operation was
performed. Full offline re-resolution of the generated lock was not supported by
the local uv metadata cache (torch absent); actual image dependency/import compatibility
must pass the real CPU probe before publication. The new evidence is offline/source
validation, not a claim that the port has inherited old live Azure acceptance.

### P3 - Regional infrastructure and explicit state handoff

- [x] Introduce one Terraform root with a `regions` map and a regional module for durable owned resources.
- [x] Require each region's dedicated GPU/PE subnets, DNS/logging inputs, customer-NSG choice and explicit egress policy.
- [x] Keep one shared App Service host. Require customer-provided routing/DNS connectivity from it and private runners to every selected region; do not create shared peering, DNS links or landing-zone routes automatically.
- [x] Preserve the existing region's exact names and IDs in the reviewed migration mapping; region-derived naming applies to new regions.
- [x] Export receipt v2 and per-region deployment policy, including `max_nodes` (default one) and `max_job_minutes` (1-120).
- [x] Keep compute off by default and keep operational compute/NAT/PIP out of the new durable foundation's managed resource set.
- [x] Gate handoff on compute/owned NAT/PIP absence, detached expected subnet, stopped existing host and terminal jobs; unknown/paginated evidence fails closed.
- [x] Implement exact durable state-address assessment, not automatic migration. Terraform `moved` addresses are static; no variable-interpolated region keys are generated.
- [x] Require an explicit version-4 state backup and validate every address/ID before proposing exact `state mv` or verified-absent `state rm` commands. The tool never executes them.
- [x] Reject blanket ignores, blind forgetting, forced destroy or subnet adoption. Saved-plan validation rejects unexpected deletions/replacements and forbidden owners.
- [x] Add scoped operator/publisher/control grants. No runtime write access to DNS, logging or customer-managed NSGs.
- [x] Add one/two-region native mocks, strict cross-layer receipt fixtures and synthetic legacy-state tests.

Exit gate: offline contract/mapping checks pass. A real existing deployment is not
declared migration-safe until its separately approved saved plan proves no unintended
replacement or landing-zone mutation. Mocked tests cannot prove that live result.

P3 implementation (2026-10-23):

- `infra\modules\region\` owns durable regional services; the root selects N
  regions and keeps the optional shared host's addresses stable. `legacy_region`
  retains the original naming hash and remains `control_region`; new regions use
  the region-derived hash. Customer VNet/subnets/DNS/logging/NSGs remain references.
- `studio` is strict schema v2; shared host metadata is a separate `portal` output.
  Compute/egress IDs are prospective policy only. `compute_enabled=true` is
  rejected and no managed compute/NAT/PIP/NAT-association resources exist.
- New private control containers and explicit resource-scoped roles separate
  operators, publishing, jobs and hosted verification. The legacy GPU blob grant
  is intentionally narrowed from account to run-container scope: that one role's
  scope/UUID replacement is reviewed, while unchanged scoped UUIDs and durable IDs
  remain stable. Live job/log/output access after this reduction needs later acceptance.
- `infra\migration.py` is read-only. Static assessment emits no state commands.
  Explicit live mode uses only CLI context/token reads and ARM GETs; scope,
  ownership, absence, host/subnet state and complete job pagination must pass
  before exact state commands are proposed. A separate `--plan-json` gate rejects
  unmoved-root deletes, durable replacements and all but the exact intended GPU
  role replacement. It does not authorize or execute an apply.
- `infra\README.md` records backup, backend lineage/serial, maintenance, state
  mapping, provider-specific composite IDs, rollback and live-validation gates.
  The example is regional and safely compute-disabled. Real customer state/config
  files were not rewritten, moved or refreshed; their checksums are checked separately.
- Legacy Deploy/Prepare/Portal/Publish/Start and Terraform reconciliation are
  blocked against this new root. Cached v1 Stop still attempts native release
  before surfacing the explicit migration requirement. This is not an operational
  multi-region deployment until P4/P5/P6 are complete.

P3 verification:

| Command / boundary | Result |
| --- | --- |
| Isolated copied-source `terraform init -backend=false` with cached providers; `fmt -check -recursive`, `validate`, `test` | PASS; 55 native mocked runs |
| `pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1` | PASS; actual one/two-region plans match strict shared schemas and the positive resource-owner allowlist |
| `python -m unittest discover -s infra -p 'test_*.py' -v` | 23 passed; scope/ownership/absence/paging/state/host/saved-plan rejection checks |
| `dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore` | 425 passed, 2 existing environment-dependent skips; includes independent typed consumption of exported Terraform receipts |
| `python azureml\test_controls.py --scratch <new-empty-directory>` | 33 run, 1 optional old upstream-factory fixture skip |
| `pwsh -NoProfile -File azureml\Test-Controls.ps1 -PowerShellOnly` | PASS, using frozen v1 inputs/receipts and mocks |
| In `docs-site\`: `npm run build` | PASS |

Tooling remains Terraform 1.16.6 / AzureRM 5.6.0 / AzAPI 2.12.0, Python 3.12.10,
PowerShell 7.6.7 and .NET SDK 11.0.100-rc.2 targeting net10.0. No application package
dependencies, provider versions or provider locks changed in P3. The touched PowerShell tests' carried-forward
line-ending whitespace was normalized. Full change whitespace and new infrastructure
portability scans are checked before handoff.

No real Azure reads/mutations, backend/state commands, deployments, GPU activity,
image/weight acquisition, workflow launches, commits or pushes were performed.
Live no-replacement migration, new IAM sufficiency (including AML logs/artifacts),
inter-region private connectivity and Linux/macOS operation remain unverified.
P2's real Docker CPU-image probe remains pending separate approval/readiness.

### P4 - Implement single-purpose Actions and regional promotion

- [x] Keep ordinary PR CI non-deploying: .NET/web/build-tool tests, mocked Terraform checks and appropriate image/package checks only.
- [x] Protect private runners from untrusted PR/fork code. Pin action revisions and keep permissions job-scoped.
- [x] Define and enforce the private-runner/DNS prerequisite; owners must establish connectivity before an approved workflow run. Public GitHub runners are not a fallback.
- [x] Require approved federated credentials/protected environments for Azure writes; owner setup remains manual, with no tenant administration in routine CI.
- [x] Build server, GPU and trainer artifacts independently. Artifact identity excludes customer region and mutable job timeout.
- [x] Promote the same verified GPU code/model/image content into selected regions, registering positive-integer model versions and exact bindings.
- [x] Preserve verified model snapshots; create-only paths, remote byte/hash verification and unexpected-content rejection.
- [x] Publish a regional binding only after completed registration/digest/blob verification; partial publication cannot create a binding.
- [x] Deploy a previously approved server digest through the private registry without rebuilding or arming.
- [x] Separate saved-plan review/application; bind source/run/config/backend/lock/hashes and validate output before caching its receipt.
- [x] Provide protected manual recovery using the independently runnable `scripts\recovery\Stop-Studio.ps1`.
- [x] Use one approved feed and secret mounts; keep credentials out of images/manifests/logs/locks.
- [x] Do not allocate GPU compute, generate, train or run a GPU smoke test from these entry points.

Proposed script purposes, only when repository-specific logic warrants a script:

| Purpose | Entry point / caller |
| --- | --- |
| Build a GPU release | Build tool under `build\gpu\`, called by the GPU release workflow |
| Publish one regional binding | Single-purpose entry point under `scripts\ci\`, called by a regional promotion matrix |
| Validate/apply a saved foundation plan | Single-purpose deployment steps or scripts, called by protected infrastructure Actions |
| Deploy one approved server artifact | Single-purpose deployment step or script, not a build/lifecycle multiplexer |
| Independently release owned compute/egress | `scripts\recovery\Stop-Studio.ps1`, called by protected recovery Actions and the manual runbook |
| Export/check mocked contracts | Retain/adapt `infra\tests\Export-MockedContracts.ps1` as a CI test helper |

Prefer native action/CLI steps for trivial commands. Do not replace one monolith
with many one-line wrappers, duplicate PowerShell/bash implementations or a generic
`-Action` script framework.

Exit gate: workflows use explicit input/output contracts, safe permissions and
fixture-tested scripts. Cloud publishing/deployment remains unexecuted until approved.

P4 implementation evidence:

- `release.yml`, `promote.yml`, `foundation.yml`, `deploy-server.yml` and
  `recover.yml` are protected default-branch manual entry points. PR CI stays on
  public runners without Azure credentials; private context uses isolated
  federated CLI sessions and masked configuration. No workflows were dispatched.
- CI validates with the shared .NET Application contracts through a validation-only
  tool. Native recovery has independent guards against the same synthetic receipts
  and needs no server, Python, Docker, Terraform or fresh output.
- GPU image staging is explicitly unready: obtain the registry manifest digest,
  pull it locally, CPU-probe/seal, then publish. Region bindings are written last.
  Model snapshot/registration names are independent of code/image changes.
  Private saved plans/config/state never become public GitHub artifacts.
- Publisher storage verification adds only `Microsoft.Storage/storageAccounts/read`
  at the owned account. No new job/compute/tenant permissions are granted to it.
- Checks: 20 pipeline cases; 36 native recovery/input assertions; 55 isolated native
  Terraform cases plus fmt/init/validate/export drift; 23 migration cases;
  .NET 427 passed / 2 existing skips; GPU builder 25 run / 1 optional source skip;
  retained Python 33 run / 1 optional upstream skip and PowerShell controls.
  Actionlint, documentation build/prose and whitespace/portability checks pass.
  The isolated locked pipeline environment has 21 compatible packages.
- Customer state/config remain checksum-identical. No Azure reads/mutations,
  state migration, image/weight acquisition, GPU activity, commits or pushes.
  New workflows/private runners/federation, Linux/container execution, live IAM
  and actual Azure publication remain unverified. Original WAN live evidence is
  not evidence for this refactor.
- P5's durable operation fence/serialization is not implemented: quiesce competing
  writers before P4 recovery. It stops and leaves the shared host stopped.
  Environment/reviewer/runner/identity/backend setup is an owner prerequisite,
  not something this implementation silently performs.

Exact portable commands, inputs, permissions and limitations:
[`docs/operations/private-pipelines.md`](../operations/private-pipelines.md).

### P5 - Port operational lifecycle into the .NET server

- [x] Put use cases in Application and Azure/storage adapters in Infrastructure; Server endpoints remain thin and `StudioAdmin`-only.
- [x] Implement regional status/readiness, approved release selection, Start and Stop using managed identity and native Azure APIs.
- [x] Keep release selection read/verify-only for the host; it must not acquire source/models or register arbitrary replacement assets.
- [x] Validate scoped receipts and release bindings before mutations, including stale/mismatched ownership and policy.
- [x] Preserve price, quota, regional SKU restrictions, private connectivity and live compute-contract checks on Start.
- [x] Persist operation intent and an external submission fence; serialize Start/Stop/submission handoffs per workspace.
- [x] Replace App Service setting changes/restarts as the arming mechanism with validated operational state read by the submission guard.
- [x] Recover interrupted operations after a host restart. Unknown outcomes remain fenced and visible until reconciled.
- [x] Make Stop attempt every region even if another region fails; cancel jobs, delete exact owned compute with underlying resources, verify absence, then remove only owned egress.
- [x] Prove all relevant jobs terminal before reporting successful release. Cancellation API errors are not success evidence.
- [x] Keep recovery independent of the host and Terraform, with the same scope/ownership/absence expectations and aggregated failures.
- [x] Emit audited actor/scope/approval/result records and OTel events without secrets, signed input URLs or raw credentials.

Exit gate: .NET and recovery tests cover interrupted Start, repeated Stop, foreign
resources, partial regional failure, ambiguous submissions and host unavailability.
The old operator path is not deleted before parity exists.

P5 implementation evidence (2026-10-23):

- `Application\Operations` owns use cases (`StudioLifecycle`, `StudioPolicy`, `StudioArmedGate`, `StudioOperationalState`);
  `Infrastructure\Operations` owns native ARM, private-blob control state, retail-price and DNS/TCP adapters;
  `Server\Endpoints\StudioAdminEndpoints` is a thin `/api/studio/*` adapter requiring the `StudioAdmin` app role
  (never implied by `Auth:Mode=None`; admins still hold `VideoCreator` until P7 splits the surfaces).
- The host receives `Studio__Foundation` (the strict v2 receipt) and an independently derived `Studio__Selection`
  from Terraform; it re-derives names/ownership/policy and refuses a mismatch. The exact Terraform-produced selection is
  a mocked fixture consumed by .NET tests.
- Durable fence: `operations\state.json` in control-region `wan-control`, ETag-conditional, strict schema. Start writes
  `starting` before any check or mutation; only `armed` opens a region. Restart converts `starting`/`stopping` and an
  armed record without a contract-valid live compute to `unknown` (fenced, visible). A stale or missing snapshot (30 s)
  closes the submission gate; a release whose durable write fails is masked until the write is retried.
- `AzureMl__Armed` is ignored when a foundation is configured; it remains the arming switch only for local development.
- Start verifies approvals, selected release (manifest, regional binding, live environment/model), workspace/subnet
  contract, private connectivity, no active jobs, retail PAYG threshold, SKU restrictions and Spot/A100 quota, then
  creates owned egress (when consented) and one private Spot compute and re-verifies it. Failure after the first write
  releases it; unverified release stays `unknown`.
- Stop fences first, cancels in-flight Start, attempts every region, cancels jobs, deletes exact owned compute with
  `underlyingResourceAction=Delete`, proves absence and terminal jobs, then removes only owned egress using the subnet ETag.
- The P4 recovery script remains independent. After an emergency release, a host restart reconciles `armed` to `unknown`;
  run Stop to record the verified Off state.
- Release selection is read/verify-only (no registration, upload or asset acquisition). The host's flat `AzureMl__Release__*`
  pointers are unchanged until P6; selection/binding is stored and verified but not yet consumed for job specs.
- Checks: 34 lifecycle/state/gate cases (including 11 pre-mutation refusals), 2 admin-role cases, full .NET 463 passed / 2 existing skips;
  55 native Terraform mocks (including host settings and the exported selection fixture); 21 pipeline cases (P4 model-URI normalization corrected); 23 migration, 25 GPU-build and 33 recovery-script checks unchanged.
  No Azure reads or mutations, workflow launches, GPU use, commits or pushes. Live managed identity/IAM, private routing,
  ARM/AML response shapes and real Entra `StudioAdmin` role assignment remain unverified.
### P6 - Regional routing, admission and durable generation recovery

- [x] Replace flat workspace configuration with validated region/workspace bindings and region-specific clients/stores.
- [x] Preserve one-region behavior and explicitly migrate legacy job/library/archive records to their original storage binding.
- [x] Persist submission intent and remote identity before creating an attempt; reconcile uncertain acceptance by GET, never by automatic repeated creation.
- [x] Enforce global and per-workspace admission limits against deployment ceilings; reducing a limit does not terminate existing work.
- [x] Rank only armed, prepared, policy-eligible regions using configured priority and observed headroom; report the reason a region is ineligible.
- [x] Treat quota/SKU checks as eligibility, not live capacity. Spike Spot Placement Score only as an optional advisory ranking input; AML applicability and rate limits remain unverified.
- [x] Implement the approved wait-then-move policy for jobs still queued/not started, with a bounded wait and no-progress observation.
- [x] Preserve the previously proposed 10-minute default queue-wait threshold; expose its effective value and validate configured alternatives.
- [x] Before moving, re-read the job; if it completed, collect its output; if execution started, do not treat it as safely unstarted queue work.
- [x] Cancel once and require fresh terminal proof before another attempt. Cancellation errors or unknown state block movement and surface an operator action.
- [x] Permit at most one attempt per eligible region for this queue-move cycle. Exhaustion leaves one clearly tracked queued/stuck result; no retry loop or parallel hedging.
- [x] Snapshot destination-region timeout/settings for each attempt and apply the same timeout to Azure ML and the runner.
- [x] Read output and serve media from the successful attempt's regional store; never infer region from the user's current settings.
- [x] Resume safely through restarts around every submission/cancellation/terminal handoff boundary.

Exit gate: race/restart tests demonstrate no duplicate live attempts, no replay of a
completed job, correct regional output, enforced limits and no dedicated/SKU fallback.
Document that multiple sequential attempts increase total elapsed time and possible
cost; a per-attempt timeout is not a global time or dollar budget.

Evidence: regional router, admission, attempt ledger and recovery (`RegionalRouting.cs`, `RegionalAzureMlBackend.cs`, `HostedGeneration.cs`); 24 routing/DI tests; full .NET 487 passed / 2 existing skips, 0 warnings. Spot Placement Score deferred as documented. No Azure access, GPU, commits or pushes; live behavior unverified.

### P7 - StudioAdmin UI and runtime settings

- [x] Add administration UI using the existing React/API patterns; do not redesign Create or Library.
- [x] Expose per-region readiness, approved release, node/job state, last error and partial-operation status.
- [x] Provide explicit Start spend confirmation, Stop-all, release selection and global/per-region settings.
- [x] Configure per-region effective job timeout within its deployed policy and the absolute 120-minute guard.
- [x] Show configured policy and effective values; reject invalid requests with actionable errors rather than silently weakening a ceiling.
- [x] Validate `StudioAdmin` server-side on every administration action, independently of hidden UI controls and `VideoCreator` access.
- [x] Update sign-in/session role handling so an administrator can reach administration without unintentionally acquiring creator permissions; test creator-only, admin-only, dual-role and unauthorized accounts.
- [x] Audit old/new values, actor and operation outcome; use version checks for concurrent updates and existing SSE for changes.
- [x] Show build/promotion as deployment activity, not a misleading in-process Prepare/Publish operation.

Exit gate: authorization, stale-update, timeout-flow and UI tests pass; ordinary
creators cannot mutate operational state.

Evidence: roles are separate session claims (`studio_creator`/`studio_admin`); non-creator sessions reach only `/api/session`, `/api/events` (`studio` notices only) and `/api/studio/*`. `GET/PUT /api/studio/settings` (`StudioLifecycle.Settings.cs`) validates against deployed ceilings with named limits, version-checks, audits old/requested values and every rejection, refreshes the router source and publishes a content-free `studio` event. React `features\admin` (region cards, Start spend/network confirmations, Stop all, release select, settings form with stale-edit protection). Tests: 4 settings cases, a creator/admin/dual/unauthorized route matrix, admin-only SSE filtering, 12 web-form cases; full .NET 493 passed / 2 existing skips, 0 warnings; web 44 passed, tsc and build clean; docs-site build and prose check clean. Timeout flow is covered by settings update -> router source refresh here plus the P6 per-attempt timeout snapshot tests. No Azure access, GPU, commits or pushes; live Entra role assignment and managed-identity behavior unverified. Sessions issued before this change must sign in again.

### P8 - Complete the worker/CPU hosting boundary

- [x] Add an Azure ML implementation of the existing training compute abstraction; do not rewrite the trainer SDK or PyTorch implementation.
- [x] Stage immutable COCO dataset inputs and outputs under approved regional scope, and submit the approved trainer image with managed identity.
- [x] Reuse admission, durable remote identity, cancellation/recovery and explicit runtime/node limits; training must not bypass generation's capacity controls.
- [x] Keep trainer image builds, initial weights, source pins, dependency installation and licence collection in CI/CD.
- [x] Publish approved trainer metadata/image bindings for the .NET registry without copying Python training implementations or their dependencies into the server image.
- [x] Retain `trainers\_template` and customer trainer metadata as a documented extension contract, not an unneeded new framework.
- [x] Package required CPU `ffmpeg`/`ffprobe` tooling in the server image for bounded media import/extraction, retaining file-only protocols and subprocess isolation.
- [x] Keep the feature flag off in hosted deployments until remote execution and CPU media readiness are demonstrated.
- [x] Review the local Docker adapter after remote parity: retain only for an explicitly supported .NET local-training mode; otherwise document direct manual container runs and remove the unused adapter.

Exit gate: offline tests cover job scope, immutable data, artifact collection, limits
and recovery; image checks prove required executables are packaged. Live training
and GPU validation require separate spend approval.

Evidence: `TrainerBinding` contract (`contracts\trainer.schema.json`, strict reader, shared .NET validator mode); `publish_trainer.py` and `promote.yml component: yolox` promote the sealed trainer digest and write the create-only regional binding last; `TrainerBindings` hosted registry; `AzureMlTrainingCompute` (admission slot first, create-only staged data with hash manifest, identity saved before the single create, GET-only reconciliation, restart follows the saved job, detach vs run-limit cancel with proof, region job limit as timeout) behind `IDurableComputeTarget`; `TrainingService.RecoverAsync` and `TrainingRecoveryService`; ffmpeg/ffprobe installed in the server image and verified by `build_image.py`. The local `DockerComputeTarget` is retained as the documented local mode. Hosted flag stays off (Terraform validation unchanged). Tests: 18 remote-training cases (+ contract schema), 26 pipeline cases (3 trainer promotion, 2 image packaging), full .NET 511 passed / 2 existing skips, web unchanged, 55 Terraform mocks, 36 recovery, GPU/infra/control checks unchanged, docs-site build and prose check clean. No Azure access, image build or weight download, GPU or training, commits or pushes. Live Azure ML job behavior, trainer GPU/CUDA validity, registry copy and apt ffmpeg in the real image build are unverified.

#

Evidence: removed `upload-wan-code.sh`, `upload-wan-models.sh`, `Initialize-PortalAuth.ps1` (now `docs\operations\entra-bootstrap.md`, which also adds the `StudioAdmin` role), `Initialize-LocalDev.ps1` (hand-written `appsettings.Development.json`), `Install-GitHooks.ps1`, `Update-Docs.ps1` and the pre-commit hook that ran it (it would have blocked commits), and the whole `azureml\` Python folder (runtime, config, cost_guard, models, diagnostics, telemetry, OTLP requirements, upstream cost patch, uv lock, both Python test suites, `Test-Controls.ps1`). Replacement coverage: .NET `AzureMlGuard`/routing/lifecycle tests for the cost guard and submission, `build\gpu` and `scripts\ci` for preparation, promotion and build identity, `scripts\recovery` for emergency release, `scripts\Test-LegacyLifecycle.ps1` for the retained legacy Stop (the PowerShell half of the old `Test-Controls.ps1`, moved unchanged apart from dropping the Python half). `Invoke-WanSafetyStudio.ps1` now accepts only Status/Stop/Check; the retired actions are rejected by parameter validation. Reference-only GGUF/upstream workflows moved to `docs\reference-workflows`; `.dockerignore`, `.gitignore` and CI updated; the build-tool lock `scripts\ci\requirements.lock` is kept for its real CI consumers; a `build\gpu` test now asserts `azureml\` stays absent. Docs (README, root docs, docs-site deployment and developer pages, AGENTS, contracts inventory) rewritten for the new boundaries; stale-reference scan leaves only intentional retirement notes. Checks: .NET 511 passed / 2 skipped, pipeline 26, build identity, `build\gpu` 25, infra, trainers, 55 Terraform mocks, 36 recovery, contract fixtures, legacy lifecycle, actionlint, web 44, docs-site prose check and build. Removed files were backed up to the session `files\p9-removed` folder (tracked ones also remain in git history). Not run: the retired Python suites (deleted), any Azure access, image builds, GPU or deployment. Live behavior of the whole refactor remains unverified.

## P9 - Remove superseded scripts, dependencies and documentation

- [ ] Remove the old lifecycle multiplexer after all supported operations have a tested hosted, workflow or manual destination. (User decision 2026-10-26: retained as a frozen legacy Status/Stop/Check tool for an un-migrated v1 deployment; delete it and `scripts\Test-LegacyLifecycle.ps1` together after the v1 to v2 migration is verified.)
- [x] Remove duplicate shell upload implementations after promotion parity and documentation updates.
- [x] Move remaining manual setup/local-run procedures into documentation rather than retaining an unowned tool collection.
- [x] Remove legacy Python operational guards, upstream submission/UI patch hunks and obsolete tests only after replacement coverage is mapped.
- [x] Retain/move the build-tool Python lock and dependencies that still have real CI consumers; do not delete them merely because the host is .NET.
- [x] Remove unused diagnostic/exporter helpers and dependencies after confirming their build/worker callers, retaining appropriate telemetry.
- [x] Exclude reference-only workflows from published server content; keep references in docs only if still useful.
- [x] Update README, configuration/domain/deployment docs, AGENTS guidance, `.dockerignore`, csproj content rules and all workflow paths.
- [x] Remove references to the retired Python portal and stale architecture claims; mark old design proposals as historical or replace them.
- [x] Scan tracked source/docs/workflows for dead entry points, missing files, obsolete environment keys, unintended operator files in images/payloads and nonportable deployment values.

Script disposition:

| Existing script | Intended disposition |
| --- | --- |
| `scripts\Invoke-WanSafetyStudio.ps1` | Retire after splitting build/deployment Actions, hosted lifecycle and independent recovery |
| `scripts\upload-wan-code.sh`, `scripts\upload-wan-models.sh` | Fold verified behavior into the single release pipeline; remove duplicate manual publication paths |
| `scripts\Initialize-PortalAuth.ps1` | Move privileged one-time Entra/bootstrap/consent and rotation procedures into an explicit manual runbook; ordinary CI and the web host receive no tenant-admin rights |
| `scripts\Initialize-LocalDev.ps1` | Document supported local configuration/setup commands; keep no separate local platform control plane |
| `scripts\Install-GitHooks.ps1`, `scripts\Update-Docs.ps1` | Document manual authoring/setup; use existing deterministic docs CI checks, removing hook references together if executable helpers are retired |
| `azureml\Test-Controls.ps1` | Split checks by real consumer: .NET lifecycle tests, build-tool tests and recovery-script tests; remove obsolete combined entry point after coverage |
| `infra\tests\Export-MockedContracts.ps1` | Keep as a single-purpose workflow-used contract fixture helper |

Review `.githooks\pre-commit` and documentation-agent references alongside their
scripts so nothing dangles. Do not modify user-level Git hook configuration or
silently introduce paid AI authoring as a CI dependency.

Exit gate: every remaining executable entry point has an identified supported caller;
manual-only procedures are documented; no replacement TUI or multiplexer exists.

### P10 - Integrated verification and optional live acceptance

- [x] Run targeted checks after each phase and the integrated suite at final cutover.
- [x] Verify contract compatibility, state/address mapping and runtime/deployment ownership together.
- [x] Exercise fault injection at job acceptance, cancel, output completion, regional promotion and Stop boundaries.
- [x] Verify runtime packages, hosted image contents and GPU code bundle contents rather than just matching source strings.
- [x] Record exact commands, versions, runner OS, counts, skipped coverage and network limitations in the handoff.
- [x] Keep all new cloud workflows unlaunched and all compute off until the user explicitly authorizes a deployment/live acceptance stage.
- [ ] If authorized later, validate identity/DNS/RBAC, a reviewed no-replacement migration, one bounded GPU generation, cross-region queue behavior where reproducible, and verified all-region Stop.
- [x] State clearly which capacity/eviction situations could not be reproduced. Do not fabricate a capacity guarantee or mark a skipped live case passed.

Exit gate: all offline acceptance criteria pass and remaining live limitations are
explicit. No live acceptance is implied by approving code implementation.

Evidence: see `docs\operations\verification-handoff.md` for versions, exact commands, counts, the fault-injection map, packaging checks and the ordered live acceptance plan. Added: a missing-output fault case, `scripts\ci\check_server_content.py` (publish/image content allowlist, run on the real publish output, wired into `build_image.py` and CI's server-image job), and a real pinned-source GPU bundle run (previously skipped). .NET 512 passed / 2 skipped; pipeline 29; `build\gpu` 25 with 0 skipped; all other offline suites, web, docs and actionlint clean. The image-content and CPU-probe checks need Docker and have not run. All workflows remain unlaunched and all compute is off. Live acceptance (including the second region and the standalone RTX 6000 ComfyUI VM) needs explicit authorization.

### P11 - Options-based configuration (no Terraform receipt)

- [x] Replace the foundation receipt, `Studio__Foundation` and `Studio__Selection` with the `Studio` options section (`StudioOptions`): explicit per-region resource IDs, evaluated at start with named-setting errors and unknown-key detection, reported on `/health-ui` instead of stopping the host.
- [x] Terraform creates infrastructure only; it emits no receipt or studio app settings.
- [x] Pipelines, recovery, contracts tool and fixtures read one settings document (`contracts\fixtures\studio-settings.json`).
- [x] Docs are App Service first: `docs\configuration.md`, `docs\operations\app-service-settings.md`, `docs\samples\studio-settings.json.example`.
- [x] Remove dead Studio settings that nothing read: the log workspace ID, the private DNS zone IDs, the private endpoint subnet ID, the workspace identity ID and the key vault ID. `PublicIpTags`, `CustomerManagedNsg`, `RegistryId`, `PrivateConnectivityHosts` and `OwnershipTags` stay; `OwnershipTags` is documented as the ownership safety proof.
- [x] Move the workspace identity and key vault IDs to the pipeline-only `Pipeline:Regions:<region>:{WorkspaceIdentityId, KeyVaultId}`; publishing stops when one is missing, and they are described only in the private pipelines docs.
- [x] A bad configuration no longer stops the host: it starts with no background workers, answers `/api` and `/auth` with 503, and serves the anonymous `/health-ui` page and `GET /api/health/config`, which name each setting and never show a configured value. `/healthz` and `/health` stay liveness probes. `Auth:TenantId` falls back to `Studio:TenantId`, then `AzureMl:TenantId`.
- [ ] Follow-ups: AppHost `Studio:*` passthrough; remove the flat single-workspace `AzureMl__*` mode.

Evidence: .NET 496 passed / 2 skipped, pipeline 29, GPU build 25, infra 23, trainers 3, 53 Terraform mocks, 66 recovery, contract fixtures, legacy lifecycle, actionlint, web 44, docs-site build and prose clean. No Azure access, commits or pushes.

## 7. Validation matrix

Commands below are reference commands for implementation, not commands run by this
planning task. Use the smallest relevant selection while working.

| Boundary | Checks |
| --- | --- |
| .NET | `dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts`; target contract/lifecycle/router/auth/recovery cases first |
| Web | In `web\`: `npm run build` and `npx vitest run`; add admin/SSE/permission regression cases |
| Terraform | `terraform -chdir=infra fmt -check -recursive`, `init -backend=false`, `validate`, `test` in a clean/isolated source fixture with no customer tfvars/state |
| Python | `python -m unittest discover -s build\gpu -p test_*.py` (set `WAN_STUDIO_P2_UPSTREAM` for the exact-source case), `-s infra`, `-s trainers`, `-s scripts\ci -p test_pipeline.py` |
| PowerShell | `scripts\recovery\Test-StudioRecovery.ps1`, `scripts\ci\Test-StudioContracts.ps1`, and `scripts\Test-LegacyLifecycle.ps1` for the retained legacy Stop |
| Recovery | Mock ARM calls; prove exact-owned-only deletion, foreign-resource refusal, all-region attempts, idempotency and no Python/Docker/Terraform/server dependency |
| GPU build | Pinned-source/import closure and file allowlist checks; approved Docker CPU probe against staged source and all served modes, without GPU/network |
| Models | Tiny checksum/size/licence fixtures, local reuse, interrupted publication and changed-content rejection; no real weights in ordinary CI |
| Packaging | Inspect published .NET image and GPU code manifest; operator/CI/customer files must not leak into runtime artifacts |
| Docs | Run existing docs build/link/prose checks where applicable; ensure every documented entry point exists |

Dependency installation occurs only after manifest changes or a demonstrated missing
dependency. Use existing approved feeds/tooling and never disable TLS or add public
registry fallbacks to work around this device's feed policy.

## 8. Cutover, rollback and review checklist

Implementation approval must settle the proposed operational resource ownership
handoff and its conditional permissions before P3/P5. No existing deployed state is
silently repurposed.

Cutover sequence:

1. Build/test the replacement contracts, artifacts, pipelines and server while retaining old recovery.
2. In an explicitly approved maintenance window, stop the old deployment and verify compute/jobs/owned egress released.
3. Back up state and receipts; validate exact scope, resource names and reviewed address/ownership migration.
4. Apply the reviewed durable foundation changes with compute off; publish a verified regional release binding.
5. Deploy the approved server image disarmed; verify identity, permissions and private connectivity.
6. Enable operator access/settings only after readiness checks. Start/GPU tests remain separate explicit approvals.
7. Remove obsolete entry points only after the replacement and emergency-release path meet their gates.

On failed publication/deployment, remain disarmed and retain the last verified binding.
On uncertain job or resource state, fence submissions and use verified recovery; do
not roll back by resubmitting. Code rollback uses a compatible approved image/manifest.
State ownership changes require the documented reverse mapping while verified OFF,
not an automatic rollback that recreates compute.

Review approval items:

- [x] CI/CD builds/promotes; App Service verifies/selects releases, with no hosted Docker/Terraform/model downloader.
- [x] Terraform owns durable foundation; .NET owns ephemeral compute/optional egress after verified-OFF handoff.
- [x] Conditional dedicated-subnet permissions and independent recovery are acceptable.
- [x] Multi-region wait-then-move, per-region timeouts and bounded admission match the intended behavior.
- [x] Experimental profiles remain explicitly experimental unless their retirement is separately agreed.
- [x] Hosted training/media work stays gated until its worker boundary is complete.
- [x] Manual setup becomes documentation; no TUI or replacement lifecycle multiplexer.
- [x] No implementation begins until this complete Markdown plan is reviewed.
