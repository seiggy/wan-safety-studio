# AzureML Job Submission

This workspace blocks AzureML's default local staging flow with `KeyBasedAuthenticationNotPermitted`. The supported path is:

1. Upload code and any reusable model bundles to `workspaceblobstore` with `--auth-mode login`.
2. Submit jobs that mount those remote URIs directly.
3. Let the job write outputs back to the default AzureML job output location.

## How The Submitters Work

- `azureml/register_and_submit.py` is the direct CLI. It resolves the workflow profile, environment, models input, and AzureML command job definition.
- `azureml/web_submit.py` is a thin UI wrapper over the same backend. If you upload workflow input images, it stores each blob under `$REMOTE_ROOT/web-inputs/...`, creates user-delegation SAS URLs, and passes those URLs into `register_and_submit.py`.
- When `--code-path` is set, the job mounts the uploaded repo snapshot and runs from that mounted folder instead of letting AzureML package local code.
- When `--models-path` is omitted, the submit script inspects the selected workflow, copies only the referenced model files plus `src/models/configs/*.yaml`, registers that bundle as a `uri_folder` data asset, and mounts it as the job's `models` input.
- The CLI can also register or reuse that mounted models bundle as `custom_model`; use `--models-asset-kind model` for the recommended LTX workflow-bundle path described below.
- If you already have a registered models asset for the requested version, `--skip-data-upload` reuses `azureml:<models-name>:<version>` instead of uploading it again.

## Prerequisites

Authenticate first:

```bash
az login
az account set --subscription 6025ba02-1dfd-407f-b358-88f811c7c7aa
```

The Python helpers use `DefaultAzureCredential`. Local development typically works through `az login`; hosted deployments can also use managed identity via `AZURE_CLIENT_ID`.

## Defaults

```bash
export AZUREML_COMPUTE=wannd40spotpe
export ACCOUNT=sc1mlworkspace1687429816
export CONTAINER=azureml-blobstore-a2f597f4-1a31-4e25-9c39-aa7e2d3b6df0
export REMOTE_ROOT=codex/comfyui
export VERSION=$(date -u +%Y%m%d%H%M%S)

export CODE_PREFIX=$REMOTE_ROOT/code/$VERSION
export WAN_MODELS_PREFIX=$REMOTE_ROOT/models/wan/$VERSION
export LTX_MODELS_PREFIX=$REMOTE_ROOT/models/ltx/$VERSION
export H3_MODELS_PREFIX=$REMOTE_ROOT/models/minimax-h3/$VERSION
```

## Register Environments

If you already have environment IDs such as `azureml:comfyui-ltx-cu128:<version>`, reuse them with `--environment-id` and skip this step.

The WAN, LTX, and MiniMax H3 profiles use separate AzureML environment names even if you register them from the same base image:

```bash
az acr build \
  --registry sc1mlcr \
  --image comfyui-runtime:$VERSION \
  --file azureml/context/Dockerfile \
  azureml/context

python - <<'PY'
from azure.ai.ml import MLClient
from azure.ai.ml.entities import Environment
from azure.identity import AzureCliCredential
import os

client = MLClient(
    AzureCliCredential(),
    "6025ba02-1dfd-407f-b358-88f811c7c7aa",
    "sc1-ml1",
    "sc1ml1",
)
version = os.environ["VERSION"]
image = f"sc1mlcr.azurecr.io/comfyui-runtime:{version}"

for env_name in ("comfyui-wan-cu128", "comfyui-ltx-cu128", "comfyui-minimax-h3-cu128"):
    env = Environment(
        name=env_name,
        version=version,
        image=image,
        description=f"ComfyUI runtime for {env_name}.",
    )
    created = client.environments.create_or_update(env)
    print(created.id)
PY
```

`register_and_submit.py` can also register an environment for the current version when you omit `--environment-id`. Use `--environment-image` to register from a prebuilt image, or omit it to build from `azureml/context/`.

### Build A Preloaded LTX Image

If you want the exact LTX first/last-frame workflow models baked into the runtime image instead of downloaded during job startup, build the preloaded image and register a matching environment version:

```bash
ACR_NAME=sc1mlcr \
WRITE_LATEST_ENV_VERSION=1 \
./azureml/build_ltx_preloaded_image.sh
```

The helper:

- reads `azureml/workflows/ltx_2_first_last_ttp_workflow.json`
- collects the exact model refs used by that graph
- builds `sc1mlcr.azurecr.io/comfyui-ltx-runtime-preloaded:<tag>` on top of the current `comfyui-runtime:<base-tag>`
- registers `comfyui-ltx-cu128:<tag>` to the preloaded image
- registers `comfyui-wan-cu128:<tag>` to the original base image when `REGISTER_WAN_ENV=1`
- optionally writes the new shared version into `azureml/.latest_env_version`

At runtime, `bootstrap_ltx_and_run.py` now prefers the image-baked models under `COMFY_PRELOADED_MODELS_DIR`, then overlays any mounted models input, and only falls back to network downloads for anything still missing.

