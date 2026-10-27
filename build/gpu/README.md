# GPU artifact preparation

P2 separates build-time preparation from Azure publication. These tools run on a
build machine, not App Service or paid AML compute. They neither log into Azure,
create infrastructure, register assets, push images nor submit jobs. P4 wires
protected image builds and regional promotion. The current legacy operator remains
available until that cutover.

| Tool/input | Purpose |
| --- | --- |
| `prepare.py` | Export the reviewed Git commit into minimal WAN code, deterministic `code.tar`, all .NET-rendered probe workflows, image context and `stage.json` |
| `models.py` | One shared model implementation, used by the new build and the transitional legacy import; stage verified weights and pinned model cards |
| `verify.py` | Check staged bytes, run the real staged-source CPU probe in an already-local digest-addressed image, then seal `manifest.json` |
| `profiles.json` | Immutable upstream/base/H3 pins and the WAN runtime-file allowlist |
| `runtime-ltx.patch` | Experimental LTX no-paid-download guard, separated from old portal/submission patches |
| `requirements-build.lock` | Only the build tools' tracing dependencies, with exact versions and SHA-256 hashes |
| `requirements-runtime.lock` | Full Linux x86_64 / CPython 3.11 GPU dependency resolution, including the torch 2.8 / torchvision 0.23 / torchaudio 2.8 CUDA 12.8 pair |
| `wan-pinned-manifest.json` | The existing eight exact WAN model sizes/checksums/origins; no weights in Git |

Shared `telemetry.py` preserves preparation tracing; the legacy publisher forwards
to it rather than maintaining a second tracing configuration. Optional OTLP
exporters can be provided by the build environment; if configured but missing,
the existing helper emits an explicit warning.

The dependency lock is separate from `azureml\uv.lock`, which still supports the
legacy Azure publisher. The new build tools do not need the Azure ML/identity/blob
submission SDKs. Neither lock contains a registry URL or credentials.

## Build-only steps

The following are operator/pipeline commands, not steps performed by this phase.
Image builds, real weight downloads and publication still require approval.

1. Supply a Git repository containing upstream commit
   `dc0d29031b73a5ba7376d910adb7a58c206f32b1`. The exporter reads committed bytes,
   never a dirty checkout, disables LFS smudge/lazy fetching and rejects an
   unresolved LFS pointer in selected code. It does not clone/fetch automatically.
2. Check/export the workflow corpus from the production .NET renderer. The checked-in
   `contracts\fixtures\wan-workflows\` is drift-checked against every `WanModes.All`
   entry; Draft and Fast are separate cases even though they share a template.
3. Stage into a **new** output directory:

```powershell
python build\gpu\prepare.py --source '<pinned-source-repository>' `
  --workflows contracts\fixtures\wan-workflows --output '<new-stage-directory>'
python build\gpu\models.py --models '<verified-model-cache>' `
  --licenses '<reviewed-pinned-model-card-cache>' --output '<model-snapshot-directory>'
```

With no `--download`, the second command never contacts model metadata, weight or
licence endpoints. Each cache hit is checked by size and SHA-256; a corrupt supplied
file fails rather than silently falling back to a download. Hard links are used
where possible, with a copy fallback across volumes. Verification rehashes staged
bytes, so modifying a linked cache cannot silently produce a valid manifest.

The licence cache supplies `<owner>--<repository>--README.md` for each manifest
repository at its exact pinned revision. Preserve/review the model's licence and
applicable use terms; tooling does not accept gated model terms on your behalf.
`--download` explicitly permits acquiring missing weights and cards into the caches.
Build output retains the cards plus repository/revision/source records; it does not
infer every model's licence from the old portal.

4. In a separately approved image-build job, build **only** the staged `image\`
   context using its Dockerfile. Supply the one approved HTTPS feed through
   `--secret id=pip-index,env=PIP_INDEX_URL`, not an image build argument.
   Pass `RUNTIME_LOCK_SHA256=<SHA256-of-requirements-runtime.lock>` as a build argument.
   For authenticated feeds, use a BuildKit `netrc` secret from the user's config;
   never put credentials in a feed argument. No extra index or TLS bypass is allowed.
   The base is digest-pinned, package wheels are hash-locked and checked with
   `pip check`, and no mutable apt installation is added. The image build fails if
   the base is not the lock's expected CPython 3.11 target.
5. Obtain the **registry manifest digest** from the approved build output. A Docker
   image-config ID is not interchangeable with that digest. For multi-platform/OCI
   outputs the image identifier must resolve to the exact local Linux amd64 image
   that will be promoted. This phase does not publish it to obtain a digest.
6. Run:

```powershell
python build\gpu\verify.py --stage '<stage-directory>' `
  --assets '<model-snapshot-directory>' --image '<repository>@sha256:<manifest-digest>'
```

