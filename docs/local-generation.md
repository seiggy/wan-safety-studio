# Local generation and storage (Aspire dev mode)

**Quick setup:** defaults are Azurite storage and ComfyUI at http://localhost:8188, so spire start works as is. To change anything run `.\scripts\Initialize-LocalDev.ps1` (see its examples); it writes the git-ignored `appsettings.Development.json`. A `comfyui-url` value saved from the Aspire dashboard overrides the file.

Local mode runs `app\dev_server.py` (no sign-in, never deploy it). Azure always uses Azure Storage; the options below apply only to local runs. Set them in `appsettings.Development.json` next to `apphost.cs` or as environment variables (`Storage__Backend=azurite`).

| Setting | Values | Notes |
|---|---|---|
| `Storage:Backend` | `local` (default), `azurite`, `azure` | Where datasets, labels and generated videos live |
| `Storage:Path` | folder | `local` only. A LAN share is just a mounted path: `\\server\share` or `/mnt/share` (NFS/SMB). Default `.dev-data\blob` |
| `Storage:Account` | account name | `azure` only; uses your `az login` (needs Blob Data Contributor) |
| `ComfyUi:Retrieval` | `api` (default), `share` | `api` downloads each finished video through ComfyUI's `/view`, so no shared storage is needed. `share` reads ComfyUI's output folder |
| `ComfyUi:OutputDir` | folder | `share` only: ComfyUI's output folder as mounted on this machine |
| `ComfyUi:Workflow` | file | Optional workflow path; default `app\generation\workflows\wan22_i2v.api.json` |

`azurite` starts the emulator through Aspire (persistent data volume). The `comfyui-url` parameter (dashboard) points at your ComfyUI.

## Workflow files (`app\generation\workflows\`)
Pulled from the pinned upstream (`jakeatmsft/azureml_vidgen_comfyui@dc0d290`, `azureml/workflows/image_to_video_wan22_i2v_api.json`, MIT). Default is the **fp8 scaled** variant: stock ComfyUI nodes only, no custom node.

| File | Use |
|---|---|
| `wan22_i2v_fp8.api.json` | **Import this into your ComfyUI server** to test the graph by hand (saves a `.webm`). |
| `wan22_i2v.api.json` | The studio's template (default `ComfyUi:Workflow`): the same graph with `{{placeholders}}` filled per job. A test keeps it in sync with the file above. |
| `wan22_i2v_upstream_fp8.api.json` | Upstream as shipped (also saves an animated WEBP). Reference only. |
| `wan22_i2v_gguf_q5km.api.json` | Optional smaller GGUF Q5_K_M variant (needs the ComfyUI-GGUF node, https://github.com/city96/ComfyUI-GGUF). Not used by the studio unless set as `ComfyUi:Workflow` after adding placeholders. |

Both are **API-format**: drag the JSON onto the ComfyUI canvas, or `POST` it to `/prompt` as `{"prompt": <file>}`.

### Models (names must match exactly, including case)
| Folder | File |
|---|---|
| `models/diffusion_models/` | `wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors` |
| `models/diffusion_models/` | `wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors` |
| `models/vae/` | `wan_2.1_vae.safetensors` |
| `models/text_encoders/` | `umt5_xxl_fp8_e4m3fn_scaled.safetensors` |

If yours are named differently, edit `unet_name`, `vae_name` and `clip_name` in the reference and template. Placeholders: `{{positive_prompt}}`, `{{negative_prompt}}`, `{{input_image}}`, `{{seed}}`, `{{width}}`, `{{height}}`, `{{length}}` (frames, 4n+1), `{{fps}}`, `{{filename_prefix}}`. Any node that saves a video (`.mp4/.webm/.mov/.mkv`) is picked up from ComfyUI's history.
## Flow
Submit -> upload reference image -> queue prompt -> poll history -> fetch the video (API or share) -> store `video-library/<job>/video.<ext>` + `meta.json` -> the Library and Vision "import from library" see it. Jobs run one at a time, matching the single GPU node.
## Fully local vs. a deployed Azure ML workspace
`Generation:Provider` gates the whole Azure ML path:

| Provider | What runs | Needs |
|---|---|---|
| `comfyui` (default) | `dev_server.py`: ComfyUI generation, vision API, Azurite/folder/Azure storage. **No Azure ML code or credentials are touched**; the UI shows "Local mode". | ComfyUI + workflow file |
| `azureml` | The Azure-backed portal through `scripts\Invoke-WanSafetyStudio.ps1 -Action Portal` (same checks as running it by hand), behind the same React UI. For machines too weak to run Wan locally. | `az login` to the studio tenant/subscription, completed Deploy/Prepare/Start, `Initialize-PortalAuth.ps1`. Optional `AzureMl:ConfigPath` and `AzureMl:CacheDirectory` |

Switch with `.\scripts\Initialize-LocalDev.ps1 -Provider azureml` (or `-Provider comfyui`), then `aspire start`. In `azureml` mode Aspire starts no Azurite or ComfyUI resources, and Entra sign-in is used (sign in at `http://localhost:51881`). The UI adapts from the `mode` field of `/api/status`.

## Python dependencies on a feed-only machine (CFS)
`uv.lock` is committed with public PyPI URLs so customers can restore it. On machines that must use an internal feed, plain `uv sync` fails or would rewrite the lock with internal URLs. Aspire therefore does not run `uv sync` (`WithUv(install: false)`); run once, and again after `uv.lock` changes:

```powershell
.\scripts\Install-DevPython.ps1            # exports hashes from uv.lock, installs via your configured index
.\scripts\Install-DevPython.ps1 -Index <feed-url>   # or name the index explicitly
```
Packages are hash-verified against the committed lock whichever index serves them. Never commit a `uv.lock` containing feed URLs; regenerate it only on a machine with public PyPI access. Pending that: the OTLP exporter dependency (telemetry to the Aspire dashboard) is not in the lock yet.

## NAS output over SMB
Set ComfyUI's output directory to the NAS share, then:
```powershell
.\scripts\Initialize-LocalDev.ps1 -Retrieval share -ComfyUiOutputDir '\\UNAS-Pro-8\ComfyUI'
# credentials: environment variables (or the dashboard prompt / user secrets) - never a committed file
$env:'Parameters__comfyui-smb-user' = '<user>'; $env:'Parameters__comfyui-smb-password' = '<password>'
aspire start
```
On Windows the API authenticates with `WNetAddConnection2` (no password on a command line); if Windows already holds a connection to that server with other credentials it is reused. On Linux/macOS mount the share and set `ComfyUi:OutputDir` to the mount.