Current status on `wannd40spot`: the verified working path is still the base runtime `azureml:comfyui-ltx-cu128:20260521201834` plus a mounted minimal workflow bundle. The preloaded LTX environment version `20260603194230` remains opt-in until it is rebuilt and revalidated on that cluster.

## Upload Code

Upload a repo snapshot once per version and reference it through `--code-path`:

```bash
rsync -a --exclude-from=.amlignore ./ azureml/.upload_code/repo/
cp azureml/context/requirements-azureml-runtime.txt azureml/.upload_code/repo/azureml/context/

az storage blob upload-batch \
  --account-name "$ACCOUNT" \
  --auth-mode login \
  --destination "$CONTAINER" \
  --destination-path "$CODE_PREFIX" \
  --source azureml/.upload_code/repo \
  --overwrite true

az storage blob upload \
  --account-name "$ACCOUNT" \
  --auth-mode login \
  --container-name "$CONTAINER" \
  --name "$CODE_PREFIX/azureml/extra_model_paths.yaml" \
  --file azureml/extra_model_paths.yaml \
  --overwrite true
```

The resulting code URI is:

- `azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/`

## Choose A Models Input Strategy

### Reuse Remote Model Roots

Keep each model family under a different datastore root so AzureML only mounts the files required by the selected profile:

- WAN: `azureml://datastores/workspaceblobstore/paths/$WAN_MODELS_PREFIX/`
- LTX: `azureml://datastores/workspaceblobstore/paths/$LTX_MODELS_PREFIX/`
- MiniMax H3: `azureml://datastores/workspaceblobstore/paths/$H3_MODELS_PREFIX/`

Each remote root should look like a ComfyUI `models/` folder, for example `diffusion_models/...`, `vae/...`, `checkpoints/...`, `text_encoders/...`, and any needed `configs/*.yaml`.

Example upload for a prepared profile-specific models folder:

```bash
az storage blob upload-batch \
  --account-name "$ACCOUNT" \
  --auth-mode login \
  --destination "$CONTAINER" \
  --destination-path "$WAN_MODELS_PREFIX" \
  --source /path/to/prepared-models/wan \
  --overwrite true
```

### Auto-Stage A Minimal Models Asset

If you omit `--models-path`, `register_and_submit.py` reads the selected workflow JSON, finds the referenced model filenames, copies only those files from local `src/models/`, includes `src/models/configs/*.yaml`, registers the bundle as a `uri_folder` data asset, and mounts that asset into the job.

This is the simplest option when you are submitting from a machine that already has the needed local models.

Pass `--models-asset-kind model` if you want that minimal bundle registered as `custom_model` instead of `uri_folder`. The tested LTX path below uses the `custom_model` form.

For MiniMax H3, first download the exact four-file CUDA 12.8-compatible bundle into `src/models/`:

```bash
python azureml/context/download_model_manifest.py \
  --manifest azureml/minimax_h3_model_manifest.json \
  --output-root src/models
```

The `minimax_h3` profile defaults to a minimal mounted `custom_model` asset and launches ComfyUI with `--lowvram --disable-smart-memory --reserve-vram 2`. It also defaults to 736x416, 56 frames at 24 fps, and one batch to leave activation headroom on AzureML GPUs.

The four workflow files are:

- `diffusion_models/minimax_h3_fl2va_pruned_fp8_scaled.safetensors`
- `text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors`
- `vae/minimax_h3_video_vae_fp16.safetensors`
- `vae/minimax_h3_audio_vae_fp32.safetensors`

See [`../docs/minimax-h3.md`](../docs/minimax-h3.md) for the complete model, private-network, and troubleshooting guide.

## Recommended MiniMax H3 Path

Use the following configuration for MiniMax H3 on this workspace:

- workflow `azureml/workflows/image_to_video_minimax_h3_api.json`
- compute `wannd40spotpe` on `ml-vnet/subnets/aml-compute`
- a minimal `custom_model` asset containing only the four files above
- the `comfyui-minimax-h3-cu128` environment family
- the profile defaults of 736x416, 56 frames, 24 fps, and batch size 1

The workspace, Blob, and File private endpoints are on `ml-vnet/subnets/aml-private-endpoints`. Because workspace storage has no public data-plane access, code/model staging and artifact downloads must run from a host connected to `ml-vnet`. AzureML jobs on `wannd40spotpe` mount those inputs through private DNS.

## Recommended LTX Path

For the first/last-frame LTX workflow on `wannd40spot`, the current working path is:

- workflow `azureml/workflows/ltx_2_first_last_ttp_workflow.json`
- environment `azureml:comfyui-ltx-cu128:20260521201834`
- code input `azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/`
- models input registered as `custom_model`, for example `azureml:comfyui-ltx-workflow-models:<asset-version>`
- CLI submission through `azureml/register_and_submit.py` with `--models-asset-kind model`

Why this is the recommended path:

- it mounts only the workflow-required LTX files instead of a larger profile root
- it avoids runtime Hugging Face downloads for the required LTX files
- it avoids the current preloaded-image issue on `wannd40spot`
- the runtime already redirects temp and cache data to node-local scratch and uploads breadcrumbs into `user_logs`

