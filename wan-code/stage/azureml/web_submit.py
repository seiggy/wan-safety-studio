#!/usr/bin/env python3

from __future__ import annotations

import argparse
import asyncio
import base64
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
import html
import itertools
import json
import random
import re
from pathlib import Path, PurePosixPath
import shutil
import tempfile
import time
from types import SimpleNamespace
from urllib.parse import unquote, urlparse
import uuid

from aiohttp import web
from PIL import Image
from azure.storage.blob import BlobSasPermissions, BlobServiceClient, generate_blob_sas

try:
    from .credential import build_credential
    from . import register_and_submit as aml_submit
    from .workflow_profiles import (
        PROFILES,
        ImageInputSpec,
        WorkflowProfile,
        duration_seconds_to_length,
        get_profile,
    )
    from . import workflow_utils
except ImportError:
    from credential import build_credential
    import register_and_submit as aml_submit
    from workflow_profiles import (
        PROFILES,
        ImageInputSpec,
        WorkflowProfile,
        duration_seconds_to_length,
        get_profile,
    )
    import workflow_utils


GALLERY_LIMIT = 50
GALLERY_SCAN_LIMIT = 200
GALLERY_PAYLOAD_CACHE_SECONDS = 60
GALLERY_SOURCE_CACHE_SECONDS = 15 * 60
VIDEO_SUFFIXES = (".mp4", ".webm", ".mov", ".mkv")
POSTER_SUFFIXES = (".webp", ".png", ".jpg", ".jpeg")
MINIMAX_H3_PROFILE_KEY = "minimax_h3"
MINIMAX_H3_COMFY_REVISION = "95d755cd8107a72258d452b5d3657273d571f07d"
MINIMAX_H3_MODEL_REPO = "Comfy-Org/MiniMax-H3"
MINIMAX_H3_MODEL_FILES = (
    "diffusion_models/minimax_h3_fl2va_pruned_fp8_scaled.safetensors",
    "text_encoders/qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors",
    "vae/minimax_h3_video_vae_fp16.safetensors",
    "vae/minimax_h3_audio_vae_fp32.safetensors",
)
MINIMAX_H3_LAUNCHER = (
    'import runpy,sys; '
    'from pathlib import Path; '
    'sys.argv.extend(["--lowvram","--disable-smart-memory","--reserve-vram","2"]); '
    'runpy.run_path(str(Path(__file__).with_name("comfy_main.py")),run_name="__main__")'
)


@dataclass(frozen=True)
class ProfileSettings:
    key: str
    label: str
    description: str
    prompt_label: str
    workflow: str
    environment_name: str
    environment_id: str
    environment_version: str
    models_name: str
    models_path: str
    models_asset_kind: str
    models_version: str
    experiment_name: str
    display_name_prefix: str
    width: int
    height: int
    fps: float
    steps: int | None
    cfg: float | None
    sampler_name: str | None
    scheduler: str | None
    denoise: float | None
    batch_size: int
    filename_prefix: str
    duration_seconds: float
    duration_step_seconds: float
    negative_prompt: str
    length_mode: str
    frame_quantum: int
    supports_image: bool
    requires_image: bool
    image_inputs: tuple[ImageInputSpec, ...]


@dataclass(frozen=True)
class AppSettings:
    host: str
    port: int
    subscription_id: str
    resource_group: str
    workspace_name: str
    compute: str
    code_path: str
    code_version: str
    storage_account: str
    storage_container: str
    upload_storage_account: str
    upload_storage_container: str
    upload_prefix: str
    gallery_storage_account: str
    gallery_storage_container: str
    gallery_output_datastore: str | None
    gallery_output_prefix: str
    default_profile: str
    gallery_profile: str
    profiles: dict[str, ProfileSettings]
    sas_ttl_hours: int
    max_upload_mb: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--subscription-id", default=aml_submit.DEFAULT_SUBSCRIPTION_ID)
    parser.add_argument("--resource-group", default=aml_submit.DEFAULT_RESOURCE_GROUP)
    parser.add_argument("--workspace-name", default=aml_submit.DEFAULT_WORKSPACE_NAME)
    parser.add_argument(
        "--compute",
        default=aml_submit.DEFAULT_COMPUTE,
        required=aml_submit.DEFAULT_COMPUTE is None,
        help=aml_submit.COMPUTE_HELP,
    )
    parser.add_argument("--default-profile", default=getattr(aml_submit, "DEFAULT_PROFILE", "ltx"), choices=sorted(PROFILES))
    parser.add_argument("--gallery-profile", default="ltx", choices=sorted(PROFILES))
    parser.add_argument("--environment-id", default=None)
    parser.add_argument("--environment-version", default=None)
    parser.add_argument("--workflow", default=None)
    parser.add_argument("--code-path", default=None)
    parser.add_argument("--code-version", default=None)
    parser.add_argument("--models-path", default=None)
    parser.add_argument("--models-asset-kind", choices=aml_submit.MODELS_ASSET_KINDS, default=None)
    parser.add_argument("--models-version", default=None)
    parser.add_argument("--remote-root", default="codex/comfyui")
    parser.add_argument("--storage-account", default="sc1mlworkspace1687429816")
    parser.add_argument(
        "--storage-container",
        default="azureml-blobstore-a2f597f4-1a31-4e25-9c39-aa7e2d3b6df0",
    )
    parser.add_argument(
        "--upload-storage-account",
        default=None,
        help="Storage account for uploaded input images. Defaults to --storage-account.",
    )
    parser.add_argument(
        "--upload-storage-container",
        default=None,
        help="Blob container for uploaded input images. Defaults to --storage-container.",
    )
    parser.add_argument("--upload-prefix", default=None)
    parser.add_argument(
        "--gallery-storage-account",
        default=None,
        help="Storage account containing gallery videos. Defaults to --storage-account.",
    )
    parser.add_argument(
        "--gallery-storage-container",
        default=None,
        help="Blob container containing gallery videos. Defaults to --storage-container.",
    )
    parser.add_argument(
        "--gallery-output-datastore",
        default=None,
        help="AzureML datastore where new web jobs write generated video output.",
    )
    parser.add_argument("--gallery-output-prefix", default="video-library")
    parser.add_argument("--width", type=int, default=None)
    parser.add_argument("--height", type=int, default=None)
    parser.add_argument("--fps", type=float, default=None)
    parser.add_argument("--steps", type=int, default=None)
    parser.add_argument("--cfg", type=float, default=None)
    parser.add_argument("--sampler-name", default=None)
    parser.add_argument("--scheduler", default=None)
    parser.add_argument("--denoise", type=float, default=None)
    parser.add_argument("--batch-size", type=int, default=None)
    parser.add_argument("--filename-prefix", default=None)
    parser.add_argument("--duration-seconds", type=float, default=None)
    parser.add_argument("--negative-prompt", default=None)
    parser.add_argument("--sas-ttl-hours", type=int, default=48)
    parser.add_argument("--max-upload-mb", type=int, default=64)
    for profile_key in sorted(PROFILES):
        parser.add_argument(f"--{profile_key}-environment-id", default=None)
        parser.add_argument(f"--{profile_key}-environment-version", default=None)
        parser.add_argument(f"--{profile_key}-models-path", default=None)
        parser.add_argument(
            f"--{profile_key}-models-asset-kind",
            choices=aml_submit.MODELS_ASSET_KINDS,
            default=None,
        )
        parser.add_argument(f"--{profile_key}-models-version", default=None)
        parser.add_argument(f"--{profile_key}-workflow", default=None)
    args = parser.parse_args()
    args.compute = aml_submit.normalize_compute_name(args.compute)
    if not args.compute:
        parser.error(aml_submit.MISSING_COMPUTE_ERROR)
    return args


def read_version_file(path: Path, label: str) -> str:
    if not path.is_file():
        raise FileNotFoundError(f"Missing {label} file: {path}")
    value = path.read_text(encoding="utf-8").strip()
    if not value:
        raise ValueError(f"{label} file is empty: {path}")
    return value


def _profile_override(args: argparse.Namespace, profile_key: str, suffix: str) -> object:
    return getattr(args, f"{profile_key}_{suffix}")


def _resolve_profile_settings(
    args: argparse.Namespace,
    *,
    profile: WorkflowProfile,
    code_version: str,
    shared_models_version: str,
    shared_environment_version: str,
) -> ProfileSettings:
    is_default_profile = profile.key == args.default_profile
    workflow = (
        _profile_override(args, profile.key, "workflow")
        or (args.workflow if is_default_profile else None)
        or profile.workflow
    )
    models_version = (
        _profile_override(args, profile.key, "models_version")
        or (args.models_version if is_default_profile else None)
        or shared_models_version
    )
    models_path = (
        _profile_override(args, profile.key, "models_path")
        or (args.models_path if is_default_profile else None)
        or f"azureml://datastores/workspaceblobstore/paths/{args.remote_root}/models/{profile.models_subdir}/{models_version}/"
    )
    models_asset_kind = (
        _profile_override(args, profile.key, "models_asset_kind")
        or (args.models_asset_kind if is_default_profile else None)
        or profile.models_asset_kind
    )
    environment_version = (
        _profile_override(args, profile.key, "environment_version")
        or (args.environment_version if is_default_profile else None)
        or shared_environment_version
    )
    environment_id = (
        _profile_override(args, profile.key, "environment_id")
        or (args.environment_id if is_default_profile else None)
        or f"azureml:{profile.environment_name}:{environment_version}"
    )
    return ProfileSettings(
        key=profile.key,
        label=profile.label,
        description=profile.description,
        prompt_label=profile.prompt_label,
        workflow=workflow,
        environment_name=profile.environment_name,
        environment_id=environment_id,
        environment_version=environment_version,
        models_name=profile.models_name,
        models_path=models_path,
        models_asset_kind=models_asset_kind,
        models_version=models_version,
        experiment_name=profile.experiment_name,
        display_name_prefix=profile.display_name_prefix,
        width=args.width if is_default_profile and args.width is not None else profile.width,
        height=args.height if is_default_profile and args.height is not None else profile.height,
        fps=args.fps if is_default_profile and args.fps is not None else profile.fps,
        steps=args.steps if is_default_profile and args.steps is not None else profile.steps,
        cfg=args.cfg if is_default_profile and args.cfg is not None else profile.cfg,
        sampler_name=(
            args.sampler_name if is_default_profile and args.sampler_name is not None else profile.sampler_name
        ),
        scheduler=args.scheduler if is_default_profile and args.scheduler is not None else profile.scheduler,
        denoise=args.denoise if is_default_profile and args.denoise is not None else profile.denoise,
        batch_size=args.batch_size if is_default_profile and args.batch_size is not None else profile.batch_size,
        filename_prefix=(
            args.filename_prefix
            if is_default_profile and args.filename_prefix is not None
            else profile.filename_prefix
        ),
        duration_seconds=(
            args.duration_seconds
            if is_default_profile and args.duration_seconds is not None
            else profile.duration_seconds
        ),
        duration_step_seconds=profile.duration_step_seconds,
        negative_prompt=(
            args.negative_prompt
            if is_default_profile and args.negative_prompt is not None
            else profile.negative_prompt
        ),
        length_mode=profile.length_mode,
        frame_quantum=profile.frame_quantum,
        supports_image=profile.supports_image,
        requires_image=profile.requires_image,
        image_inputs=profile.image_inputs,
    )


