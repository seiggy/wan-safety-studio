# Local generation and storage (Aspire dev mode)

**Quick setup:** defaults are Azurite storage and ComfyUI at http://localhost:8188, so spire start works as is. To change anything run `.\scripts\Initialize-LocalDev.ps1` (see its examples); it writes the git-ignored `appsettings.Development.json`. A `comfyui-url` value saved from the Aspire dashboard overrides the file.

Local mode runs the .NET server (`src\VideoSafetyStudio.Server`, no sign-in, never deploy it). Azure always uses Azure Storage; the options below apply only to local runs. Set them in `appsettings.Development.json` next to `AppHost.cs` or as environment variables (`Storage__Backend=azurite`).

| Setting | Values | Notes |
|---|---|---|
| `Storage:Backend` | `local` (default), `azurite`, `azure` | Where datasets, labels and generated videos live |
| `Storage:Path` | folder | `local` only. A LAN share is just a mounted path: `\\server\share` or `/mnt/share` (NFS/SMB). Default `.dev-data\blob` |
| `Storage:Account` | account name | `azure` only; uses your `az login` (needs Blob Data Contributor) |
| `ComfyUi:Retrieval` | `api` (default), `share` | `api` downloads each finished video through ComfyUI's `/view`, so no shared storage is needed. `share` reads ComfyUI's output folder |
| `ComfyUi:OutputDir` | folder | `share` only: ComfyUI's output folder as mounted on this machine |
| `ComfyUi:WorkflowsDir` | folder | Optional folder of workflow templates (same file names as `src\VideoSafetyStudio.Server\Workflows`); default is the folder shipped with the server |

`azurite` starts the emulator through Aspire (persistent data volume). The `comfyui-url` parameter (dashboard) points at your ComfyUI.

## Workflow files (`src\VideoSafetyStudio.Server\Workflows\`)
Pulled from the pinned upstream (`jakeatmsft/azureml_vidgen_comfyui@dc0d290`, `azureml/workflows/image_to_video_wan22_i2v_api.json`, MIT). Default is the **fp8 scaled** variant: stock ComfyUI nodes only, no custom node.

| File | Use |
|---|---|
| `/api/modes/{mode}/workflow` | **Import this into your ComfyUI server** to test a mode by hand: returns the rendered API-format graph with example inputs. |
| `wan22_i2v.api.json`, `wan22_i2v_lightning.api.json`, `wan22_ti2v_5b.api.json` | The studio's templates (one per quality mode, see below): the graphs with `{{placeholders}}` filled per job. |

| `wan22_i2v_gguf_q5km.api.json`, `wan22_i2v_upstream_fp8.api.json` | Reference only (GGUF variant needs the ComfyUI-GGUF node; upstream as shipped). Not used by the studio. |

Both are **API-format**: drag the JSON onto the ComfyUI canvas, or `POST` it to `/prompt` as `{"prompt": <file>}`.

### Models (names must match exactly, including case)
| Folder | File |
|---|---|
| `models/diffusion_models/` | `wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors` |
| `models/diffusion_models/` | `wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors` |
| `models/vae/` | `wan_2.1_vae.safetensors` |
| `models/text_encoders/` | `umt5_xxl_fp8_e4m3fn_scaled.safetensors` |

If yours are named differently, set them (no file edits): `.\scripts\Initialize-LocalDev.ps1 -UnetHigh <file> -UnetLow <file> -Vae <file> -Clip <file>` (settings `ComfyUi:UnetHigh/UnetLow/Vae/Clip`). The template uses the placeholders `{{unet_high}}`, `{{unet_low}}`, `{{vae_name}}`, `{{clip_name}}`; the reference file keeps the defaults above. Other placeholders: `{{positive_prompt}}`, `{{negative_prompt}}`, `{{input_image}}`, `{{seed}}`, `{{width}}`, `{{height}}`, `{{length}}` (frames, 4n+1), `{{fps}}`, `{{filename_prefix}}`. Any node that saves a video (`.mp4/.webm/.mov/.mkv`) is picked up from ComfyUI's history.
## Flow
Submit -> upload reference image -> queue prompt -> poll history -> fetch the video (API or share) -> store `video-library/<job>/video.<ext>` + `meta.json` -> the Library and Vision "import from library" see it. Jobs run one at a time, matching the single GPU node.
## Fully local vs. a deployed Azure ML workspace
`Generation:Provider` gates the whole Azure ML path:

| Provider | What runs | Needs |
|---|---|---|
| `comfyui` (default) | The .NET server: ComfyUI generation, vision API (when enabled), Azurite/folder/Azure storage. **No Azure ML code or credentials are touched**; the UI shows "Local mode". | ComfyUI + workflow file |
| `azureml` | The Azure-backed portal through `scripts\Invoke-WanSafetyStudio.ps1 -Action Portal` (same checks as running it by hand), behind the same React UI. For machines too weak to run Wan locally. | `az login` to the studio tenant/subscription, completed Deploy/Prepare/Start, `Initialize-PortalAuth.ps1`. Optional `AzureMl:ConfigPath` and `AzureMl:CacheDirectory` |