Verification uses `--pull never`, no network, no GPU flags, a read-only root
filesystem and read-only code/workflow/context mounts. Only isolated tmpfs scratch
is writable. It checks the image lock label and in-image lock bytes, imports the
actual staged ComfyUI `main` before `nodes` on CPU, and checks required node classes
for **all** served modes. It rehashes inputs after the probe and writes no manifest
on failure. This is node/import compatibility validation, not a generation/GPU test.

The final manifest matches `GpuBuildManifest`, including `buildInputs` hashes for
the builder, Dockerfile, lock, pins, workflow-probe code and runtime patch. The
normalized LF copies live separately in `build-inputs\`, outside the worker code.
Image-context files are copied from those exact reviewed input bytes. The
manifest has no customer IDs, local cache paths or registry hostname. Image digest,
code/model/licence hashes and build inputs determine identity; timeout/concurrency,
region and package mirror hostname do not. Model snapshots can be reused unchanged
when only code or operator settings change.

## Runtime payload

The production WAN bundle contains upstream ComfyUI source, MIT/NOTICE attribution
and exactly these upstream AML helpers:

```text
azureml/aml_project_layout.py
azureml/extra_model_paths.yaml
azureml/run_workflow_job.py
azureml/workflow_profiles.py
azureml/workflow_utils.py
```

It does not contain our .NET source, Python operator config/guards, submission/UI
code, customer configuration, build tooling, caches, root assets or weights.
Disabled custom nodes, source tests/CI and unused blueprint collections are omitted.
Legitimate nested `comfy\ldm\models` and `comfy_api\input` packages are retained,
including empty package markers. Code archive paths/order/modes/ownership/timestamps
are normalized; changing the local staging path does not change the archive.

Experimental LTX/H3 remain outside the production WAN release contract. Their
metadata and LTX fail-closed patch are retained here; legacy preparation continues
to support its experimental profiles. No H3 portal import is needed just to obtain
its source revision. There is no claim that the new WAN image lock validates those
different experimental environments.

## Checks and dependency maintenance

```powershell
python -m unittest discover -s build\gpu -p 'test_*.py' -v
python scripts\ci\test_build_identity.py
dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts --no-restore
pwsh -NoProfile -File scripts\ci\Test-StudioContracts.ps1
```

For the additional exact-source integration check, set `WAN_STUDIO_P2_UPSTREAM`
to the supplied Git repository. No hardcoded file count is an acceptance threshold.
These tests use tiny synthetic models and mocked Docker outcomes; the real source
fixture only stages/inspects code and applies the LTX patch, without executing CUDA.

A disposable source fixture can be prepared explicitly with Git (no checkout or LFS
weight acquisition). Use a fresh directory and fetch the complete commit rather
than a partial clone, because staging refuses lazy network fetching:

```powershell
git init '<new-source-fixture>'
git -C '<new-source-fixture>' fetch --depth=1 `
  https://github.com/jakeatmsft/azureml_vidgen_comfyui.git `
  dc0d29031b73a5ba7376d910adb7a58c206f32b1
$env:WAN_STUDIO_P2_UPSTREAM = '<absolute-source-fixture>'
python -m unittest discover -s build\gpu -p 'test_*.py' -v
Remove-Item Env:WAN_STUDIO_P2_UPSTREAM
```

Regenerate structural schemas/probe workflows through the documented
`WAN_STUDIO_UPDATE_CONTRACTS=1` .NET checks in `contracts\README.md`; CI only checks
committed fixtures. The required `buildInputs` field was added before any P1
contract deployment; it intentionally invalidates older pre-release build fixtures.

The runtime lock was generated with uv 0.9.5:

```powershell
uv --no-config pip compile build\gpu\requirements-runtime.in `
  --python-version 3.11 --python-platform x86_64-manylinux_2_28 --no-binary blake3 `
  --generate-hashes --no-header --no-annotate --exclude-newer 2026-10-14 `
  --default-index '<one-approved-feed>' --native-tls `
  --output-file build\gpu\requirements-runtime.lock
uv --no-config pip compile build\gpu\pyproject.toml --python-version 3.11 --universal `
  --only-binary :all: --generate-hashes --no-header --no-annotate `
  --default-index '<one-approved-feed>' --native-tls `
  --output-file build\gpu\requirements-build.lock
```

The approved feed lacked blake3 wheel `.metadata` responses, so only its resolver
metadata came from the pinned source distribution; the image still requires binary
wheels. Review and remove any emitted `--hash=md5:` lines, retaining SHA-256 hashes
and exact resolved versions. This feed's old uv metadata cache did not support a
complete offline re-resolution (torch was unavailable in that cache); no public
fallback or insecure connection was used.

**Validation limits:** the exact source and offline build checks run on Windows.
Docker was not running, so no real image build/container import probe, Linux GPU
execution or weight download was performed. CPython/base-package compatibility and
the new resolved dependency set remain gated by the actual CPU image probe before
publication. These are new build-boundary checks, not the original application's
historical live Azure evidence.