def resolve_settings(args: argparse.Namespace) -> AppSettings:
    azureml_dir = Path(__file__).resolve().parent
    code_version = args.code_version or read_version_file(azureml_dir / ".current_version", "code version")
    shared_models_version = args.models_version or code_version
    shared_environment_version = args.environment_version or read_version_file(
        azureml_dir / ".latest_env_version",
        "environment version",
    )
    code_path = args.code_path or (
        f"azureml://datastores/workspaceblobstore/paths/{args.remote_root}/code/{code_version}/"
    )
    upload_prefix = args.upload_prefix or f"{args.remote_root}/web-inputs"
    profiles = {
        profile_key: _resolve_profile_settings(
            args,
            profile=get_profile(profile_key),
            code_version=code_version,
            shared_models_version=shared_models_version,
            shared_environment_version=shared_environment_version,
        )
        for profile_key in sorted(PROFILES)
    }
    return AppSettings(
        host=args.host,
        port=args.port,
        subscription_id=args.subscription_id,
        resource_group=args.resource_group,
        workspace_name=args.workspace_name,
        compute=args.compute,
        code_path=code_path,
        code_version=code_version,
        storage_account=args.storage_account,
        storage_container=args.storage_container,
        upload_storage_account=args.upload_storage_account or args.storage_account,
        upload_storage_container=args.upload_storage_container or args.storage_container,
        upload_prefix=upload_prefix,
        gallery_storage_account=args.gallery_storage_account or args.storage_account,
        gallery_storage_container=args.gallery_storage_container or args.storage_container,
        gallery_output_datastore=args.gallery_output_datastore,
        gallery_output_prefix=args.gallery_output_prefix.strip("/"),
        default_profile=args.default_profile,
        gallery_profile=args.gallery_profile,
        profiles=profiles,
        sas_ttl_hours=args.sas_ttl_hours,
        max_upload_mb=args.max_upload_mb,
    )


def utc_version() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")


def sanitize_filename(name: str) -> str:
    cleaned = Path(name).name
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "-", cleaned).strip(".-")
    if not cleaned:
        cleaned = "upload.png"
    if "." not in cleaned:
        cleaned += ".png"
    return cleaned[:120]


def profile_duration_to_length(profile: ProfileSettings, seconds: float) -> int:
    workflow_profile = get_profile(profile.key)
    return duration_seconds_to_length(workflow_profile, seconds, fps=profile.fps)


def validate_image(path: Path) -> dict[str, int | str]:
    with Image.open(path) as image:
        image.load()
        return {
            "width": image.width,
            "height": image.height,
            "format": image.format or path.suffix.lstrip(".").upper() or "UNKNOWN",
        }


def upload_blob(local_path: Path, blob_name: str, settings: AppSettings) -> str:
    blob_service = make_blob_service_client(
        settings,
        account_name=settings.upload_storage_account,
    )
    blob_client = blob_service.get_blob_client(
        container=settings.upload_storage_container,
        blob=blob_name,
    )
    with local_path.open("rb") as handle:
        blob_client.upload_blob(handle, overwrite=True)

    start_time = datetime.now(timezone.utc) - timedelta(minutes=5)
    expiry_time = start_time + timedelta(hours=settings.sas_ttl_hours)
    user_delegation_key = blob_service.get_user_delegation_key(start_time, expiry_time)
    return make_signed_blob_url(
        settings,
        blob_name,
        user_delegation_key=user_delegation_key,
        start_time=start_time,
        expiry_time=expiry_time,
        account_name=settings.upload_storage_account,
        container_name=settings.upload_storage_container,
    )


def make_blob_name(settings: AppSettings, filename: str) -> str:
    stamp = utc_version()
    date_prefix = stamp[:8]
    unique = uuid.uuid4().hex[:10]
    return f"{settings.upload_prefix}/{date_prefix}/{stamp}-{unique}-{filename}"


def build_submission_args(
    settings: AppSettings,
    profile: ProfileSettings,
    *,
    prompt: str,
    negative_prompt: str,
    duration_seconds: float,
    uploaded_images: dict[str, dict[str, object]],
) -> argparse.Namespace:
    version = utc_version()
    generated_output_path = None
    if settings.gallery_output_datastore:
        generated_output_path = (
            f"azureml://datastores/{settings.gallery_output_datastore}/paths/"
            f"{settings.gallery_output_prefix}/{version}/"
        )
    submit_args = argparse.Namespace(
        subscription_id=settings.subscription_id,
        resource_group=settings.resource_group,
        workspace_name=settings.workspace_name,
        compute=settings.compute,
        profile=profile.key,
        environment_name=profile.environment_name,
        models_name=profile.models_name,
        version=version,
        generated_output_path=generated_output_path,
        code_path=settings.code_path,
        models_path=profile.models_path,
        models_asset_kind=profile.models_asset_kind,
        environment_id=profile.environment_id,
        environment_image=None,
        workflow=profile.workflow,
        skip_data_upload=False,
        stream=False,
        install_runtime_deps=False,
        positive_prompt=prompt,
        negative_prompt=negative_prompt,
        seed=random.SystemRandom().randrange(10**14, 10**15),
        steps=profile.steps,
        cfg=profile.cfg,
        sampler_name=profile.sampler_name,
        scheduler=profile.scheduler,
        denoise=profile.denoise,
        width=profile.width,
        height=profile.height,
        duration_seconds=duration_seconds,
        length=profile_duration_to_length(profile, duration_seconds),
        batch_size=profile.batch_size,
        fps=profile.fps,
        filename_prefix=profile.filename_prefix,
    )
    for spec in profile.image_inputs:
        image_payload = uploaded_images.get(spec.key)
        setattr(
            submit_args,
            f"{spec.key}_url",
            str(image_payload["url"]) if image_payload is not None and image_payload.get("url") else None,
        )
        setattr(
            submit_args,
            f"{spec.key}_filename",
            str(image_payload["filename"])
            if image_payload is not None and image_payload.get("filename")
            else None,
        )
    return submit_args