Useful user logs for this flow:

- `user_logs/bootstrap_progress.jsonl`
- `user_logs/run_workflow_job_preimport.log`
- `user_logs/std_log.txt`

The portal also supports an LTX image-to-video profile backed by `src/blueprints/Image to Video (LTX-2.3).json`. It uses the same `comfyui-ltx-cu128` environment family and mounted `ltx` model root, but requires only one reference image instead of start/end frames.

## Submit Jobs From The CLI

Recommended LTX 2 first/last-frame using uploaded code, a registered minimal `custom_model` asset, and signed start/end image URLs:

```bash
python azureml/register_and_submit.py \
  --profile ltx \
  --version "$VERSION" \
  --environment-id "azureml:comfyui-ltx-cu128:20260521201834" \
  --code-path "azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/" \
  --models-path "azureml:comfyui-ltx-workflow-models:<asset-version>" \
  --models-asset-kind model \
  --compute "$AZUREML_COMPUTE" \
  --clip-text-encode-prompt "cinematic harbor city slowly transitions from the first frame into the last frame" \
  --start-image-url "<signed-start-image-url>" \
  --start-image-filename start.png \
  --end-image-url "<signed-end-image-url>" \
  --end-image-filename end.png \
  --stream
```

WAN 2.2 image-to-video using uploaded code, a remote WAN models root, and a signed input image URL:

```bash
python azureml/register_and_submit.py \
  --profile wan \
  --version "$VERSION" \
  --environment-id "azureml:comfyui-wan-cu128:$VERSION" \
  --code-path "azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/" \
  --models-path "azureml://datastores/workspaceblobstore/paths/$WAN_MODELS_PREFIX/" \
  --compute "$AZUREML_COMPUTE" \
  --input-image-url "<signed-image-url>" \
  --input-image-filename input.png \
  --stream
```

MiniMax H3 image-to-video with native audio using a registered minimal model asset:

```bash
python azureml/register_and_submit.py \
  --profile minimax_h3 \
  --version "$VERSION" \
  --environment-id "azureml:comfyui-minimax-h3-cu128:$VERSION" \
  --code-path "azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/" \
  --models-path "azureml:comfyui-minimax-h3-models:$VERSION" \
  --models-asset-kind model \
  --compute wannd40spotpe \
  --positive-prompt "the subject turns toward camera; audio: soft wind and synchronized footsteps" \
  --input-image-url "<signed-image-url>" \
  --input-image-filename input.png \
  --stream
```

LTX 2 first/last-frame using uploaded code and an auto-staged minimal `custom_model` asset from local `src/models/`:

```bash
python azureml/register_and_submit.py \
  --profile ltx \
  --version "$VERSION" \
  --environment-id "azureml:comfyui-ltx-cu128:20260521201834" \
  --code-path "azureml://datastores/workspaceblobstore/paths/$CODE_PREFIX/" \
  --models-asset-kind model \
  --compute "$AZUREML_COMPUTE" \
  --clip-text-encode-prompt "cinematic harbor city slowly transitions from the first frame into the last frame" \
  --start-image-url "<signed-start-image-url>" \
  --start-image-filename start.png \
  --end-image-url "<signed-end-image-url>" \
  --end-image-filename end.png \
  --stream
```

## Local Submit Portal

Run the lightweight upload-and-submit UI:

```bash
python azureml/web_submit.py --default-profile ltx --gallery-profile ltx
```

The portal:

- lets you switch between WAN, MiniMax H3, LTX image-to-video, and LTX first/last-frame profiles from the UI
- labels the LTX primary prompt field as `CLIP Text Encode (Prompt)` to match the workflow node
- requires start/end frame uploads for LTX first/last-frame interpolation jobs
- uploads workflow input images to blob storage and passes SAS URLs into the shared submit backend
- reads `azureml/.current_version` for the default code version
- reads `azureml/.latest_env_version` for the shared default environment version
- currently follows the shared default models-input behavior; use the CLI when you need `--models-asset-kind model` for the recommended LTX `custom_model` workflow bundle
- keeps the gallery focused on completed LTX jobs by default

Useful overrides:

```bash
python azureml/web_submit.py \
  --default-profile wan \
  --gallery-profile ltx \
  --wan-environment-id "azureml:comfyui-wan-cu128:20260518145823" \
  --wan-models-version 20260518231130 \
  --ltx-environment-id "azureml:comfyui-ltx-cu128:20260521201834" \
  --ltx-models-version 20260521093556
```

## Outputs

AzureML writes the `generated` output to `workspaceblobstore/azureml/<job-name>/generated/`. The portal gallery reads from that same location.

Download job artifacts after completion:

```bash
az ml job download \
  --resource-group sc1-ml1 \
  --workspace-name sc1ml1 \
  --name <job-name> \
  --output-name generated \
  --download-path ./azureml-job-output
```

The download command must run from a host with routing and private DNS access to `ml-vnet`; otherwise private workspace storage returns `AuthorizationFailure`.
