# Verification handoff and live acceptance plan

Status: offline verification complete. **No live acceptance has been run.** Nothing here was executed against Azure, no image
was built or pulled, no weights were downloaded, no GPU was allocated, no workflow was dispatched and nothing was committed
or pushed. Compute is off in every deployment this work touched.

## Environment

Windows 11 (NT 10.0.26300), .NET SDK 10.0.403 (runtime 10.0.14), Terraform 1.16.6, Node 24.11.0, Python 3.12.10, PowerShell 7.6.8,
actionlint 1.7.12. All package traffic goes through the approved feed only. Linux, Docker and the private runners were not used.

## Exact commands and results

| Boundary | Command | Result |
| --- | --- | --- |
| .NET | `dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts` | 512 passed, 2 skipped, 0 warnings |
| Pipeline fault injection | `python -m unittest discover -s scripts\ci -p test_pipeline.py` (hash-locked `scripts\ci\requirements.lock`; `STUDIO_CONTRACT_TOOL` = built `tools\VideoSafetyStudio.Contracts` dll) | 29 passed, including real .NET validation of release and trainer bindings |
| Build identity | `python scripts\ci\test_build_identity.py` | pass (ASCII and Unicode vectors match .NET) |
| GPU build boundary | `python -m unittest discover -s build\gpu -p test_*.py` with `WAN_STUDIO_P2_UPSTREAM` = the exact pinned upstream commit (fetched read-only into `.artifacts`) | 25 passed, 0 skipped. Real payload: 621 files, 38,922,311 bytes, no operator modules or weights |
| Verified-OFF migration | `python -m unittest discover -s infra -p test_*.py` | 23 passed |
| Trainer SDK | `python -m unittest discover -s trainers -p test_*.py` | 3 passed |
| Terraform | `pwsh -NoProfile -File infra\tests\Export-MockedContracts.ps1` (isolated fmt, init, validate, 55 native mocks, output cross-checks) | pass |
| Recovery | `pwsh -NoProfile -File scripts\recovery\Test-StudioRecovery.ps1` | 36 checks pass |
| Shared contracts | `pwsh -NoProfile -File scripts\ci\Test-StudioContracts.ps1` | pass |
| Legacy Stop | `pwsh -NoProfile -File scripts\Test-LegacyLifecycle.ps1` | pass |
| Web | in `web\`: `npx tsc -b`, `npx vitest run`, `npm run build` | 44 tests pass; build clean |
| Docs | in `docs-site\`: `node scripts\check-prose.mjs`, `npm run build` | clean |
| Workflows | `actionlint` | clean |
| Published content | `dotnet publish src\VideoSafetyStudio.Server -c Release -p:SkipWebBuild=true`, built UI copied to `wwwroot`, then `python scripts\ci\check_server_content.py <dir>` | pass: no `.ps1/.sh/.py/.tf/.tfvars/.tfstate`, no infra/scripts/docs folders, exactly the three production workflow templates |

Customer `infra\terraform.tfstate` and `infra\terraform.tfvars.json` hashes are unchanged from the start of the work.

## Fault-injection coverage

| Boundary | Where it is exercised |
| --- | --- |
| Job acceptance (lost or unclear create, restart between record and intent, restart after create) | `RegionalRoutingTests`: unrecorded acceptance, crash between job record and intent, host stop mid-attempt; `RemoteTrainingTests`: unclear create settled by GET, never twice |
| Cancel (unproven, repeated, completes during cancel, restart after cancel intent) | `RegionalRoutingTests`; `RemoteTrainingTests` (detach versus proven cancel) |
| Output completion (completed job, no output; collected after restart; correct region) | `RegionalRoutingTests` (including a completed job that produced no video: explicit failure, no replay, capacity released); `RemoteTrainingTests` |
| Regional promotion (partial promotion, retry, changed bytes, registration failure) | `test_pipeline.py` `PromotionTests`, `TrainerPromotionTests` |
| Stop (every region, repeat, foreign resources, unreadable store, interrupted, cancel errors) | `StudioLifecycleTests`; `Test-StudioRecovery.ps1`; `Test-LegacyLifecycle.ps1` |
| Authorization, stale settings, SSE leakage | `AuthTests` role matrix, `StudioSettingsTests`, `HttpLifecycleRegressionTests` |

## Packaging checks

- Server image source: the Dockerfile installs `ffmpeg`/`ffprobe`; `build_image.py` runs both and, for the server, copies `/app` out of the real image and runs `check_server_content.py` on it. CI's `server-image` job loads the built image and does the same. **Neither has run**: they need Docker and a runner.
- GPU code bundle: verified against the exact pinned source by the staged-payload tests above; the approved CPU probe in `build\gpu\verify.py` needs Docker and has not run.
- Trainer image: `build_image.py` runs `python -m vision.trainers.yolox --help` offline; not run.

## Not covered offline

Real ARM/Azure ML response shapes; whether cancelling a queued Spot job behaves as assumed; what Queued/Provisioning mean for
Spot; managed-identity RBAC sufficiency; private DNS and routing; Entra role assignment; private runners; Linux behavior;
Docker builds and image contents; GPU/CUDA validity of the trainer; real Spot capacity and eviction behavior. Capacity and
eviction cannot be reproduced on demand, so no capacity guarantee is claimed.

## Live acceptance plan (needs explicit authorization before any step)

Planned topology: the existing hub/spoke with a **second spoke** holding a second Azure ML workspace (two regions, both on
the validated `Standard_NC24ads_A100_v4` Spot baseline), plus a **standalone RTX 6000 Spot VM** running ComfyUI that the existing local
`comfyui` provider is pointed at. The RTX 6000 VM is outside the Azure ML contracts: the studio's SKU, price and quota rules stay
pinned to the A100 baseline and no code change is needed for it.

Run in this order; stop at the first failure and record it.

1. **Foundation, no compute.** `foundation.yml` plan and review (no deletes or replacements), then the separately approved apply for both regions. Confirm compute is absent and the state matches the reviewed plan. Then set the `Studio__...` settings from the informational outputs ([app-service-settings.md](app-service-settings.md)) and open `https://<site>/health-ui` until it reports `ready`.
2. **Identity, DNS, RBAC.** Entra bootstrap per `docs\operations\entra-bootstrap.md`; verify the host managed identity resolves every private endpoint privately and holds only the documented grants in each region; verify creator, admin, dual and unauthorized accounts.
3. **Release.** `release.yml` (gpu, server, and optionally trainer) then `promote.yml` per region; confirm each regional binding is written last and the host reads it.
4. **Migration (only if the v1 deployment is being moved).** Follow `infra\README.md` with a reviewed no-replacement plan; keep the legacy Stop path until verified.
5. **Hosted Start, one region.** Administration page: select release, Start with explicit approvals; confirm one private Spot node, no public IP, price and quota checks.
6. **One bounded generation** in that region with a short timeout; verify output location, library read, admission release.
7. **Second region and queue behavior.** Arm the second region; observe routing priority and headroom. Cross-region queue move is reproducible only if Spot capacity is actually unavailable in the first region; if it cannot be provoked, record it as not reproduced.
8. **Settings.** Change a per-region timeout and concurrency from the UI; confirm audit entries, the stale-version refusal, and that running work is not interrupted.
9. **Stop.** Stop all regions from the UI; verify compute, NAT and public IP absent in each, then run `recover.yml` / `Stop-Studio.ps1` separately and confirm it is idempotent.
10. **Standalone RTX 6000 ComfyUI VM.** Local-provider generation against it; separate from the Azure ML path. Stop the VM afterwards.
11. **Optional training.** Only with separate spend approval, and the hosted dataset-builder flag remains off until it passes.

Record for every case: command or action, region, identifiers, observed result, and anything that could not be reproduced. A
skipped live case is recorded as not run, never as passed. This offline result does not imply any live acceptance.