def build_minimax_h3_job(submit_args: argparse.Namespace):
    """Build the web portal's proven MiniMax H3 bootstrap job.

    The workspace does not currently have dedicated MiniMax environment and
    model assets. This path uses the validated base runtime, checks out the
    tested upstream ComfyUI revision, and downloads the exact four-file H3
    bundle onto node-local storage before generation.
    """
    controls = aml_submit.job_controls(submit_args)
    root = Path(__file__).resolve().parents[1]
    workflow_path = root / submit_args.workflow
    workflow_payload = workflow_utils.load_workflow_payload(workflow_path)
    workflow_utils.patch_prompt(workflow_payload, submit_args)
    workflow_b64 = base64.b64encode(
        (json.dumps(workflow_payload, indent=2) + "\n").encode("utf-8")
    ).decode("ascii")
    launcher_b64 = base64.b64encode(MINIMAX_H3_LAUNCHER.encode("utf-8")).decode("ascii")
    model_files = " ".join(MINIMAX_H3_MODEL_FILES)
    work_suffix = re.sub(r"[^A-Za-z0-9._-]+", "-", str(submit_args.version)).strip(".-")
    work_suffix = work_suffix or uuid.uuid4().hex[:12]

    command_text = f'''set -euo pipefail
H3_NODE_ROOT="${{AZ_BATCH_NODE_ROOT_DIR:-/mnt}}"
H3_WORK_DIR="$H3_NODE_ROOT/minimax-h3-{work_suffix}"
H3_PROJECT_DIR="$H3_WORK_DIR/project"
mkdir -p "$H3_PROJECT_DIR" "$H3_WORK_DIR/models" "$H3_NODE_ROOT/.comfyui-tmp"
cp -a "${{{{inputs.repo}}}}/azureml" "$H3_PROJECT_DIR/"
cp -a /opt/comfy-h3/. "$H3_PROJECT_DIR/src/"
rmdir "$H3_WORK_DIR/models"
ln -s "${{{{inputs.models}}}}" "$H3_WORK_DIR/models"
mv "$H3_PROJECT_DIR/src/main.py" "$H3_PROJECT_DIR/src/comfy_main.py"
python -c "import base64,pathlib; pathlib.Path('$H3_PROJECT_DIR/src/main.py').write_bytes(base64.b64decode('{launcher_b64}'))"
mkdir -p "$H3_PROJECT_DIR/azureml/workflows"
python -c "import base64,pathlib; pathlib.Path('$H3_PROJECT_DIR/azureml/workflows/image_to_video_minimax_h3_api.json').write_bytes(base64.b64decode('{workflow_b64}'))"
TMPDIR="$H3_NODE_ROOT/.comfyui-tmp" python "$H3_PROJECT_DIR/azureml/run_workflow_job.py" \
  --workflow "$H3_PROJECT_DIR/azureml/workflows/image_to_video_minimax_h3_api.json" \
  --models-dir "$H3_WORK_DIR/models" \
  --output-dir "${{{{outputs.generated}}}}" \
  --positive-prompt "${{{{inputs.positive_prompt}}}}" \
  --seed "${{{{inputs.seed}}}}" \
  --steps "${{{{inputs.steps}}}}" \
  --sampler-name "${{{{inputs.sampler_name}}}}" \
  --scheduler "${{{{inputs.scheduler}}}}" \
  --denoise "${{{{inputs.denoise}}}}" \
  --width "${{{{inputs.width}}}}" \
  --height "${{{{inputs.height}}}}" \
  --duration-seconds "${{{{inputs.duration_seconds}}}}" \
  --length "${{{{inputs.length}}}}" \
  --batch-size "${{{{inputs.batch_size}}}}" \
  --fps "${{{{inputs.fps}}}}" \
  --filename-prefix "${{{{inputs.filename_prefix}}}}" \
  --input-image-url "file://${{{{inputs.input_image_url}}}}" \
  --input-image-filename "${{{{inputs.input_image_filename}}}}" \
  --startup-timeout 1800 \
  --run-timeout 7200
'''

    metadata_inputs = {
        "positive_prompt": submit_args.positive_prompt,
        "seed": submit_args.seed,
        "steps": submit_args.steps,
        "sampler_name": submit_args.sampler_name,
        "scheduler": submit_args.scheduler,
        "denoise": submit_args.denoise,
        "width": submit_args.width,
        "height": submit_args.height,
        "duration_seconds": submit_args.duration_seconds,
        "length": submit_args.length,
        "batch_size": submit_args.batch_size,
        "fps": submit_args.fps,
        "filename_prefix": submit_args.filename_prefix,
    }
    inputs = {
        "repo": aml_submit.Input(type="uri_folder", path=submit_args.code_path, mode="ro_mount"),
        "models": aml_submit.build_models_input(submit_args.models_path, asset_kind=submit_args.models_asset_kind),
        "input_image_url": aml_submit.Input(type="uri_file", path=submit_args.input_image_url, mode="download"),
        "input_image_filename": submit_args.input_image_filename,
        **{key: value for key, value in metadata_inputs.items() if value is not None},
    }
    generated_output_kwargs = {"type": "uri_folder", "mode": "rw_mount"}
    generated_output_path = getattr(submit_args, "generated_output_path", None)
    if generated_output_path:
        generated_output_kwargs["path"] = generated_output_path

    return aml_submit.command(
        **controls,
        command=command_text,
        inputs=inputs,
        outputs={"generated": aml_submit.Output(**generated_output_kwargs)},
        environment=submit_args.environment_id,
        compute=submit_args.compute,
        experiment_name="comfyui-minimax-h3",
        display_name=f"comfyui-minimax-h3-video-{submit_args.version}",
        tags={
            "profile": MINIMAX_H3_PROFILE_KEY,
            "bootstrap": f"upstream-comfyui-{MINIMAX_H3_COMFY_REVISION[:8]}",
            "models": "prepared-private-mount",
        },
    )


def submit_minimax_h3_job(submit_args: argparse.Namespace) -> dict[str, object]:
    client = aml_submit.ml_client(submit_args)
    created_job = client.jobs.create_or_update(build_minimax_h3_job(submit_args))
    created_job = aml_submit.verify_created_job(client, created_job, submit_args)
    return {
        "profile": MINIMAX_H3_PROFILE_KEY,
        "environment": {"id": submit_args.environment_id},
        "models": {"source": MINIMAX_H3_MODEL_REPO, "files": list(MINIMAX_H3_MODEL_FILES)},
        "job": {
            "name": created_job.name,
            "display_name": created_job.display_name,
            "status": created_job.status,
            "studio_url": created_job.studio_url,
        },
    }


def submit_job(submit_args: argparse.Namespace) -> dict[str, object]:
    if submit_args.profile == MINIMAX_H3_PROFILE_KEY:
        return submit_minimax_h3_job(submit_args)
    _, _, result = aml_submit.create_job_submission(submit_args)
    return result


def workspace_ml_client(settings: AppSettings):
    return aml_submit.ml_client(
        SimpleNamespace(
            subscription_id=settings.subscription_id,
            resource_group=settings.resource_group,
            workspace_name=settings.workspace_name,
        )
    )


def fetch_job(job_name: str, settings: AppSettings) -> dict[str, str | None]:
    client = workspace_ml_client(settings)
    job = client.jobs.get(job_name)
    return {
        "name": job.name,
        "display_name": job.display_name,
        "status": job.status,
        "studio_url": job.studio_url,
    }


def make_blob_service_client(
    settings: AppSettings,
    *,
    account_name: str | None = None,
) -> BlobServiceClient:
    resolved_account_name = account_name or settings.storage_account
    return BlobServiceClient(
        account_url=f"https://{resolved_account_name}.blob.core.windows.net",
        credential=build_credential(),
    )


def maybe_float(value: object) -> float | None:
    if value in (None, ""):
        return None
    try:
        return float(str(value))
    except (TypeError, ValueError):
        return None


def maybe_int(value: object) -> int | None:
    numeric = maybe_float(value)
    if numeric is None:
        return None
    return int(numeric)


def isoformat_or_none(value: object) -> str | None:
    if isinstance(value, datetime):
        return value.isoformat()
    return None


def gallery_output_prefix(job_name: str) -> str:
    return f"azureml/{job_name}/generated/"


def gallery_job_output_prefix(job: object) -> str:
    outputs = getattr(job, "outputs", None) or {}
    generated = outputs.get("generated") if isinstance(outputs, dict) else None
    output_path = (
        generated.get("path")
        if isinstance(generated, dict)
        else getattr(generated, "path", None)
    )
    if output_path:
        marker = "/paths/"
        path_text = unquote(str(output_path))
        if marker in path_text:
            relative = path_text.split(marker, 1)[1].strip("/")
            if relative:
                return f"{relative}/"
    return gallery_output_prefix(str(getattr(job, "name", "")))


def gallery_experiment_name(job: object) -> str:
    return str(getattr(job, "experiment_name", "") or "")


def gallery_profile_for_job(job: object, settings: AppSettings) -> ProfileSettings | None:
    experiment_name = gallery_experiment_name(job)
    display_name = str(getattr(job, "display_name", "") or "")
    for profile in settings.profiles.values():
        if experiment_name == profile.experiment_name:
            return profile
    for profile in settings.profiles.values():
        if display_name.startswith(profile.display_name_prefix):
            return profile
    return None


def gallery_relative_output_path(
    job_name: str,
    blob_name: str,
    output_prefix: str | None = None,
) -> str:
    prefix = output_prefix or gallery_output_prefix(job_name)
    if blob_name.startswith(prefix):
        return blob_name.removeprefix(prefix)
    return blob_name


def gallery_input_value(job: object, key: str) -> str | None:
    raw_inputs = getattr(job, "_job_inputs", None) or {}
    value = raw_inputs.get(key)
    if value in (None, ""):
        return None
    if isinstance(value, dict):
        return None
    return str(value)


def gallery_studio_url(job: object) -> str | None:
    services = getattr(job, "_services", None) or {}
    if isinstance(services, dict):
        studio = services.get("Studio")
        if isinstance(studio, dict) and studio.get("endpoint"):
            return str(studio["endpoint"])
    studio_url = getattr(job, "studio_url", None)
    if studio_url:
        return str(studio_url)
    return None


def gallery_image_specs(settings: AppSettings, profile: ProfileSettings | None) -> tuple[ImageInputSpec, ...]:
    if profile is not None:
        return profile.image_inputs

    ordered_specs: list[ImageInputSpec] = []
    seen_keys: set[str] = set()
    for candidate_profile in settings.profiles.values():
        for spec in candidate_profile.image_inputs:
            if spec.key in seen_keys:
                continue
            seen_keys.add(spec.key)
            ordered_specs.append(spec)
    return tuple(ordered_specs)


def gallery_input_images(
    job: object,
    settings: AppSettings,
    profile: ProfileSettings | None,
) -> list[dict[str, str | None]]:
    images: list[dict[str, str | None]] = []
    for spec in gallery_image_specs(settings, profile):
        image_url = gallery_input_value(job, f"{spec.key}_url")
        if not image_url:
            continue
        blob_account, blob_container, blob_name = blob_location_from_url(image_url)
        known_accounts = {
            settings.upload_storage_account,
            settings.gallery_storage_account,
        }
        images.append(
            {
                "key": spec.key,
                "label": spec.label,
                "filename": gallery_input_value(job, f"{spec.key}_filename"),
                "blob_name": blob_name if blob_account in known_accounts else None,
                "blob_account": blob_account if blob_account in known_accounts else None,
                "blob_container": blob_container if blob_account in known_accounts else None,
                "url": image_url,
            }
        )
    return images


def gallery_output_layout(
    job_name: str,
    blob_name: str,
    output_prefix: str | None = None,
) -> str:
    relative = gallery_relative_output_path(job_name, blob_name, output_prefix).lower()
    if relative.startswith("video/"):
        return "LTX nested output"
    if "/" not in relative:
        return "Flat generated output"
    return "Nested generated output"


def pick_gallery_video_blob(
    blobs: list[object],
    job_name: str,
    output_prefix: str | None = None,
) -> object | None:
    candidates = [
        blob
        for blob in blobs
        if str(getattr(blob, "name", "")).lower().endswith(VIDEO_SUFFIXES)
    ]
    if not candidates:
        return None

    def sort_key(blob: object) -> tuple[int, int, int, str]:
        relative = gallery_relative_output_path(
            job_name,
            str(getattr(blob, "name", "")),
            output_prefix,
        ).lower()
        path = PurePosixPath(relative)
        if relative.startswith("video/"):
            layout_rank = 0
        elif "/" not in relative:
            layout_rank = 1
        else:
            layout_rank = 2
        preview_rank = 1 if "preview" in path.name else 0
        return (layout_rank, preview_rank, len(path.parts), relative)

    return min(candidates, key=sort_key)


