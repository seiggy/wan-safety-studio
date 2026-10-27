# Studio execution contracts

These contracts separate immutable build artifacts from regional deployment and
operational state. They are introduced by P1 of the approved
[execution-boundary plan](../docs/plans/execution-boundaries-and-multi-region.md).
They do not switch the running server or Terraform to the new lifecycle yet.

| File | Version | Producer / consumers |
| --- | --- | --- |
| (no file) | n/a | The studio topology. It is the `Studio` configuration section (`StudioOptions`), validated by `StudioContractRules.Foundation`; the shared example is `fixtures\studio-settings.json`. There is no receipt and no Terraform `studio` output |
| `build.schema.json` | 1 | GPU build runner; regional publication and release verification |
| `release.schema.json` | 1 | Verified regional publisher; hosted release selection |
| `trainer.schema.json` | 1 | Trainer publisher (`publish_trainer.py`); hosted trainer registry and remote training jobs |
| `settings.schema.json` | 1 | StudioAdmin settings; admission and timeout enforcement |
| `job.schema.json` | 1 | Submission/recovery coordinator; remote attempt reconciliation |

`fixtures\studio-settings.json` is the one explicit settings document every test and pipeline tool shares: a `Studio` section in appsettings shape (what the web host reads, see [docs\configuration.md](../docs/configuration.md)) and a `Pipeline` section the web host never reads. `fixtures\studio.json` is a shared, synthetic two-region example of the build, release, settings and job documents, not customer
configuration or a usable prepared release. UUIDs, model files, file contents and
image digests are fixtures. Do not download the example model or deploy either file.
The example's legacy region reproduces the existing Terraform fixture's names.
Its `unicodeBuild` entry checks non-ASCII text, supplementary characters, ordinal
path ordering and JSON control escaping against an independent Python implementation.

## Validation

The authoritative typed readers are `StudioOptionsMapper.ToFoundation` (the `Studio` options), `StudioContracts.ReadBuild`, `ReadRelease`, `ReadSettings` and `ReadJob` in the Application project.

- All fields are required unless explicitly nullable. Unknown, duplicate,
  case-mismatched, coerced or malformed fields fail validation.
- The `Studio` options are explicit operator configuration: the operator lists the exact resource IDs. They are validated structurally (right resource type, studio subscription, no shared workspace or GPU subnet, six DNS zones, at least five private hosts, policy bounds); nothing is derived from a Terraform output.
- Validate the `Studio` options before passing them to the other readers.
- Private run, release and control containers have distinct purposes. GPU identities
  and creator uploads must not be able to write operational state in `wan-control`.
- The immutable build identity contains no deployment IDs, local paths, timestamps,
  timeout, concurrency, routing priority or registry hostname.
- Release bindings select an exact workspace, compute identity/storage scope, image
  digest and positive-integer model version. They are not transferable between regions.
- Settings may reduce deployment ceilings, not increase them. A policy change must
  revalidate settings, but does not rebuild the GPU artifact.
- A ledger records a remote identity before submission. Only a verified canceled
  attempt with no observed execution may precede a queue move. Completed, live and
  unknown attempts cannot be replayed through this contract.

Schema validation checks structure, types and independent bounds; it cannot prove
cross-field scope, checksums or Azure state. PowerShell consumers use the generated
schemas as an initial check, not as authorization to mutate resources. Every mutation
still needs contextual validation and fresh cloud ownership/state reads.

In particular, a missing local execution timestamp is not proof that Azure never
started a job. The routing implementation must reconcile fresh server evidence;
contracts do not replace Azure reads or make Spot allocation predictable.

## Naming and ownership

The suffix is the first eight lowercase SHA-256 hex characters of:

- Existing explicitly selected legacy region: `<subscription>:<deployment>`.
- New regional resources: `<subscription>:<deployment>:<region>`.

Subscription UUIDs and Azure region keys are canonical lowercase. At most one region
uses legacy naming. Other service names follow `StudioResourceNames`, preserving the
current short-slug rule for vault/storage names. These are Terraform's naming rules
for the resources it creates; fixture checks prevent accidental replacement of legacy
identities. The host does not derive IDs from them: it uses the IDs in the `Studio` settings.

Durable resource tags are exactly the declared `application`, `deployment`,
`managedBy=terraform` and `region` values. Ephemeral resources use the same scope
with `managedBy=studio`. Fresh resource checks require the declared tags but may
retain additional customer/policy tags. A legacy Terraform-owned resource is not
silently adopted as a studio-owned resource.

The `owner` policy is explicit. A Terraform-owned legacy (v1) deployment can be inspected during
migration, but is not permission for the new hosted implementation to create or
delete its compute. Runtime ownership requires the verified-OFF handoff in the
[ownership guide](../docs/operations/ownership-and-permissions.md).

## Content identity

`artifactId` is lowercase SHA-256 over canonical UTF-8 JSON of the complete build
manifest with `artifactId` set to the empty string. The property remains present.

1. Sort the `code`, `models`, `licenses` and `buildInputs` arrays by their case-sensitive `path`,
   using ordinal UTF-16 ordering.
2. Sort object keys recursively using ordinal ordering; preserve other array order.
3. Write compact JSON with no optional spaces, UTF-8 text and no HTML/non-ASCII
   escaping. Escape JSON control characters normally. Numeric build fields are integers.
4. Hash those bytes without a trailing newline.