Switch with `.\scripts\Initialize-LocalDev.ps1 -Provider azureml` (or `-Provider comfyui`), then `aspire start`. In `azureml` mode Aspire starts no Azurite or ComfyUI resources, and Entra sign-in is used (sign in at `http://localhost:51881`). The UI adapts from the `mode` field of `/api/status`.

## NAS output over SMB
Set ComfyUI's output directory to the NAS share, then:
```powershell
.\scripts\Initialize-LocalDev.ps1 -Retrieval share -ComfyUiOutputDir '\\UNAS-Pro-8\ComfyUI'
# credentials: environment variables (or the dashboard prompt / user secrets) - never a committed file
$env:'Parameters__comfyui-smb-user' = '<user>'; $env:'Parameters__comfyui-smb-password' = '<password>'
aspire start
```
On Windows the API authenticates with `WNetAddConnection2` (no password on a command line); if Windows already holds a connection to that server with other credentials it is reused. On Linux/macOS mount the share and set `ComfyUi:OutputDir` to the mount.

## Timing and telemetry
Each job opens ComfyUI's websocket **before** queueing and times every node, then reports:
- **Stages** (`load_models`, `text_encode`, `image_to_latent`, `model_setup`, `sample_high_noise`, `sample_low_noise`, `vae_decode`, `save_video`): seconds, sampler steps and seconds per step, and whether nodes were cached (ComfyUI skips cached loaders, so `load_models` is large only on the first job or after a model swap).
- **Phases** (wall clock): `upload_image`, `comfyui`, `retrieve`, `store_video`; plus `appQueueSeconds` (time waiting behind earlier jobs here) and `totalSeconds`.
- **Where it goes:** the Aspire dashboard (traces: a `generation.job` span with one `stage.*` child per node with the real start/end times; metrics `wan.generation.stage.duration`, `wan.generation.sampler.step.duration`, `wan.generation.job.duration` with `width`, `height`, `frames` attributes) and each video's `meta.json` / `/api/gallery` item under `timing`. The shape attributes let you compare 5 s (81 frames) with 10 s (161 frames) runs.
- If the websocket cannot connect the job still runs, with no stage breakdown.
- The Azure ML portal does not emit this yet.

## Quality modes
The Create view's **Quality** control picks a workflow template and sampler settings (`GET /api/modes` lists them; `GET /api/modes/{mode}/workflow` returns the API-format graph with example inputs, ready to import into ComfyUI by hand). Settings follow the Comfy-Org templates `video_wan2_2_14B_i2v` and `video_wan2_2_5B_ti2v`.

| Mode | Model | Sampling | Template |
|---|---|---|---|
| Draft | 14B high/low + lightx2v 4-step LoRAs | 4 steps (2 + 2), CFG 1, shift 5, euler, 16 fps | `wan22_i2v_lightning.api.json` |
| Fast | same | 7 steps (3 high + 4 low), CFG 1, shift 5, euler, 16 fps | `wan22_i2v_lightning.api.json` |
| Turbo | Wan 2.2 TI2V-5B (single model, own VAE) | one KSampler, 20 steps, CFG 5, shift 8, uni_pc, 24 fps | `wan22_ti2v_5b.api.json` |
| Prime | 14B high/low, no LoRA | 20 steps (10 + 10), CFG 3.5, shift 5, euler, 16 fps | `wan22_i2v.api.json` |

Prime now follows the Comfy-Org recommendation (shift 5); the pinned upstream workflow used shift 8.

Extra files for the new modes (names must match exactly):

| Folder | File | Source |
|---|---|---|
| `models/loras/` | `wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors`, `wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors` | `Comfy-Org/Wan_2.2_ComfyUI_Repackaged` -> `split_files/loras/` |
| `models/diffusion_models/` | `Wan2_2-TI2V-5B_fp8_e5m2_scaled_KJ.safetensors` | `Kijai/WanVideo_comfy_fp8_scaled` -> `TI2V/` |
| `models/vae/` | `wan2.2_vae.safetensors` | `Comfy-Org/Wan_2.2_ComfyUI_Repackaged` -> `split_files/vae/` (the 5B model needs this VAE, not `wan_2.1_vae`) |

Override names with `ComfyUi:LoraHigh`, `LoraLow`, `Unet5B`, `Vae5B` (see `Initialize-LocalDev.ps1`). `ComfyUi:WorkflowsDir` points at a folder of your own templates with the same file names; the old single-file `ComfyUi:Workflow` setting is gone. New placeholders: `{{steps}}`, `{{split_step}}`, `{{cfg}}`, `{{shift}}`, `{{sampler}}`, `{{lora_high}}`, `{{lora_low}}`, `{{unet_5b}}`, `{{vae_5b}}`. A mode whose template is missing shows as unavailable.

## Waiting and queue spans
Each job trace now starts at **submit**: `stage.waiting` (submit -> this server starts the job, i.e. time behind earlier jobs) and `stage.comfyui_queue` (prompt accepted -> ComfyUI `execution_start`), then the ComfyUI stages. `meta.json` and the metrics carry both as phases (`waiting`, `comfyui_queue`) plus the mode (`wan.mode`, `wan.steps` tags), so you can compare sequential vs parallel nodes and modes.