def pick_gallery_poster_blob(
    blobs: list[object],
    job_name: str,
    video_blob: object | None,
    output_prefix: str | None = None,
) -> object | None:
    candidates = [
        blob
        for blob in blobs
        if str(getattr(blob, "name", "")).lower().endswith(POSTER_SUFFIXES)
    ]
    if not candidates:
        return None

    video_parent = None
    if video_blob is not None:
        video_relative = gallery_relative_output_path(
            job_name,
            str(getattr(video_blob, "name", "")),
            output_prefix,
        ).lower()
        video_parent = PurePosixPath(video_relative).parent

    def sort_key(blob: object) -> tuple[int, int, int, str]:
        relative = gallery_relative_output_path(
            job_name,
            str(getattr(blob, "name", "")),
            output_prefix,
        ).lower()
        path = PurePosixPath(relative)
        if relative.startswith("video/"):
            layout_rank = 0
        elif "/" not in relative:
            layout_rank = 1
        else:
            layout_rank = 2
        same_parent_rank = 0 if video_parent is not None and path.parent == video_parent else 1
        return (same_parent_rank, layout_rank, len(path.parts), relative)

    return min(candidates, key=sort_key)


def blob_name_from_url(url: str | None, container_name: str) -> str | None:
    if not url:
        return None
    parsed = urlparse(url)
    path = unquote(parsed.path.lstrip("/"))
    prefix = f"{container_name}/"
    if not path.startswith(prefix):
        return None
    blob_name = path.removeprefix(prefix)
    return blob_name or None


def blob_location_from_url(url: str | None) -> tuple[str | None, str | None, str | None]:
    if not url:
        return None, None, None
    parsed = urlparse(url)
    host_suffix = ".blob.core.windows.net"
    if not parsed.hostname or not parsed.hostname.endswith(host_suffix):
        return None, None, None
    account_name = parsed.hostname.removesuffix(host_suffix)
    path_parts = unquote(parsed.path.lstrip("/")).split("/", 1)
    if len(path_parts) != 2 or not all(path_parts):
        return None, None, None
    return account_name, path_parts[0], path_parts[1]


def make_signed_blob_url(
    settings: AppSettings,
    blob_name: str,
    *,
    user_delegation_key: object,
    start_time: datetime,
    expiry_time: datetime,
    account_name: str | None = None,
    container_name: str | None = None,
) -> str:
    resolved_account_name = account_name or settings.storage_account
    resolved_container_name = container_name or settings.storage_container
    sas = generate_blob_sas(
        account_name=resolved_account_name,
        container_name=resolved_container_name,
        blob_name=blob_name,
        user_delegation_key=user_delegation_key,
        permission=BlobSasPermissions(read=True),
        start=start_time,
        expiry=expiry_time,
        protocol="https",
    )
    return (
        f"https://{resolved_account_name}.blob.core.windows.net/"
        f"{resolved_container_name}/{blob_name}?{sas}"
    )


def build_gallery_source_payload(settings: AppSettings) -> dict[str, object]:
    client = workspace_ml_client(settings)
    blob_service = make_blob_service_client(
        settings,
        account_name=settings.gallery_storage_account,
    )
    container_client = blob_service.get_container_client(settings.gallery_storage_container)

    items: list[dict[str, object]] = []
    scanned_jobs = 0

    for job in itertools.islice(client.jobs.list(max_results=GALLERY_SCAN_LIMIT), GALLERY_SCAN_LIMIT):
        scanned_jobs += 1
        experiment_name = gallery_experiment_name(job)
        if not experiment_name.startswith("comfyui-"):
            continue
        display_name = str(getattr(job, "display_name", "") or "")
        if str(getattr(job, "status", "") or "") != "Completed":
            continue

        profile = gallery_profile_for_job(job, settings)
        output_prefix = gallery_job_output_prefix(job)
        blobs = sorted(
            container_client.list_blobs(name_starts_with=output_prefix),
            key=lambda blob: str(getattr(blob, "name", "")),
        )
        video_blob = pick_gallery_video_blob(blobs, job.name, output_prefix)
        if video_blob is None:
            continue
        poster_blob = pick_gallery_poster_blob(blobs, job.name, video_blob, output_prefix)

        images = gallery_input_images(job, settings, profile)
        fps = maybe_float(gallery_input_value(job, "fps"))
        length = maybe_int(gallery_input_value(job, "length"))
        width = maybe_int(gallery_input_value(job, "width"))
        height = maybe_int(gallery_input_value(job, "height"))
        duration_seconds = (
            maybe_float(gallery_input_value(job, "duration_seconds"))
            or (round(length / fps, 2) if fps and length else None)
        )
        created_at = getattr(getattr(job, "creation_context", None), "created_at", None)
        video_blob_name = str(getattr(video_blob, "name", ""))
        video_size = getattr(video_blob, "size", None)

        item = {
            "job_name": job.name,
            "display_name": display_name,
            "experiment_name": experiment_name,
            "profile_key": profile.key if profile is not None else None,
            "profile_label": (
                profile.label
                if profile is not None
                else (experiment_name.removeprefix("comfyui-").upper() or "ComfyUI")
            ),
            "prompt": (
                gallery_input_value(job, "positive_prompt")
                or gallery_input_value(job, "clip_text_encode_prompt")
                or ""
            ),
            "created_at": isoformat_or_none(created_at),
            "created_by": getattr(getattr(job, "creation_context", None), "created_by", None),
            "studio_url": gallery_studio_url(job),
            "video_blob_name": video_blob_name,
            "video_filename": Path(video_blob_name).name,
            "output_layout": gallery_output_layout(job.name, video_blob_name, output_prefix),
            "video_size_mb": round(video_size / (1024 * 1024), 2) if isinstance(video_size, int | float) else None,
            "poster_blob_name": str(getattr(poster_blob, "name", "")) if poster_blob is not None else None,
            "images": images,
            "width": width,
            "height": height,
            "fps": fps,
            "length": length,
            "duration_seconds": duration_seconds,
        }
        items.append(item)
        if len(items) >= GALLERY_LIMIT:
            break

    return {
        "items": items,
        "count": len(items),
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "scanned_jobs": scanned_jobs,
    }


def build_gallery_payload(
    settings: AppSettings,
    source_payload: dict[str, object],
) -> dict[str, object]:
    raw_items = source_payload.get("items")
    if not isinstance(raw_items, list) or not raw_items:
        return {
            "items": [],
            "count": int(source_payload.get("count", 0) or 0),
            "generated_at": source_payload.get("generated_at"),
            "scanned_jobs": int(source_payload.get("scanned_jobs", 0) or 0),
        }

    start_time = datetime.now(timezone.utc) - timedelta(minutes=5)
    expiry_time = datetime.now(timezone.utc) + timedelta(hours=settings.sas_ttl_hours)
    delegation_keys: dict[str, object] = {}

    def signed_url(account_name: str, container_name: str, blob_name: str) -> str:
        user_delegation_key = delegation_keys.get(account_name)
        if user_delegation_key is None:
            blob_service = make_blob_service_client(settings, account_name=account_name)
            user_delegation_key = blob_service.get_user_delegation_key(start_time, expiry_time)
            delegation_keys[account_name] = user_delegation_key
        return make_signed_blob_url(
            settings,
            blob_name,
            user_delegation_key=user_delegation_key,
            start_time=start_time,
            expiry_time=expiry_time,
            account_name=account_name,
            container_name=container_name,
        )

    items: list[dict[str, object]] = []
    for raw_item in raw_items:
        if not isinstance(raw_item, dict):
            continue
        item = dict(raw_item)
        video_blob_name = str(item.pop("video_blob_name", "") or "")
        poster_blob_name = item.pop("poster_blob_name", None)
        raw_images = item.pop("images", [])

        if not video_blob_name:
            continue

        item["video_url"] = signed_url(
            settings.gallery_storage_account,
            settings.gallery_storage_container,
            video_blob_name,
        )
        item["poster_url"] = (
            signed_url(
                settings.gallery_storage_account,
                settings.gallery_storage_container,
                str(poster_blob_name),
            )
            if poster_blob_name
            else None
        )
        images: list[dict[str, object]] = []
        if isinstance(raw_images, list):
            for raw_image in raw_images:
                if not isinstance(raw_image, dict):
                    continue
                image = dict(raw_image)
                blob_name = image.pop("blob_name", None)
                blob_account = image.pop("blob_account", None)
                blob_container = image.pop("blob_container", None)
                if blob_name and blob_account and blob_container:
                    image["url"] = signed_url(
                        str(blob_account),
                        str(blob_container),
                        str(blob_name),
                    )
                images.append(image)
        item["images"] = images
        items.append(item)

    return {
        "items": items,
        "count": len(items),
        "generated_at": source_payload.get("generated_at"),
        "scanned_jobs": int(source_payload.get("scanned_jobs", 0) or 0),
    }


