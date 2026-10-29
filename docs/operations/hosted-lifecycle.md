# Hosted operational lifecycle

The .NET server in App Service owns regional status, approved-release selection, Start and Stop. It uses its managed
identity and native Azure APIs only: no Terraform, Docker, Python, Git, package restore or model acquisition runs in the
host. Offline/mocked checks cover this phase; it has not been run against Azure.

## Configuration

The host reads its topology from the `Studio` configuration section (.NET options), supplied as App Service application
settings (`Studio__Regions__<region>__...`). Nothing is read from a Terraform output and there is no receipt: the operator
lists each region's exact resource IDs, and the host manages only those IDs. The complete setting list is in
[configuration.md](../configuration.md#studio-hosted-multi-region-topology); the step-by-step setup is
[app-service-settings.md](app-service-settings.md). No `Studio` value is a secret.

Hosted multi-region operation is on exactly when `Studio:Regions` has entries. The host evaluates the settings once at start
and never stops for a bad one. A missing or malformed value (a resource in another subscription, a shared workspace or GPU
subnet, fewer than five private-connectivity hosts, a `ControlRegion` that is not one of the regions, an unknown `Studio`
setting name) leaves the host running without background workers, answers every `/api` and `/auth` route with 503, and is
listed on the anonymous `/health-ui` page and `GET /api/health/config` with the setting's name ([configuration
guide](../configuration.md#configuration-health-page)). When `Studio:Regions` is empty (local development, single-workspace hosts) the
lifecycle routes return 503 and `AzureMl__Armed` still controls local submissions. When it has entries, `AzureMl__Armed` is
ignored: only the durable fence below can open submissions.

Two kinds of values exist. **Environment (App Service settings):** the topology and identities, and the policy ceilings
(`Compute:MaxNodes`, `MaxJobMinutes`, `MaxPaygHourlyUsd`, `ManageEgress`). Changing them needs a settings change and a
restart. **Web GUI (Administration page):** the live limits below, stored in `operations/settings.json` and always bounded by
those ceilings.
## Authorization

Every `/api/studio/*` route requires the **`StudioAdmin`** Entra app role (carried into the session as an admin claim),
plus the normal session, CSRF and origin checks. A `VideoCreator` is not an admin, and `Auth:Mode=None` never implies it.
Sign-in accepts either role and the session carries exactly the roles held: a `StudioAdmin`-only account reaches only Administration (and its content-free change notices), never creator routes, and a creator never reaches `/api/studio/*`. Each role is its own session claim (`studio_creator`, `studio_admin`), so sessions issued before this change must sign in again. Creating and assigning the app roles is manual Entra work; this repository does not do it.

| Route | Purpose |
| --- | --- |
| `GET /api/studio/status` | Durable phase, selected release and **live** Azure state per region. An unreadable region is `UNREADABLE`, never shown as off |
| `POST /api/studio/regions/{region}/release` `{artifactId}` | Select an already promoted release. Read/verify only; refused unless the region is Off |
| `POST /api/studio/regions/{region}/start` `{approveGpuSpend, approveCustomerNsgRules}` | Verify, then create one private Spot node and arm |
| `POST /api/studio/stop` | Release every region and prove it |
| `GET /api/studio/settings` | Effective settings, the deployed ceilings and the version token |
| `PUT /api/studio/settings` {expectedVersion, maxConcurrentJobs, queueMoveMinutes, regions} | Replace the settings; refused with `409` if the version is stale |

## Durable fence

`operations\state.json` lives in the control region's private `wan-control` container, written with ETag conditions. Each
region is `off`, `starting`, `armed`, `stopping` or `unknown`; only `armed` permits generation, and only for the matching
workspace. The submission guard reads a validated snapshot refreshed every 5 seconds; a missing or older-than-30-second
snapshot means **no generation**, so a restart, an unreachable control store or a failed refresh fails closed.

- Start writes `starting` before any check or Azure write. Only one operation runs per region; a second is refused.
- On host start, `starting`/`stopping` become `unknown`. An `armed` record whose compute is absent, drifted or unreadable
  also becomes `unknown`. `unknown` blocks Start until Stop proves release.
- A release whose durable write failed is masked in memory and retried on refresh; the stale record is never trusted.
- Stop fences the whole host first, cancels any in-flight Start, then handles regions one at a time and aggregates failures.

## Start checks (all before the first write)

Explicit spend approval (and the customer-NSG acknowledgment when an existing NSG is used); a selected release whose
manifest, regional binding, live environment and live model match; the workspace's ownership tags; the dedicated GPU
subnet's CIDR, NSG, default-outbound and delegation; private DNS/TCP reachability of every endpoint; no queued or running
job that could resume; exactly one Linux pay-as-you-go retail rate at or below the regional threshold; an unrestricted exact
A100 SKU; and sufficient Spot and A100 family quota. Price, SKU and quota are eligibility, not capacity. The threshold is
not a billing cap.

Then, only for a region with `Compute:ManageEgress`, the studio creates its tagged public IP and NAT and attaches the NAT
to the dedicated subnet using the subnet's ETag and preserving its other properties; then it creates one private Spot
compute (min 0, `Compute:MaxNodes`, 120-second idle, no public IP, SSH off) and verifies it. Any failure after the first write
runs the release routine; if release cannot be proven the region is `unknown`.

## Stop and release semantics

Per region: cancel relevant jobs (errors are warnings only if fresh reads later prove every job terminal), delete only an
owned compute with `underlyingResourceAction=Delete`, wait for absence, prove all relevant jobs terminal, then detach only
the studio's NAT, delete only its NAT and public IP, and verify. A foreign compute, NAT, public IP or subnet association
is an error, never adopted or detached. The result is `verified` only when every region proves release **and** its durable
state is recorded. Repeating Stop is safe. Hosts do not read or manage egress they are not configured to manage.

## Emergency path and permissions

`scripts\recovery\Stop-Studio.ps1` stays independent of this host. After it runs, restart the host; it will mark any
region armed without a valid compute as `unknown`, and a Stop through the host records `off`.

The host identity's permissions are defined in `infra\operations-rbac.tf` and `docs\operations\ownership-and-permissions.md`
(compute write/delete, job read/cancel, subnet read/join, SKU and usage reads, release read, control write). Live sufficiency
of those grants, private routing and the real ARM/AML response shapes are unverified.

## Regional routing (P6)

A hosted host (`Studio:Regions` has entries) submits through the regional backend; local and legacy hosts keep the flat `AzureMl__*` backend unchanged.

- **Settings** live in `wan-control` `operations/settings.json` and default from the configured ceilings (region concurrency = `MaxNodes`, timeout = `MaxJobMinutes`, global = sum, queue move 10 min, control region priority 0). An invalid stored document keeps the last valid one. They are edited in the Administration page (below).
- **Eligibility**: only armed, release-verified regions; each ineligible region reports its reason. Order is lower priority number, then more headroom, then name. Quota/SKU/price are eligibility, not live capacity.
- **Admission**: global and per-region slots are reserved before submission and held until the attempt is terminal. Lowering a limit never stops running or resumed work.
- **Attempt ledger** (`video-library-attempts/{job}.json`, contract-validated before every write): the job record and intent are saved before the remote create. The create is never retried; an uncertain result is reconciled by GET only.
- **Queue move**: only when status is Queued/Provisioning/NotStarted for longer than the queue-move wait, the job has the recorded input names, and an unvisited eligible region has a free slot (reserved first). The job is cancelled once; a move happens only after terminal proof of cancellation without a start. Completed jobs are collected, started or unknown jobs are never moved or re-cancelled, and no proof surfaces as `Unknown` for the operator. At most one attempt per region; exhaustion leaves the last attempt tracked, with no retry loop or hedging.
- **Timeout**: each attempt snapshots the destination region's timeout, applied to the Azure ML limit and the runner. Sequential attempts increase total elapsed time and possible cost; a per-attempt timeout is not a global time or dollar budget.
- **Output and media** are read from the successful attempt's region store, never inferred from current settings. Pre-routing jobs are adopted as control-region attempts (legacy artifact marker of 64 zeros) and never move.
- **Recovery**: restarts resume at every boundary (saved but no intent: verify and fail without creating; cancel requested/unknown: no re-cancel; canceled unstarted: continue routing; completed: collect only).
- **Spot Placement Score** is a deferred advisory spike, not implemented. Azure ML capacity APIs do not guarantee run-versus-wait, and the placement API shares the ARM read quota and should be cached.
## Administration (P7)

An administrator sees **Administration** in the web UI (creators who are also admins get it beside their normal views; an admin-only account sees only it). It shows per region the phase (including `Unknown` and in-flight `Starting`/`Stopping`), selected and armed release, live compute and nodes, active jobs, owned egress, blockers and the last error, and offers release selection, Start (with explicit GPU-spend and customer-network confirmations, then a browser confirmation) and Stop all. Release builds, promotion and foundation changes run in your own build pipeline; the repo ships GitHub Actions workflows as the reference implementation. Administration only selects an already promoted GPU release by its artifact ID; it never builds or promotes one. To get the ID, see [Select a release: where the artifact ID comes from](#select-a-release-where-the-artifact-id-comes-from). If a region shows Compute `UNREADABLE`, see [Fix a region that shows UNREADABLE](#fix-a-region-that-shows-unreadable).

Settings edit the global and per-region concurrency, per-region job timeout, priority and queue wait. Values above a deployed ceiling (nodes, the region's timeout policy, the absolute 120 minutes, global above the regional sum) are rejected with the exact limit, never clamped. The form sends the version it loaded; if another administrator saved first the server answers `409`, nothing is written, and the UI offers to discard the local edit and reload. Saved settings apply to new submissions and attempts; running work is never interrupted. Every update (and every rejection) is audited with actor, old and requested values and outcome in `audit/`, and a content-free `studio` event over the existing SSE stream tells other administrators to refresh. The browser does the same range checks only to show mistakes early; the server re-validates everything.

## Limits

- One App Service instance. ETag checks protect overlapping restarts, but there is no distributed lease.
- Live behavior (cancel semantics, Queued/Provisioning for Spot, response shapes) is unverified against real Azure.
- Release selection takes the GPU build artifact ID that your build pipeline produced and promoted; the UI does not list releases, so you must bring the ID (see the next section).

## Select a release: where the artifact ID comes from

A new region shows phase `Off`, the blocker "No approved release is selected." and a **Release artifact ID** box. The page banner "Workspace and private storage connected. WAN asset preparation is not complete; generation is disabled." means the same thing: no release is selected or armed yet.

The box takes the **GPU build artifact ID**: 64 lowercase hex characters, the SHA-256 identity of a sealed GPU build (`^[a-f0-9]{64}$`). The host verifies it on Select. It is **not**:

- the server artifact ID or the trainer (`yolox`) artifact ID. A server or yolox ID is rejected with a verification error.
- a source release, tag, commit or CI run ID, on any platform.
- an image digest (`sha256:...`).

The same ID is used for every region. What differs per region is the regional binding that promotion writes, and only an ID that has a binding for that region can be selected there.

### Where the build comes from

This repo is a solution accelerator. Nothing is pulled from the author's GitHub releases or from GHCR. You build from your own fork or source checkout, and the images are pushed to your own private Azure Container Registry (`Studio:Regions:<region>:Platform:RegistryId`; repositories `wan/server`, `wan/runtime` and `wan/yolox`; tag `unready-<run id>-<attempt>`, then promoted by digest). The upstream GPU runtime source and the model weights and licences come from caches you supply: `STUDIO_UPSTREAM_SOURCE` (a git cache holding the pinned upstream commit), `STUDIO_MODEL_CACHE` and `STUDIO_LICENSE_CACHE` (preverified caches). The build never downloads weights. If a cache is missing, the build stops before it seals anything.

The artifact ID does not depend on the CI system. It is a content hash computed by the repo scripts, so any platform that runs them produces it.

### Order of work

1. Build and seal the GPU artifact (`scripts\ci\build_image.py`). It prints the ID.
2. Promote that ID to every configured region (`scripts\ci\publish_region.py`).
3. In Administration, per region: paste the ID, Select release, approve spend, Start.

### Prerequisites on any build platform

- Python 3.12 with both hashed locks installed in the same interpreter: `pip install --require-hashes -r scripts\ci\requirements.lock` and, for the GPU component, `pip install --require-hashes -r build\gpu\requirements-build.lock` (the GPU build tools import their tracing packages). The reference `private-context` action installs both.
- The built Contracts tool, with `STUDIO_CONTRACT_TOOL` pointing at its dll.
- Docker with buildx.
- `az login` as the publisher principal. The scripts require the signed-in Azure CLI caller to equal `Pipeline:PublisherPrincipalId`.
- A runner that can reach the private registry, storage and Azure ML endpoints (private DNS and a network route).
- The settings document described in [Private pipelines](private-pipelines.md), with `Studio` and `Pipeline` sections, supplied as `STUDIO_SETTINGS_JSON` or `STUDIO_SETTINGS_PATH`.
- The build variables in the table below. `build_image.py` (all components) and `foundation.py plan` read them. The run ID and attempt form the image tag.
- Optionally `STUDIO_FEDERATED_IDENTITY=true`. It makes the scripts (and `scripts\recovery\Stop-Studio.ps1`) require the signed-in Azure CLI identity to be the federated service principal `AZURE_CLIENT_ID`. If it is unset, that check is skipped, but the publisher principal check above always applies.

| Name | Meaning | Required | GitHub fallback |
| --- | --- | --- | --- |
| `STUDIO_BUILD_SOURCE_SHA` | The 40-hex source commit | Yes | `GITHUB_SHA` |
| `STUDIO_BUILD_RUN_ID` | A numeric ID, unique per run | Yes | `GITHUB_RUN_ID` |
| `STUDIO_BUILD_RUN_ATTEMPT` | The numeric attempt number | Yes | `GITHUB_RUN_ATTEMPT` |
| `STUDIO_BUILD_OUTPUT` | A file to which the script appends `artifact=<id>` and `digest=<digest>` lines | No | `GITHUB_OUTPUT` |

If both names are set, the `STUDIO_` name wins. A missing required value stops the script with `STUDIO_BUILD_<NAME> is required`. GitHub Actions needs nothing extra: it supplies the `GITHUB_*` fallbacks, and `GITHUB_ACTIONS=true` still turns on the federated identity check. On Azure DevOps, GitLab, Jenkins or a local shell, set the three required `STUDIO_BUILD_*` variables from your CI's own commit, run and attempt values.

The scripts have not been tested on a non-GitHub platform in this repo. If the first run fails, check in this order: the three required `STUDIO_BUILD_*` variables are set and `STUDIO_BUILD_SOURCE_SHA` is 40 hex characters; `az account show` is the publisher principal; the runner resolves and reaches the registry, storage and Azure ML endpoints; `STUDIO_CONTRACT_TOOL` points at an existing dll; the three caches exist.

### Prepare the GPU build caches (one time per runner)

The GPU build never downloads anything. It reads three caches that you prepare on the runner, as paths the runner itself can open (a WSL runner needs Linux paths, not Windows paths). Set each as a repository variable.

1. **`STUDIO_UPSTREAM_SOURCE`: a full git clone of the upstream project.** The build exports the pinned commit from it. The pin is the `sourceSha` in `build\gpu\profiles.json`.

```bash
git clone https://github.com/jakeatmsft/azureml_vidgen_comfyui.git /srv/wan/upstream
git -C /srv/wan/upstream cat-file -t <sourceSha from build/gpu/profiles.json>   # must print: commit
```

Use a full clone: a shallow clone, a fork, or a different repository fails with `unknown revision` and `Source repository does not contain the reviewed upstream commit.` If you keep your own mirror, clone that instead, as long as it holds the pinned commit.

2. **`STUDIO_MODEL_CACHE`** and **`STUDIO_LICENSE_CACHE`:** folders holding the pinned model files and their licence texts, matching `build\gpu\wan-pinned-manifest.json`. See `build\gpu\README.md` for the layout and checks. The build verifies sizes and hashes and stops on any mismatch.
### Step 1: build the artifact and read its ID

Run the build from the repo root:

```powershell
python scripts\ci\build_image.py --component gpu --settings <settings.json> --work <dir>
```

It builds and pushes the image, verifies it, calls `build\gpu verify` and then `publish()` in `scripts\ci\publish_gpu_build.py`, which writes `builds/<id>/manifest.json` (immutable) into the control region's `wan-release` container. The last line it prints is:

```text
Verified gpu artifact <64-hex id>. No deployment, GPU allocation, generation or training.
```

Copy the 64-character value from whatever log your platform keeps.

### Step 2: promote it to every region

Run once per configured region:

```powershell
python scripts\ci\publish_region.py --settings <settings.json> --artifact <id> --region <region>
```

Each run prints:

```text
Verified regional binding: releases/<id>/<region-scope-fingerprint>.json
```

A region without that line has no binding for the ID, and Select fails there. Fix the cause shown in the output and run the command again for the same ID. Retries are safe. The other regions are not affected.

### Reference implementation: GitHub Actions

The repo ships `release.yml` and `promote.yml` as one way to run the two steps. If you use GitHub Actions, they do it for you:

1. Open Actions, then the `release` workflow, then **Run workflow** from the protected default branch, with `component` set to `gpu`. The job uses the `studio-build` environment, so a reviewer approves it, and it runs on the private runner labelled `wan-private`. The runner needs the three cache variables above.
2. Open the step **Build, CPU-check and seal only the selected artifact**. It prints the `Verified gpu artifact` line and writes the ID to its step output `artifact` (and the image digest to `digest`).
3. Run `promote` with `component` = `gpu` and `artifact` = that ID. It uses the `studio-publish` environment and the same runner, and runs `publish_region.py` for every configured region. Each region's log shows the `Verified regional binding` line.

### Step 3: select and start in Administration

1. Sign in as a `StudioAdmin` and open Administration.
2. For each region that is `Off`, paste the ID into **Release artifact ID** and choose **Select release**. Selecting only works while the region is Off, and it only reads and verifies. It repeats once per region.
3. Tick the GPU spend approval. If `Network:CustomerManagedNsg` is true, also tick the customer NSG acknowledgement.
4. Choose **Start** for the region, then confirm in the browser dialog.

### Finding the ID after the fact

None of these depend on a CI system. Use whichever you can reach.

1. **The control region's storage account.** In the Azure portal open the account, then Containers, then `wan-release`. Each folder under `builds/` is an artifact ID. Folders under `releases/<id>/` are the promoted ones. A region can only select an ID that has `releases/<id>/<fingerprint>.json` for that region. The same listing is available from the CLI, which needs Storage Blob Data Reader and a network path to the private endpoint:

```powershell
az storage blob list --account-name <storage account> --container-name wan-release `
  --prefix releases/ --auth-mode login `
  --query "[].{name:name, lastModified:properties.lastModified}" -o table
```

   Use `--prefix builds/` to list built artifacts instead.
2. **The manifest.** Read `builds/<id>/manifest.json` to confirm what an ID is.
3. **The printed line.** Search whatever log the build wrote for `Verified gpu artifact`.
4. **The reference GitHub workflow.** The `release` run's step log or its step output `artifact`.

If several builds exist, the newest promoted one for your region is the folder under `releases/<id>/` with the latest `lastModified`.

## Fix a region that shows UNREADABLE

Compute `UNREADABLE` with "Live Azure state could not be read; the region is not shown as off." means the host could not read that region from Azure Resource Manager. The page and `GET /api/studio/status` read, for each region: the workspace, the compute (a missing compute is a normal 404 and shows `OFF`), the NAT gateway and public IP only when `Compute:ManageEgress` is true, and the workspace jobs list. Any failure other than 404 gives `UNREADABLE`. The host fails safe: the region is never shown as off and generation stays fenced.

The status error text also ends with one of these causes. Provider error text is deliberately not shown:

- `Cause: Azure answered HTTP <code> to a read of the workspace, compute or jobs (...)`
- `Cause: <ExceptionType> (see the host log)`

### Step 1: read the full error

The host writes the full error as the warning `Live status for <region> is unreadable.` Read it from:

- App Service, Monitoring, **Log stream**, or `az webapp log tail -g <resource-group> -n <site>`.
- Application Insights or Log Analytics traces, if you configured them.
- Locally, the .NET or Aspire console log (`aspire logs api`).

### Step 2: match the symptom

| Cause text or log | Likely cause | Fix |
| --- | --- | --- |
| HTTP 403 or 401 | The identity the host uses has no read on that region's resources | Procedure A |
| HTTP 400, or 403 on a resource that does not exist | `Workspace__Id` or `Compute__Id` is malformed or points at the wrong place, or the resource group or workspace was deleted or moved | Procedure B |
| HTTP 429 or 5xx | Transient Azure error | Reload; retry after a few minutes |
| An exception type name instead of an HTTP code (for example an Azure.Identity credential exception) | The host cannot acquire a token (stale credentials, no login) | Procedure C |

One region can be `UNREADABLE` while another is fine, because each region has its own workspace-scope grant and its own settings. Compare the failing region's grants and settings with the working region's.

### Procedure A: the identity lacks read access (HTTP 403 or 401)

1. Find the identity. On App Service it is the site's managed identity (the Terraform `host` principal when the portal is created). Locally it is whichever account `DefaultAzureCredential` picks, usually the `az login` user, and that account must be the Terraform `operator_principal_id`.
2. Locally, run `az account show` and confirm the user, tenant and subscription. If they are wrong, run `az login --tenant <tenant-id>` and `az account set --subscription <subscription-id>`, then restart the host.
3. Check the grant at the region's workspace:

```powershell
az role assignment list --assignee <principal object id> --scope <workspace id> --include-inherited -o table
```

   The output must list a role named `wan-operator-...`. `infra\operations-rbac.tf` creates that custom role per region, with reads and writes on the workspace (workspaces read, computes read, write and delete, jobs read, jobs cancel), the compute identity (read, assign), the VNet and the GPU subnet (read, join). At subscription scope it grants the region-eligibility reads (`locations/usages/read`, `Compute/skus/read`). Both the operator and, when the portal exists, the host principal receive them.
4. If the role is missing, have the foundation owner run `foundation.yml` and review the plan for the role assignments of that region. A new role assignment can take several minutes to propagate.
5. Verify (see below).

### Procedure B: wrong or missing resource

1. Compare the region's settings with the real resources: `Studio__Regions__<region>__Workspace__Id` must be the workspace's full ARM ID. `Studio__Regions__<region>__Compute__Id` must be `<workspace id>/computes/<name>`. `/health-ui` and [app-service-settings.md](app-service-settings.md) show each value and where to find it.
2. In the Azure portal, confirm the workspace and its resource group still exist in the subscription named by `Studio:SubscriptionId`.
3. Fix the setting, restart the site, and verify.

### Procedure C: no token

1. On App Service, confirm the site's managed identity is enabled and `AZURE_CLIENT_ID` names the intended identity, then restart the site.
2. Locally, run `az login` again and restart the host.

### Verify

Reload Administration, or call `GET /api/studio/status` as the signed-in admin. Compute must show `OFF` (no compute), or `IDLE` or `RUNNING` for a started region. If it is still `UNREADABLE`, read the new `Cause:` text and the host log again.
