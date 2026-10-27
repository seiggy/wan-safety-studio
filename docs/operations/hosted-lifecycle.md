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

An administrator sees **Administration** in the web UI (creators who are also admins get it beside their normal views; an admin-only account sees only it). It shows per region the phase (including `Unknown` and in-flight `Starting`/`Stopping`), selected and armed release, live compute and nodes, active jobs, owned egress, blockers and the last error, and offers release selection, Start (with explicit GPU-spend and customer-network confirmations, then a browser confirmation) and Stop all. Build, promotion and foundation changes are shown as GitHub Actions deployment activity; nothing here runs them.

Settings edit the global and per-region concurrency, per-region job timeout, priority and queue wait. Values above a deployed ceiling (nodes, the region's timeout policy, the absolute 120 minutes, global above the regional sum) are rejected with the exact limit, never clamped. The form sends the version it loaded; if another administrator saved first the server answers `409`, nothing is written, and the UI offers to discard the local edit and reload. Saved settings apply to new submissions and attempts; running work is never interrupted. Every update (and every rejection) is audited with actor, old and requested values and outcome in `audit/`, and a content-free `studio` event over the existing SSE stream tells other administrators to refresh. The browser does the same range checks only to show mistakes early; the server re-validates everything.

## Limits

- One App Service instance. ETag checks protect overlapping restarts, but there is no distributed lease.
- Live behavior (cancel semantics, Queued/Provisioning for Spot, response shapes) is unverified against real Azure.
- Release selection takes an artifact ID promoted by the deployment workflows; the UI does not list releases.