def shared_style_block(extra_css: str = "") -> str:
    return (
        """<style>
    :root {
      --bg: #10151b;
      --panel: rgba(18, 27, 35, 0.84);
      --panel-2: rgba(28, 39, 49, 0.92);
      --line: rgba(122, 149, 170, 0.22);
      --text: #edf3f8;
      --muted: #94a7b8;
      --accent: #ff8a3d;
      --accent-2: #ffd36b;
      --danger: #ff6c61;
      --ok: #4fd1a5;
      --shadow: 0 28px 80px rgba(0, 0, 0, 0.35);
      --radius: 22px;
      --font-ui: "Aptos", "Segoe UI Variable", "Segoe UI", sans-serif;
      --font-mono: "Cascadia Code", "SFMono-Regular", "Consolas", monospace;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      font-family: var(--font-ui);
      color: var(--text);
      background:
        radial-gradient(circle at top left, rgba(255, 138, 61, 0.18), transparent 32%),
        radial-gradient(circle at top right, rgba(79, 209, 165, 0.12), transparent 28%),
        linear-gradient(160deg, #0c1015 0%, #121923 42%, #0a0f13 100%);
    }
    .shell {
      width: min(1120px, calc(100vw - 32px));
      margin: 24px auto 32px;
      display: grid;
      gap: 18px;
    }
    .topbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
    }
    .brand {
      display: inline-flex;
      align-items: center;
      padding: 10px 14px;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.03);
      color: var(--text);
      font-size: 13px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }
    .brand:hover {
      text-decoration: none;
      background: rgba(255, 255, 255, 0.05);
    }
    .nav-links {
      display: flex;
      gap: 10px;
      flex-wrap: wrap;
    }
    .nav-link {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 10px 14px;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.03);
      color: var(--muted);
      font-size: 14px;
    }
    .nav-link:hover {
      text-decoration: none;
      color: var(--text);
      background: rgba(255, 255, 255, 0.06);
    }
    .nav-link.active {
      color: var(--text);
      border-color: rgba(255, 211, 107, 0.35);
      background: linear-gradient(135deg, rgba(255, 211, 107, 0.16), rgba(255, 138, 61, 0.1));
    }
    .hero, .panel {
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: var(--radius);
      box-shadow: var(--shadow);
      backdrop-filter: blur(18px);
    }
    .hero {
      padding: 20px 24px 18px;
      display: grid;
      gap: 16px;
      overflow: hidden;
      position: relative;
    }
    .hero::after {
      content: "";
      position: absolute;
      inset: auto -40px -60px auto;
      width: 220px;
      height: 220px;
      background: linear-gradient(135deg, rgba(255, 138, 61, 0.42), rgba(255, 211, 107, 0.04));
      border-radius: 999px;
      filter: blur(8px);
    }
    .eyebrow {
      font-size: 12px;
      letter-spacing: 0.16em;
      text-transform: uppercase;
      color: var(--accent-2);
      margin: 0;
    }
    .hero-header {
      position: relative;
      z-index: 1;
      display: grid;
      gap: 8px;
      max-width: min(760px, 100%);
      margin: 0 auto;
      text-align: center;
    }
    h1 {
      margin: 0;
      font-size: clamp(24px, 3.2vw, 36px);
      line-height: 1.04;
      letter-spacing: -0.03em;
      max-width: none;
    }
    .subtitle {
      margin: 0;
      color: var(--muted);
      max-width: 64ch;
      margin-inline: auto;
      line-height: 1.55;
    }
    .grid {
      display: grid;
      grid-template-columns: minmax(0, 1.25fr) minmax(320px, 0.75fr);
      gap: 18px;
    }
    .panel {
      padding: 20px;
    }
    .meta {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
      margin-top: 2px;
      position: relative;
      z-index: 1;
    }
    .chip {
      padding: 12px 14px;
      border-radius: 16px;
      background: rgba(255, 255, 255, 0.03);
      border: 1px solid var(--line);
    }
    .chip span {
      display: block;
      color: var(--muted);
      font-size: 12px;
      margin-bottom: 4px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    form {
      display: grid;
      gap: 14px;
    }
    .image-fields {
      display: grid;
      gap: 14px;
    }
    label {
      display: grid;
      gap: 8px;
      font-size: 14px;
      color: var(--muted);
    }
    textarea, input, select, button {
      font: inherit;
    }
    textarea, input[type="number"], input[type="file"], select {
      width: 100%;
      border: 1px solid rgba(148, 167, 184, 0.22);
      background: var(--panel-2);
      color: var(--text);
      border-radius: 16px;
      padding: 14px 16px;
    }
    textarea {
      min-height: 132px;
      resize: vertical;
      line-height: 1.5;
    }
    input[type="file"] {
      padding: 12px;
    }
    .row {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 160px;
      gap: 12px;
    }
    details {
      border: 1px solid var(--line);
      border-radius: 16px;
      padding: 12px 14px;
      background: rgba(255, 255, 255, 0.025);
    }
    summary {
      cursor: pointer;
      color: var(--text);
      font-weight: 600;
      margin-bottom: 10px;
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 14px;
      flex-wrap: wrap;
    }
    button {
      border: 0;
      border-radius: 999px;
      padding: 14px 20px;
      font-weight: 700;
      color: #11161c;
      background: linear-gradient(135deg, var(--accent-2), var(--accent));
      cursor: pointer;
      min-width: 180px;
    }
    button.secondary {
      color: var(--text);
      background: rgba(255, 255, 255, 0.04);
      border: 1px solid var(--line);
      box-shadow: none;
      min-width: auto;
    }
    button.secondary:hover:not(:disabled) {
      background: rgba(255, 255, 255, 0.08);
    }
    button:disabled {
      cursor: wait;
      opacity: 0.65;
    }
    .hint, .status-note {
      color: var(--muted);
      font-size: 13px;
      line-height: 1.5;
    }
    .status-card {
      display: grid;
      gap: 12px;
    }
    .status-pill {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      width: fit-content;
      padding: 8px 12px;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.03);
      font-size: 13px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    .status-pill::before {
      content: "";
      width: 10px;
      height: 10px;
      border-radius: 999px;
      background: var(--accent);
      box-shadow: 0 0 20px rgba(255, 138, 61, 0.55);
    }
    .status-pill.completed::before { background: var(--ok); box-shadow: 0 0 20px rgba(79, 209, 165, 0.55); }
    .status-pill.failed::before,
    .status-pill.canceled::before { background: var(--danger); box-shadow: 0 0 20px rgba(255, 108, 97, 0.55); }
    .job-meta {
      display: grid;
      gap: 10px;
      padding: 14px;
      border-radius: 18px;
      background: rgba(255, 255, 255, 0.03);
      border: 1px solid var(--line);
    }
    .job-meta code {
      font-family: var(--font-mono);
      font-size: 12px;
      word-break: break-all;
    }
    code {
      font-family: var(--font-mono);
      font-size: 0.94em;
    }
    .hidden { display: none; }
    .error {
      color: #ffd1cc;
      background: rgba(255, 108, 97, 0.12);
      border: 1px solid rgba(255, 108, 97, 0.24);
      padding: 12px 14px;
      border-radius: 14px;
      white-space: pre-wrap;
    }
    a {
      color: #a7dcff;
      text-decoration: none;
    }
    a:hover { text-decoration: underline; }
"""
        + extra_css
        + """
    @media (max-width: 860px) {
      .grid, .meta, .row {
        grid-template-columns: 1fr;
      }
      .shell {
        width: min(100vw - 20px, 1120px);
        margin: 10px auto 20px;
      }
      .hero, .panel {
        border-radius: 18px;
      }
      .topbar {
        align-items: stretch;
      }
      .nav-links {
        width: 100%;
      }
      .nav-link {
        flex: 1 1 0;
      }
    }
  </style>"""
    )


def page_head(title: str, extra_css: str = "") -> str:
    return (
        "<head>"
        '<meta charset="utf-8">'
        '<meta name="viewport" content="width=device-width, initial-scale=1">'
        f"<title>{html.escape(title)}</title>"
        f"{shared_style_block(extra_css)}"
        "</head>"
    )


def page_nav(active: str) -> str:
    submit_class = "nav-link active" if active == "submit" else "nav-link"
    videos_class = "nav-link active" if active == "videos" else "nav-link"
    return (
        '<header class="topbar">'
        '<a class="brand" href="/">Video Model Console</a>'
        '<nav class="nav-links">'
        f'<a class="{submit_class}" href="/">Submit</a>'
        f'<a class="{videos_class}" href="/videos">Video Library</a>'
        "</nav>"
        "</header>"
    )


def gallery_extra_css() -> str:
    return """
    .library-panel {
      display: grid;
      gap: 16px;
    }
    .toolbar {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 14px;
      flex-wrap: wrap;
    }
    .toolbar-copy {
      display: grid;
      gap: 6px;
      max-width: 64ch;
    }
    .toolbar-copy h2 {
      margin: 0;
      font-size: 22px;
      line-height: 1.15;
    }
    .toolbar-copy p {
      margin: 0;
      color: var(--muted);
      line-height: 1.55;
    }
    .gallery-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
      gap: 16px;
    }
    .video-card {
      display: grid;
      gap: 14px;
      padding: 14px;
      border-radius: 18px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.03);
    }
    .media-frame {
      position: relative;
    }
    .gallery-video {
      width: 100%;
      display: block;
      background: #05070a;
      border-radius: 16px;
      border: 1px solid rgba(148, 167, 184, 0.16);
    }
    .reference-thumb {
      position: absolute;
      right: 12px;
      bottom: 12px;
      width: 72px;
      height: 72px;
      border-radius: 16px;
      overflow: hidden;
      border: 1px solid rgba(148, 167, 184, 0.22);
      box-shadow: 0 14px 30px rgba(0, 0, 0, 0.3);
      background: #0a0f13;
    }
    .reference-thumb img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }
    .card-head {
      display: grid;
      gap: 8px;
    }
    .card-kicker {
      color: var(--muted);
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }
    .card-title {
      margin: 0;
      font-size: 16px;
      line-height: 1.25;
    }
    .card-prompt {
      margin: 0;
      color: var(--text);
      line-height: 1.5;
      display: -webkit-box;
      -webkit-line-clamp: 4;
      -webkit-box-orient: vertical;
      overflow: hidden;
    }
    .mini-meta {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .mini-chip {
      padding: 6px 10px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.03);
      border: 1px solid var(--line);
      color: var(--muted);
      font-size: 12px;
    }
    .card-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 10px;
    }
    .action-link {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-height: 40px;
      padding: 10px 14px;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: rgba(255, 255, 255, 0.03);
      color: var(--text);
    }
    .action-link:hover {
      text-decoration: none;
      background: rgba(255, 255, 255, 0.06);
    }
    .empty-state {
      padding: 28px;
      border-radius: 18px;
      border: 1px dashed var(--line);
      color: var(--muted);
      text-align: center;
      background: rgba(255, 255, 255, 0.02);
    }
"""


def profile_defaults_text(profile: ProfileSettings) -> str:
    return (
        f"{profile.width}x{profile.height} | "
        f"{profile.fps:g} fps | "
        f"{profile.duration_seconds:g}s default"
    )


def profile_mode_text(profile: ProfileSettings) -> str:
    if len(profile.image_inputs) >= 2 and all(spec.required for spec in profile.image_inputs):
        return "First/last-frame workflow"
    if profile.requires_image:
        return "Image-to-video only"
    if profile.supports_image:
        return "Text-to-video or image-to-video"
    return "Text-to-video"