For Python, use explicit ordinal UTF-16 path sort keys and compact JSON with
`ensure_ascii=False`, `sort_keys=True`, `allow_nan=False`. Object keys in the build
contract are fixed ASCII identifiers. The shared fixture is the cross-language hash
test vector; implementations must reproduce it exactly.

Files require nonnegative sizes and SHA-256 hashes (model weights must have positive
sizes). Empty Python package markers are legitimate source. Code paths reject traversal,
secrets/caches, root asset directories and weight files while retaining legitimate
nested Comfy packages such as `comfy\ldm\models` and `comfy_api\input`.
The WAN payload includes only the declared upstream helper closure plus ComfyUI
source and attribution; it must not contain the old Python portal or submission SDK.
Experimental LTX builds additionally require their patched bootstrap. The build
contract can describe experimental artifacts, but hosted release bindings currently
reject non-WAN profiles rather than pretending the production provider supports them.
Build manifests also retain reviewed `buildInputs` hashes, including the exact
runtime dependency lock, pins, Dockerfile and isolated runtime patch. This required
field is part of the pre-deployment P2 contract refinement.

## Caller and replacement inventory

Disposition after P9. Old operator entry points were removed once their replacement existed.

| Former area | Disposition |
| --- | --- |
| `runtime.py prepare/verify`, `models.py`, source/image/model helpers, Python configuration/receipt checks | Removed. Build-only preparation is `build\gpu`; promotion is `publish_region.py` / `publish_trainer.py`; typed release/foundation validation is the shared .NET contracts; hosted Start re-reads live cloud state |
| `cost_guard.py` submission functions, source pin/error formatter, `upstream-cost.patch` | Removed. Production job controls and read-back are the .NET `AzureMlGuard`; upstream submitters are not shipped; build inputs are pinned in `build\gpu` |
| `diagnostics.py`, `telemetry.py`, exporter requirements | Removed with their only (retired portal / Prepare) callers; pipeline tracing is in `scripts\ci\pipeline.py` |
| PowerShell Deploy / Prepare / Portal / Publish / Start and build helpers | Retired. Foundation and server deployment are the `foundation.yml` / `release.yml` / `deploy-server.yml` workflows; Start is the hosted lifecycle |
| PowerShell Start/Status/Stop | Hosted lifecycle (P5) plus `scripts\recovery`. `Invoke-WanSafetyStudio.ps1` is retained frozen, accepting only Status/Stop/Check for an un-migrated v1 deployment, with `Test-LegacyLifecycle.ps1`; delete both after the v1 to v2 migration is verified |
| Shell code/model upload scripts | Removed. One immutable promotion path |
| Portal-auth initialization | Removed. Manual runbook `docs\operations\entra-bootstrap.md`; deployment consumes the approved client ID |
| Local-dev initialization | Removed. Hand-written `appsettings.Development.json` (`docs\local-generation.md`) |
| Git-hook/docs helpers | Removed. Docs CI (`docs.yml`, `check-prose.mjs`) remains |
| `test_controls.py`, `test_sdk_controls.py`, `Test-Controls.ps1` | Removed. Coverage lives in the .NET guard/lifecycle/routing tests, `build\gpu`, `scripts\ci`, `scripts\recovery` and `infra` suites; the legacy Stop test is `scripts\Test-LegacyLifecycle.ps1` |
| AppHost | Starts the .NET server and passes configuration; does not call Python or the operator script |
| `DockerComputeTarget`, trainer registry/Python | Hosted: `AzureMlTrainingCompute` plus pipeline-published `trainer.schema.json` bindings (P8, gated off); `DockerComputeTarget` stays the supported local mode |
| Reference-only workflows | Moved to `docs\reference-workflows\`; not published with the server |
This inventory does not mark dormant experimental profiles, local ComfyUI, trainer
templates or licensing as dead merely because production WAN does not execute them.

## Concurrency and recovery contract

Operational versions begin at one and advance by exactly one. An ETag/lease check
must accompany a durable write; a version number alone is not a storage lock.
`RequireNextVersion` rejects stale, skipped and overflowed versions.

The initial supported topology has one active coordinator. The hosted operational
store will use a global admission fence and short per-workspace operation leases
in control storage, rather than relying only on process-local semaphores.
Acquire coordination in a consistent global/workspace/job order. Do not hold a
submission lock for the duration of generation.

Persist submission intent before ARM creation. On lease loss, timeout or host
restart, keep ambiguous identities fenced until reconciled. A 404 for an
ambiguously submitted intent is not by itself proof that a request was never accepted.
Stop must still attempt native release if storage/coordinator recovery fails, but
must report unresolved operations instead of claiming complete release.

## Regenerating and checking schemas

Schemas are exported by .NET 10's built-in `JsonSchemaExporter`, with independent
policy bounds applied by `StudioContractSchemas`; no JSON-schema package is added.

From PowerShell in the repository:

```powershell
$env:WAN_STUDIO_UPDATE_CONTRACTS = '1'
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore `
  --filter 'FullyQualifiedName=VideoSafetyStudio.Tests.StudioContractTests.Checked_in_contracts_match_types_and_shared_fixtures'
Remove-Item Env:WAN_STUDIO_UPDATE_CONTRACTS
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore `
  --filter 'FullyQualifiedName~StudioContractTests'
pwsh -NoProfile -File scripts\ci\Test-StudioContracts.ps1
python scripts\ci\test_build_identity.py
```

Review the generated diff; regeneration is not acceptance of a contract change.
Normal CI compares schemas/fixtures with the typed exporter and does not rewrite them.