def profile_image_help_text(profile: ProfileSettings) -> str:
    if len(profile.image_inputs) >= 2 and all(spec.required for spec in profile.image_inputs):
        labels = " and ".join(spec.label.lower() for spec in profile.image_inputs)
        return f"{profile.label} requires both {labels} inputs for every job."
    if profile.requires_image:
        return f"{profile.label} requires a reference image for every job."
    if profile.supports_image:
        return f"{profile.label} can run without an image, or use one for image-to-video."
    return f"{profile.label} ignores reference images."


def profiles_json(settings: AppSettings) -> str:
    payload = {key: asdict(profile) for key, profile in settings.profiles.items()}
    return json.dumps(payload, sort_keys=True).replace("</", "<\\/")


def page_html(settings: AppSettings) -> str:
    default_profile = settings.profiles[settings.default_profile]
    workflow = html.escape(default_profile.workflow)
    compute = html.escape(settings.compute)
    environment = html.escape(default_profile.environment_id)
    prompt_label = html.escape(default_profile.prompt_label)
    defaults = html.escape(profile_defaults_text(default_profile))
    mode_text = html.escape(profile_mode_text(default_profile))
    image_help = html.escape(profile_image_help_text(default_profile))
    negative_prompt = html.escape(default_profile.negative_prompt)
    code_version = html.escape(settings.code_version)
    models_version = html.escape(default_profile.models_version)
    profile_description = html.escape(default_profile.description)
    profile_options = "".join(
        f'<option value="{html.escape(key)}"{" selected" if key == settings.default_profile else ""}>'
        f"{html.escape(settings.profiles[key].label)}</option>"
        for key in [settings.default_profile, *[key for key in sorted(settings.profiles) if key != settings.default_profile]]
    )
    if len(default_profile.image_inputs) >= 2:
        submit_hint = (
            "The server uploads both frames to workspace blob storage before creating the AzureML first/last-frame job."
        )
    elif default_profile.requires_image:
        submit_hint = (
            "The server uploads your reference image to workspace blob storage and returns an AML Studio link as soon as the job is created."
        )
    else:
        submit_hint = "If you attach a reference image, the server uploads it to workspace blob storage before creating the AML job."
    return f"""<!doctype html>
<html lang="en">
{page_head("AzureML Video Submit")}
<body>
  <main class="shell">
    {page_nav("submit")}
    <section class="hero">
        <div class="hero-header">
        <div class="eyebrow">AzureML Submit Console</div>
        <h1>Submit Wan, LTX, or MiniMax H3 video jobs to AzureML.</h1>
        <p class="subtitle">
          Switch between Wan 2.2, MiniMax H3, LTX image-to-video, and LTX first/last-frame workflows, submit the inputs each model requires,
          and hand the workflow off to AzureML using your current <code>az login</code> session.
        </p>
        <p id="profile-description" class="hint">{profile_description}</p>
      </div>
      <div class="meta">
        <div class="chip"><span>Selected Job Type</span><strong id="selected-model">{html.escape(default_profile.label)}</strong></div>
        <div class="chip"><span>Workflow</span><code id="workflow-chip-value">{workflow}</code></div>
        <div class="chip"><span>Profile Defaults</span><span id="defaults-chip-value">{defaults}</span></div>
        <div class="chip"><span>Run Mode</span><span id="mode-chip-value">{mode_text}</span></div>
        <div class="chip"><span>Compute</span>{compute}</div>
        <div class="chip"><span>Environment</span><code id="environment-chip-value">{environment}</code></div>
        <div class="chip"><span>Code Snapshot</span><code>{code_version}</code></div>
        <div class="chip"><span>Models Snapshot</span><code id="models-chip-value">{models_version}</code></div>
      </div>
    </section>

    <section class="grid">
      <section class="panel">
        <form id="submit-form">
          <label>
            Job type
            <select id="profile-select" name="profile">
              {profile_options}
            </select>
          </label>

          <label>
            <span id="prompt-label">{prompt_label}</span>
            <textarea name="prompt" required placeholder="drone video of robot factory with human and robots assembling machines in lockstep cooperating"></textarea>
          </label>

          <div id="image-fields" class="image-fields"></div>
          <div id="image-note" class="hint">{image_help}</div>

          <div class="row">
            <label>
              Duration in seconds
              <input id="duration-input" type="number" name="duration_seconds" min="0.5" max="30" step="{default_profile.duration_step_seconds:g}" value="{default_profile.duration_seconds:g}" required>
            </label>
            <label>
              FPS
              <input id="fps-input" type="number" name="fps" value="{default_profile.fps:g}" disabled>
            </label>
          </div>

          <details>
            <summary>Negative Prompt Override</summary>
            <label>
              Optional override
              <textarea name="negative_prompt" placeholder="Leave blank to use the selected profile default"></textarea>
            </label>
            <div class="hint">
              Default for <span id="negative-profile-label">{html.escape(default_profile.label)}</span>:
              <span id="negative-default">{negative_prompt}</span>
            </div>
          </details>

          <div class="actions">
            <button id="submit-button" type="submit">Submit AzureML Job</button>
            <div id="submit-hint" class="hint">{html.escape(submit_hint)}</div>
          </div>
        </form>
      </section>

      <section class="panel status-card">
        <div>
          <div class="eyebrow">Run Status</div>
          <div id="job-status" class="status-pill hidden">Idle</div>
        </div>
        <div id="status-note" class="status-note">No submission yet. Selected model: {html.escape(default_profile.label)}.</div>
        <div id="job-meta" class="job-meta hidden">
          <div><strong>Profile</strong><br><span id="job-profile">{html.escape(default_profile.label)}</span></div>
          <div><strong>Job</strong><br><code id="job-name"></code></div>
          <div><strong>Display name</strong><br><span id="job-display-name"></span></div>
          <div><strong>Studio</strong><br><a id="job-url" href="#" target="_blank" rel="noreferrer">Open in AzureML Studio</a></div>
          <div><strong>Frames</strong><br><span id="job-length"></span></div>
          <div><strong>Images</strong><br><span id="image-summary">No images uploaded.</span></div>
        </div>
        <div id="error-box" class="error hidden"></div>
      </section>
    </section>
  </main>

  <script id="profile-data" type="application/json">{profiles_json(settings)}</script>
  <script>
    const profiles = JSON.parse(document.getElementById("profile-data").textContent);
    const form = document.getElementById("submit-form");
    const button = document.getElementById("submit-button");
    const profileSelect = document.getElementById("profile-select");
    const profileDescription = document.getElementById("profile-description");
    const selectedModel = document.getElementById("selected-model");
    const workflowChipValue = document.getElementById("workflow-chip-value");
    const defaultsChipValue = document.getElementById("defaults-chip-value");
    const modeChipValue = document.getElementById("mode-chip-value");
    const environmentChipValue = document.getElementById("environment-chip-value");
    const modelsChipValue = document.getElementById("models-chip-value");
    const imageFields = document.getElementById("image-fields");
    const imageNote = document.getElementById("image-note");
    const promptLabel = document.getElementById("prompt-label");
    const durationInput = document.getElementById("duration-input");
    const fpsInput = document.getElementById("fps-input");
    const negativeProfileLabel = document.getElementById("negative-profile-label");
    const negativeDefault = document.getElementById("negative-default");
    const submitHint = document.getElementById("submit-hint");
    const statusPill = document.getElementById("job-status");
    const statusNote = document.getElementById("status-note");
    const jobMeta = document.getElementById("job-meta");
    const errorBox = document.getElementById("error-box");
    const jobProfile = document.getElementById("job-profile");
    const jobName = document.getElementById("job-name");
    const jobDisplayName = document.getElementById("job-display-name");
    const jobUrl = document.getElementById("job-url");
    const jobLength = document.getElementById("job-length");
    const imageSummary = document.getElementById("image-summary");

    let pollTimer = null;

    function setStatus(value) {{
      const normalized = (value || "idle").toLowerCase();
      statusPill.className = `status-pill ${{
        normalized === "completed" ? "completed" :
        normalized === "failed" ? "failed" :
        normalized === "canceled" ? "canceled" : ""
      }}`.trim();
      statusPill.textContent = value || "Idle";
      statusPill.classList.remove("hidden");
    }}

    function setError(message) {{
      errorBox.textContent = message;
      errorBox.classList.remove("hidden");
    }}

    function clearError() {{
      errorBox.textContent = "";
      errorBox.classList.add("hidden");
    }}

    function formatNumber(value) {{
      return Number(value).toString();
    }}

    function defaultsText(profile) {{
      return `${{profile.width}}x${{profile.height}} | ${{formatNumber(profile.fps)}} fps | ${{formatNumber(profile.duration_seconds)}}s default`;
    }}

    function modeText(profile) {{
      if (profile.image_inputs.length >= 2 && profile.image_inputs.every((spec) => spec.required)) {{
        return "First/last-frame workflow";
      }}
      if (profile.requires_image) {{
        return "Image-to-video only";
      }}
      if (profile.supports_image) {{
        return "Text-to-video or image-to-video";
      }}
      return "Text-to-video";
    }}

    function imageHelpText(profile) {{
      if (profile.image_inputs.length >= 2 && profile.image_inputs.every((spec) => spec.required)) {{
        const labels = profile.image_inputs.map((spec) => spec.label.toLowerCase()).join(" and ");
        return `${{profile.label}} requires both ${{labels}} inputs for every job.`;
      }}
      if (profile.requires_image) {{
        return `${{profile.label}} requires a reference image for every job.`;
      }}
      if (profile.supports_image) {{
        return `${{profile.label}} can run without an image, or use one for image-to-video.`;
      }}
      return `${{profile.label}} ignores reference images.`;
    }}

    function submitHintText(profile) {{
      if (profile.image_inputs.length >= 2) {{
        return "The server uploads both frames to workspace blob storage before creating the AzureML first/last-frame job.";
      }}
      if (profile.requires_image) {{
        return "The server uploads your reference image to workspace blob storage and returns an AML Studio link as soon as the job is created.";
      }}
      return "If you attach a reference image, the server uploads it to workspace blob storage before creating the AML job.";
    }}

    function renderImageFields(profile) {{
      imageFields.replaceChildren();
      for (const spec of profile.image_inputs) {{
        const label = document.createElement("label");
        const caption = document.createElement("span");
        caption.textContent = `${{spec.label}}${{spec.required ? " (required)" : " (optional)"}}`;
        const input = document.createElement("input");
        input.type = "file";
        input.name = spec.key;
        input.id = `${{spec.key}}-input`;
        input.accept = "image/*";
        input.required = Boolean(spec.required);
        label.appendChild(caption);
        label.appendChild(input);
        imageFields.appendChild(label);
      }}
    }}

    function syncProfileUI(resetDuration = true) {{
      const profile = profiles[profileSelect.value];
      if (!profile) {{
        return;
      }}

      selectedModel.textContent = profile.label;
      profileDescription.textContent = profile.description;
      promptLabel.textContent = profile.prompt_label || "Prompt";
      workflowChipValue.textContent = profile.workflow;
      defaultsChipValue.textContent = defaultsText(profile);
      modeChipValue.textContent = modeText(profile);
      environmentChipValue.textContent = profile.environment_id;
      modelsChipValue.textContent = profile.models_version;
      renderImageFields(profile);
      imageNote.textContent = imageHelpText(profile);
      if (resetDuration) {{
        durationInput.value = formatNumber(profile.duration_seconds);
      }}
      durationInput.step = formatNumber(profile.duration_step_seconds);
      fpsInput.value = formatNumber(profile.fps);
      negativeProfileLabel.textContent = profile.label;
      negativeDefault.textContent = profile.negative_prompt;
      submitHint.textContent = submitHintText(profile);

      if (jobMeta.classList.contains("hidden")) {{
        jobProfile.textContent = profile.label;
        statusNote.textContent = `No submission yet. Selected model: ${{profile.label}}.`;
      }}
    }}

    async function refreshJob(jobId) {{
      const response = await fetch(`/api/jobs/${{encodeURIComponent(jobId)}}`);
      const payload = await response.json();
      if (!response.ok) {{
        throw new Error(payload.error || "Failed to read job status.");
      }}
      setStatus(payload.status);
      statusNote.textContent = payload.status === "Queued"
        ? "AzureML has the run; waiting for compute allocation."
        : `Latest workspace state: ${{payload.status}}`;
      if (["Completed", "Failed", "Canceled"].includes(payload.status)) {{
        window.clearInterval(pollTimer);
        pollTimer = null;
      }}
    }}

    form.addEventListener("submit", async (event) => {{
      event.preventDefault();
      clearError();
      button.disabled = true;
      const selectedProfile = profiles[profileSelect.value];
      const fileInputs = Array.from(imageFields.querySelectorAll('input[type="file"]'));
      const uploadCount = fileInputs.filter((input) => input.files && input.files.length > 0).length;
      statusNote.textContent = uploadCount > 0
        ? `Uploading ${{uploadCount}} image${{uploadCount === 1 ? "" : "s"}} and creating the ${{selectedProfile.label}} AzureML job.`
        : `Creating the ${{selectedProfile.label}} AzureML job.`;
      setStatus("Submitting");

      if (pollTimer) {{
        window.clearInterval(pollTimer);
        pollTimer = null;
      }}

      try {{
        const response = await fetch("/api/submit", {{
          method: "POST",
          body: new FormData(form),
        }});
        const payload = await response.json();
        if (!response.ok) {{
          throw new Error(payload.error || "Submission failed.");
        }}

        jobMeta.classList.remove("hidden");
        jobProfile.textContent = payload.profile.label;
        jobName.textContent = payload.job.name;
        jobDisplayName.textContent = payload.job.display_name;
        jobUrl.href = payload.job.studio_url;
        jobLength.textContent = `${{payload.length}} frames at ${{payload.fps}} fps`;
        imageSummary.textContent = payload.images && payload.images.length
          ? payload.images
              .map((image) => `${{image.label}}: ${{image.filename}} | ${{image.format}} | ${{image.width}}x${{image.height}}`)
              .join("; ")
          : "No images uploaded.";
        setStatus(payload.job.status);
        statusNote.textContent = `${{payload.profile.label}} job created successfully. Polling AzureML for updates.`;

        pollTimer = window.setInterval(() => {{
          refreshJob(payload.job.name).catch((error) => {{
            setError(error.message);
            window.clearInterval(pollTimer);
            pollTimer = null;
          }});
        }}, 15000);
      }} catch (error) {{
        setStatus("Failed");
        statusNote.textContent = "The submit request did not complete.";
        setError(error.message);
      }} finally {{
        button.disabled = false;
      }}
    }});

    profileSelect.addEventListener("change", () => syncProfileUI(true));
    syncProfileUI(false);
  </script>
</body>
</html>
"""


def videos_page_html(settings: AppSettings) -> str:
    configured_models = ", ".join(profile.label for profile in settings.profiles.values())
    models_label = html.escape(configured_models)
    signed_ttl = html.escape(f"{settings.sas_ttl_hours}h signed asset links")
    output_root = html.escape(
        f"{settings.gallery_storage_account}/{settings.gallery_storage_container}/"
        f"{settings.gallery_output_prefix}"
    )
    return f"""<!doctype html>
<html lang="en">
{page_head("ComfyUI Video Library", extra_css=gallery_extra_css())}
<body>
  <main class="shell">
    {page_nav("videos")}
    <section class="hero">
      <div class="hero-header">
        <div class="eyebrow">ComfyUI Library</div>
        <h1>Browse completed ComfyUI video runs.</h1>
        <p class="subtitle">
          This page pulls recent completed jobs from every <code>comfyui-*</code> experiment and streams their generated
          videos from the dedicated web gallery store. WAN runs usually write video files directly under
          <code>generated/</code>, while LTX writes them under <code>generated/video/</code>; the gallery resolves both layouts.
        </p>
      </div>
      <div class="meta">
        <div class="chip"><span>Experiments</span><code>comfyui-*</code></div>
        <div class="chip"><span>Models</span><span>{models_label}</span></div>
        <div class="chip"><span>Output Root</span><code>{output_root}</code></div>
        <div class="chip"><span>Layouts</span><span>WAN: <code>generated/*</code> | LTX: <code>generated/video/*</code></span></div>
        <div class="chip"><span>Asset Access</span>{signed_ttl}</div>
        <div class="chip"><span>Scope</span>Recent completed AzureML jobs only</div>
      </div>
    </section>

    <section class="panel library-panel">
      <div class="toolbar">
        <div class="toolbar-copy">
          <h2>Recent Outputs</h2>
          <p>Each card includes the generated video, its experiment/model label, the original prompt, a fresh signed link to the blob asset, and a shortcut back to AzureML Studio.</p>
        </div>
        <button id="refresh-button" class="secondary" type="button">Refresh Library</button>
      </div>
      <div id="gallery-status" class="status-note">Loading recent videos from AzureML.</div>
      <div id="gallery-error" class="error hidden"></div>
      <div id="gallery-empty" class="empty-state hidden">No completed ComfyUI videos were found in the recent AML job history.</div>
      <div id="gallery-grid" class="gallery-grid"></div>
    </section>
  </main>

  <script>
    const galleryGrid = document.getElementById("gallery-grid");
    const galleryStatus = document.getElementById("gallery-status");
    const galleryError = document.getElementById("gallery-error");
    const galleryEmpty = document.getElementById("gallery-empty");
    const refreshButton = document.getElementById("refresh-button");

    function setGalleryError(message) {{
      galleryError.textContent = message;
      galleryError.classList.remove("hidden");
    }}

    function clearGalleryError() {{
      galleryError.textContent = "";
      galleryError.classList.add("hidden");
    }}

    function formatTimestamp(value) {{
      if (!value) return "unknown time";
      const date = new Date(value);
      return Number.isNaN(date.valueOf()) ? value : date.toLocaleString();
    }}

    function createChip(text) {{
      const chip = document.createElement("span");
      chip.className = "mini-chip";
      chip.textContent = text;
      return chip;
    }}

    function createAction(label, href) {{
      const link = document.createElement("a");
      link.className = "action-link";
      link.href = href;
      link.target = "_blank";
      link.rel = "noreferrer";
      link.textContent = label;
      return link;
    }}

    function createCard(item) {{
      const card = document.createElement("article");
      card.className = "video-card";

      const media = document.createElement("div");
      media.className = "media-frame";

      const video = document.createElement("video");
      video.className = "gallery-video";
      video.controls = true;
      video.preload = "metadata";
      video.playsInline = true;
      video.src = item.video_url;
      if (item.poster_url) {{
        video.poster = item.poster_url;
      }}
      if (item.width && item.height) {{
        video.style.aspectRatio = `${{item.width}} / ${{item.height}}`;
      }} else {{
        video.style.aspectRatio = "16 / 9";
      }}
      media.appendChild(video);

      const previewImage = Array.isArray(item.images) && item.images.length ? item.images[0] : null;
      if (previewImage && previewImage.url) {{
        const reference = document.createElement("a");
        reference.className = "reference-thumb";
        reference.href = previewImage.url;
        reference.target = "_blank";
        reference.rel = "noreferrer";
        reference.title = previewImage.filename
          ? `${{previewImage.label}}: ${{previewImage.filename}}`
          : previewImage.label || "Input image";

        const img = document.createElement("img");
        img.src = previewImage.url;
        img.alt = previewImage.filename || previewImage.label || "Input image";
        reference.appendChild(img);
        media.appendChild(reference);
      }}

      const head = document.createElement("div");
      head.className = "card-head";

      const kicker = document.createElement("div");
      kicker.className = "card-kicker";
      kicker.textContent = `${{formatTimestamp(item.created_at)}}${{item.created_by ? ` | ${{item.created_by}}` : ""}}`;
      head.appendChild(kicker);

      const title = document.createElement("h2");
      title.className = "card-title";
      title.textContent = item.display_name || item.job_name;
      head.appendChild(title);

      const prompt = document.createElement("p");
      prompt.className = "card-prompt";
      prompt.textContent = item.prompt || "Prompt unavailable.";
      head.appendChild(prompt);

      const meta = document.createElement("div");
      meta.className = "mini-meta";
      meta.appendChild(createChip(item.job_name));
      if (item.profile_label) {{
        meta.appendChild(createChip(item.profile_label));
      }}
      if (item.experiment_name) {{
        meta.appendChild(createChip(item.experiment_name));
      }}
      if (item.output_layout) {{
        meta.appendChild(createChip(item.output_layout));
      }}
      if (item.width && item.height) {{
        meta.appendChild(createChip(`${{item.width}}x${{item.height}}`));
      }}
      if (item.fps && item.length) {{
        meta.appendChild(createChip(`${{item.length}} frames @ ${{item.fps}} fps`));
      }}
      if (item.duration_seconds) {{
        meta.appendChild(createChip(`${{item.duration_seconds}}s`));
      }}
      if (item.video_size_mb) {{
        meta.appendChild(createChip(`${{item.video_size_mb}} MB`));
      }}

      const actions = document.createElement("div");
      actions.className = "card-actions";
      actions.appendChild(createAction("Open Video", item.video_url));
      if (item.studio_url) {{
        actions.appendChild(createAction("AzureML Studio", item.studio_url));
      }}
      if (Array.isArray(item.images)) {{
        for (const image of item.images) {{
          if (image.url) {{
            actions.appendChild(createAction(image.label || "Input Image", image.url));
          }}
        }}
      }}

      card.appendChild(media);
      card.appendChild(head);
      card.appendChild(meta);
      card.appendChild(actions);
      return card;
    }}

    async function loadGallery(forceRefresh = false) {{
      clearGalleryError();
      refreshButton.disabled = true;
      galleryEmpty.classList.add("hidden");
      galleryStatus.textContent = forceRefresh
        ? "Refreshing the video library from AzureML and gallery storage."
        : "Loading recent videos from AzureML.";

      try {{
        const response = await fetch(forceRefresh ? "/api/gallery?refresh=1" : "/api/gallery");
        const payload = await response.json();
        if (!response.ok) {{
          throw new Error(payload.error || "Failed to load the video library.");
        }}

        galleryGrid.replaceChildren();
        if (!payload.items.length) {{
          galleryEmpty.classList.remove("hidden");
          galleryStatus.textContent = `Scanned ${{payload.scanned_jobs}} recent AML jobs across comfyui-* experiments and found no completed video outputs.`;
          return;
        }}

        const cards = payload.items.map((item) => createCard(item));
        galleryGrid.replaceChildren(...cards);
        galleryStatus.textContent = `Showing ${{payload.count}} completed runs from the latest ${{payload.scanned_jobs}} AML jobs across comfyui-* experiments. Refreshed ${{formatTimestamp(payload.generated_at)}}.`;
      }} catch (error) {{
        galleryGrid.replaceChildren();
        galleryStatus.textContent = "The video library could not be loaded.";
        setGalleryError(error.message);
      }} finally {{
        refreshButton.disabled = false;
      }}
    }}

    refreshButton.addEventListener("click", () => loadGallery(true));
    loadGallery();
  </script>
</body>
</html>
"""


async def handle_index(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    return web.Response(text=page_html(settings), content_type="text/html")


async def handle_videos(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    return web.Response(text=videos_page_html(settings), content_type="text/html")


async def handle_submit(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    form = await request.post()

    profile_key = str(form.get("profile", settings.default_profile)).strip() or settings.default_profile
    profile = settings.profiles.get(profile_key)
    if profile is None:
        return web.json_response({"error": f"Unknown profile: {profile_key}"}, status=400)

    prompt = str(form.get("prompt", "")).strip()
    if not prompt:
        return web.json_response({"error": f"{profile.prompt_label} is required."}, status=400)

    uploads: dict[str, web.FileField] = {}
    for spec in profile.image_inputs:
        upload = form.get(spec.key)
        has_upload = isinstance(upload, web.FileField) and bool(upload.filename)
        if spec.required and not has_upload:
            return web.json_response({"error": f"{spec.label} is required for the selected profile."}, status=400)
        if has_upload and isinstance(upload, web.FileField):
            uploads[spec.key] = upload

    negative_prompt = str(form.get("negative_prompt", profile.negative_prompt)).strip()
    try:
        duration_seconds = float(str(form.get("duration_seconds", profile.duration_seconds)).strip())
    except ValueError:
        return web.json_response({"error": "Duration must be a number."}, status=400)
    if not 0.5 <= duration_seconds <= 30:
        return web.json_response({"error": "Duration must be between 0.5 and 30 seconds."}, status=400)
    if profile.length_mode == "seconds_plus_one":
        duration_seconds = max(1.0, int(duration_seconds + 0.5))

    uploaded_images: dict[str, dict[str, object]] = {}

    try:
        for spec in profile.image_inputs:
            upload = uploads.get(spec.key)
            if upload is None:
                continue

            safe_name = sanitize_filename(upload.filename or f"{spec.key}.png")
            with tempfile.TemporaryDirectory(prefix=f"{profile.key}-{spec.key}-upload-") as temp_dir:
                local_path = Path(temp_dir) / safe_name
                with local_path.open("wb") as output:
                    shutil.copyfileobj(upload.file, output)

                image_info = await asyncio.to_thread(validate_image, local_path)
                blob_name = make_blob_name(settings, safe_name)
                image_url = await asyncio.to_thread(upload_blob, local_path, blob_name, settings)
                uploaded_images[spec.key] = {
                    "key": spec.key,
                    "label": spec.label,
                    "filename": safe_name,
                    "url": image_url,
                    "blob_name": blob_name,
                    **image_info,
                }

        submit_args = build_submission_args(
            settings,
            profile,
            prompt=prompt,
            negative_prompt=negative_prompt,
            duration_seconds=duration_seconds,
            uploaded_images=uploaded_images,
        )
        result = await asyncio.to_thread(submit_job, submit_args)
    except Exception as exc:
        return web.json_response({"error": aml_submit.public_error(exc)}, status=500)

    return web.json_response(
        {
            "job": result["job"],
            "profile": {
                "key": profile.key,
                "label": profile.label,
            },
            "length": submit_args.length,
            "fps": submit_args.fps,
            "images": [
                {
                    "key": image["key"],
                    "label": image["label"],
                    "filename": image["filename"],
                    "format": image["format"],
                    "width": image["width"],
                    "height": image["height"],
                }
                for image in uploaded_images.values()
            ],
            "blob_names": {
                key: image["blob_name"]
                for key, image in uploaded_images.items()
            },
        }
    )


async def handle_job_status(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    job_name = request.match_info["job_name"]
    try:
        job = await asyncio.to_thread(fetch_job, job_name, settings)
    except Exception as exc:
        return web.json_response({"error": aml_submit.public_error(exc)}, status=500)
    return web.json_response(job)


async def handle_gallery(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    refresh = request.query.get("refresh") == "1"
    cache = request.app["gallery_cache"]
    now = time.monotonic()

    if (
        not refresh
        and cache["payload"] is not None
        and now - cache["payload_fetched_at"] < GALLERY_PAYLOAD_CACHE_SECONDS
    ):
        return web.json_response(cache["payload"])

    try:
        if (
            refresh
            or cache["source"] is None
            or now - cache["source_fetched_at"] >= GALLERY_SOURCE_CACHE_SECONDS
        ):
            source_payload = await asyncio.to_thread(build_gallery_source_payload, settings)
            cache["source"] = source_payload
            cache["source_fetched_at"] = now
        else:
            source_payload = cache["source"]
        payload = await asyncio.to_thread(build_gallery_payload, settings, source_payload)
    except Exception as exc:
        return web.json_response({"error": aml_submit.public_error(exc)}, status=500)

    cache["payload"] = payload
    cache["payload_fetched_at"] = now
    return web.json_response(payload)


async def handle_health(request: web.Request) -> web.Response:
    settings: AppSettings = request.app["settings"]
    return web.json_response(
        {
            "status": "ok",
            "code_version": settings.code_version,
            "default_profile": settings.default_profile,
            "gallery_profile": settings.gallery_profile,
            "gallery_storage_account": settings.gallery_storage_account,
            "gallery_storage_container": settings.gallery_storage_container,
            "gallery_output_datastore": settings.gallery_output_datastore,
            "upload_storage_account": settings.upload_storage_account,
            "upload_storage_container": settings.upload_storage_container,
            "profiles": {
                key: {
                    "label": profile.label,
                    "workflow": profile.workflow,
                    "environment_id": profile.environment_id,
                    "models_version": profile.models_version,
                    "models_path": profile.models_path,
                    "models_asset_kind": profile.models_asset_kind,
                    "environment_version": profile.environment_version,
                    "supports_image": profile.supports_image,
                    "requires_image": profile.requires_image,
                }
                for key, profile in settings.profiles.items()
            },
        }
    )


def make_app(settings: AppSettings) -> web.Application:
    app = web.Application(client_max_size=settings.max_upload_mb * 1024 * 1024)
    app["settings"] = settings
    app["gallery_cache"] = {
        "source": None,
        "source_fetched_at": 0.0,
        "payload": None,
        "payload_fetched_at": 0.0,
    }
    app.router.add_get("/", handle_index)
    app.router.add_get("/videos", handle_videos)
    app.router.add_get("/healthz", handle_health)
    app.router.add_post("/api/submit", handle_submit)
    app.router.add_get("/api/jobs/{job_name}", handle_job_status)
    app.router.add_get("/api/gallery", handle_gallery)
    return app


def main() -> int:
    args = parse_args()
    settings = resolve_settings(args)
    print(
        json.dumps(
            {
                "host": settings.host,
                "port": settings.port,
                "default_profile": settings.default_profile,
                "gallery_profile": settings.gallery_profile,
                "code_path": settings.code_path,
                "profiles": {
                    key: {
                        "workflow": profile.workflow,
                        "models_path": profile.models_path,
                        "models_asset_kind": profile.models_asset_kind,
                        "environment_id": profile.environment_id,
                    }
                    for key, profile in settings.profiles.items()
                },
            },
            indent=2,
        )
    )
    web.run_app(make_app(settings), host=settings.host, port=settings.port)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
