#!/usr/bin/env python3

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request


PREIMPORT_AML_USER_LOGS_ENV = "AZUREML_CR_HT_CAP_user_logs_PATH"
PREIMPORT_AML_LOGS_ENV = "AZUREML_CR_HT_CAP_logs_PATH"


def emit_preimport_breadcrumb(filename: str, stage: str) -> None:
    line = f"{stage} pid={os.getpid()}\n"
    for env_name in (PREIMPORT_AML_USER_LOGS_ENV, PREIMPORT_AML_LOGS_ENV):
        configured = os.getenv(env_name)
        if not configured:
            continue
        try:
            target_dir = Path(configured).expanduser()
            target_dir.mkdir(parents=True, exist_ok=True)
            with (target_dir / filename).open("a", encoding="utf-8") as handle:
                handle.write(line)
            return
        except OSError:
            continue


emit_preimport_breadcrumb("bootstrap_ltx_and_run_preimport.log", "module_import_start")

try:
    from .aml_project_layout import app_root as resolve_app_root
    from .aml_project_layout import project_root as resolve_project_root
    from .run_workflow_job import append_jsonl_entry
    from .run_workflow_job import COMFY_TMP_ROOT_ENV as RUNNER_TMP_ROOT_ENV
    from .run_workflow_job import resolve_log_write_paths
    from .run_workflow_job import resolve_tmp_root as resolve_runner_tmp_root
    from .workflow_profiles import all_image_input_keys
    from . import workflow_utils
except ImportError:
    from aml_project_layout import app_root as resolve_app_root
    from aml_project_layout import project_root as resolve_project_root
    from run_workflow_job import append_jsonl_entry
    from run_workflow_job import COMFY_TMP_ROOT_ENV as RUNNER_TMP_ROOT_ENV
    from run_workflow_job import resolve_log_write_paths
    from run_workflow_job import resolve_tmp_root as resolve_runner_tmp_root
    from workflow_profiles import all_image_input_keys
    import workflow_utils


LTX_MODEL_URLS = {
    workflow_utils.ModelReference(
        "checkpoints",
        "ltx-2-19b-dev.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2/resolve/main/ltx-2-19b-dev.safetensors",
    workflow_utils.ModelReference(
        "checkpoints",
        "ltx-2-19b-dev-fp8.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2/resolve/main/ltx-2-19b-dev-fp8.safetensors",
    workflow_utils.ModelReference(
        "latent_upscale_models",
        "ltx-2-spatial-upscaler-x2-1.0.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2/resolve/main/ltx-2-spatial-upscaler-x2-1.0.safetensors",
    workflow_utils.ModelReference(
        "text_encoders",
        "gemma_3_12B_it.safetensors",
    ): "https://huggingface.co/Comfy-Org/ltx-2/resolve/main/split_files/text_encoders/gemma_3_12B_it.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-distilled-lora-384.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2/resolve/main/ltx-2-19b-distilled-lora-384.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-ic-lora-detailer.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-IC-LoRA-Detailer/resolve/main/ltx-2-19b-ic-lora-detailer.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-dolly-in.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Dolly-In/resolve/main/ltx-2-19b-lora-camera-control-dolly-in.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-dolly-left.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Dolly-Left/resolve/main/ltx-2-19b-lora-camera-control-dolly-left.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-dolly-out.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Dolly-Out/resolve/main/ltx-2-19b-lora-camera-control-dolly-out.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-dolly-right.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Dolly-Right/resolve/main/ltx-2-19b-lora-camera-control-dolly-right.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-jib-down.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Jib-Down/resolve/main/ltx-2-19b-lora-camera-control-jib-down.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-jib-up.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Jib-Up/resolve/main/ltx-2-19b-lora-camera-control-jib-up.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "LTX2/ltx-2-19b-lora-camera-control-static.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2-19b-LoRA-Camera-Control-Static/resolve/main/ltx-2-19b-lora-camera-control-static.safetensors",
    workflow_utils.ModelReference(
        "checkpoints",
        "ltx-2.3-22b-dev.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2.3/resolve/main/ltx-2.3-22b-dev.safetensors",
    workflow_utils.ModelReference(
        "checkpoints",
        "ltx-2.3-22b-dev-fp8.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2.3-fp8/resolve/main/ltx-2.3-22b-dev-fp8.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "ltxv/ltx2/ltx-2.3-22b-distilled-lora-384-1.1.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2.3/resolve/main/ltx-2.3-22b-distilled-lora-384-1.1.safetensors",
    workflow_utils.ModelReference(
        "loras",
        "ltx-2.3-22b-distilled-lora-384.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2.3/resolve/main/ltx-2.3-22b-distilled-lora-384.safetensors",
    workflow_utils.ModelReference(
        "latent_upscale_models",
        "ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
    ): "https://huggingface.co/Lightricks/LTX-2.3/resolve/main/ltx-2.3-spatial-upscaler-x2-1.1.safetensors",
    workflow_utils.ModelReference(
        "text_encoders",
        "comfy_gemma_3_12B_it.safetensors",
    ): (
        "https://huggingface.co/Comfy-Org/ltx-2/resolve/main/"
        "split_files/text_encoders/gemma_3_12B_it_fp4_mixed.safetensors"
    ),
    workflow_utils.ModelReference(
        "text_encoders",
        "gemma_3_12B_it_fp4_mixed.safetensors",
    ): (
        "https://huggingface.co/Comfy-Org/ltx-2/resolve/main/"
        "split_files/text_encoders/gemma_3_12B_it_fp4_mixed.safetensors"
    ),
}

DEFAULT_PRELOADED_MODELS_DIR = Path("/opt/comfyui-preloaded-models")
PRELOADED_MODELS_ENV = "COMFY_PRELOADED_MODELS_DIR"
TMP_ROOT = Path("/tmp")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workflow", required=True)
    parser.add_argument("--models-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--enable-custom-nodes", action="store_true")
    parser.add_argument("--startup-timeout", default="900")
    parser.add_argument("--run-timeout", default="7200")
    parser.add_argument(
        "--positive-prompt",
        "--clip-text-encode-prompt",
        dest="positive_prompt",
        default=None,
    )
    parser.add_argument("--negative-prompt", default=None)
    parser.add_argument("--seed", default=None)
    parser.add_argument("--steps", default=None)
    parser.add_argument("--cfg", default=None)
    parser.add_argument("--sampler-name", default=None)
    parser.add_argument("--scheduler", default=None)
    parser.add_argument("--denoise", default=None)
    parser.add_argument("--width", default=None)
    parser.add_argument("--height", default=None)
    parser.add_argument("--duration-seconds", default=None)
    parser.add_argument("--length", default=None)
    parser.add_argument("--batch-size", default=None)
    parser.add_argument("--fps", default=None)
    parser.add_argument("--filename-prefix", default=None)
    for key in all_image_input_keys():
        flag_prefix = key.replace("_", "-")
        parser.add_argument(f"--{flag_prefix}-url", default=None)
        parser.add_argument(f"--{flag_prefix}-filename", default=None)
    return parser.parse_args()


def resolve_workflow_path(project_root: Path, workflow_arg: str) -> Path:
    workflow_path = Path(workflow_arg)
    if not workflow_path.is_absolute():
        workflow_path = project_root / workflow_path
    return workflow_path.resolve()


def copy_configs(source_root: Path, models_dir: Path) -> None:
    source = source_root / "models" / "configs"
    if not source.is_dir():
        return
    destination = models_dir / "configs"
    destination.mkdir(parents=True, exist_ok=True)
    for config_path in sorted(source.glob("*.yaml")):
        shutil.copy2(config_path, destination / config_path.name)


def download_file(url: str, destination: Path) -> None:
    if destination.exists() and destination.stat().st_size > 0:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    temp_path = destination.with_suffix(destination.suffix + ".part")
    if temp_path.exists():
        temp_path.unlink()
    with urllib.request.urlopen(url) as response, temp_path.open("wb") as output:
        shutil.copyfileobj(response, output, length=16 * 1024 * 1024)
    temp_path.replace(destination)


def resolve_preloaded_models_dir() -> Path | None:
    configured = os.getenv(PRELOADED_MODELS_ENV)
    if configured:
        candidate = Path(configured).expanduser().resolve()
        if candidate.is_dir():
            return candidate
    if DEFAULT_PRELOADED_MODELS_DIR.is_dir():
        return DEFAULT_PRELOADED_MODELS_DIR
    return None


def stage_model_file(source: Path, destination: Path) -> None:
    if destination.exists():
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    try:
        destination.symlink_to(source)
    except OSError:
        shutil.copy2(source, destination)


def stage_required_models(
    workflow_path: Path,
    models_dir: Path,
    *,
    mounted_models_dir: Path | None = None,
    preloaded_models_dir: Path | None = None,
) -> None:
    payload = workflow_utils.load_workflow_payload(workflow_path)
    model_refs = workflow_utils.collect_required_model_references(payload)
    if not model_refs:
        raise ValueError(f"Could not infer any required models from workflow {workflow_path}.")

    unknown_refs = sorted(
        (
            ref
            for ref in model_refs
            if ref not in LTX_MODEL_URLS
        ),
        key=lambda item: (item.folder_name, item.filename),
    )
    if unknown_refs:
        unknown_list = "\n".join(f"{ref.folder_name}:{ref.filename}" for ref in unknown_refs)
        raise ValueError("Missing download sources for workflow-required LTX models:\n" + unknown_list)

    reused_count = 0
    preloaded_count = 0
    mounted_count = 0
    downloaded_count = 0
    for ref in sorted(model_refs, key=lambda item: (item.folder_name, item.filename)):
        destination = models_dir / ref.folder_name / ref.filename
        if destination.exists() and destination.stat().st_size > 0:
            reused_count += 1
            continue
        if preloaded_models_dir is not None:
            try:
                preloaded_source = workflow_utils.find_local_model_file(preloaded_models_dir, ref)
            except FileNotFoundError:
                preloaded_source = None
            if preloaded_source is not None:
                stage_model_file(preloaded_source, destination)
                preloaded_count += 1
                continue
        if mounted_models_dir is not None:
            try:
                mounted_source = workflow_utils.find_local_model_file(mounted_models_dir, ref)
            except FileNotFoundError:
                mounted_source = None
            if mounted_source is not None:
                stage_model_file(mounted_source, destination)
                mounted_count += 1
                continue
        raise FileNotFoundError(f"Prepare is required before GPU execution: {ref}")
    print(
        "LTX_MODEL_STAGE "
        f"reused={reused_count} "
        f"preloaded={preloaded_count} "
        f"mounted={mounted_count} "
        f"downloaded={downloaded_count}"
    )


def stage_writable_models_dir(source_root: Path, scratch_root: Path) -> Path:
    scratch_models_dir = scratch_root / "models"
    scratch_models_dir.mkdir(parents=True, exist_ok=True)
    copy_configs(source_root, scratch_models_dir)
    return scratch_models_dir


def required_model_references(workflow_path: Path) -> set[workflow_utils.ModelReference]:
    payload = workflow_utils.load_workflow_payload(workflow_path)
    model_refs = workflow_utils.collect_required_model_references(payload)
    if not model_refs:
        raise ValueError(f"Could not infer any required models from workflow {workflow_path}.")
    return model_refs


def missing_model_references(
    models_dir: Path,
    model_refs: set[workflow_utils.ModelReference],
) -> list[workflow_utils.ModelReference]:
    missing: list[workflow_utils.ModelReference] = []
    for ref in sorted(model_refs, key=lambda item: (item.folder_name, item.filename)):
        try:
            workflow_utils.find_local_model_file(models_dir, ref)
        except FileNotFoundError:
            missing.append(ref)
    return missing


def has_required_configs(source_root: Path, models_dir: Path) -> bool:
    source_configs_dir = source_root / "models" / "configs"
    if not source_configs_dir.is_dir():
        return True
    target_configs_dir = models_dir / "configs"
    for config_path in sorted(source_configs_dir.glob("*.yaml")):
        if not (target_configs_dir / config_path.name).is_file():
            return False
    return True


def models_bundle_is_complete(
    source_root: Path,
    models_dir: Path,
    model_refs: set[workflow_utils.ModelReference],
) -> bool:
    if not models_dir.is_dir():
        return False
    if missing_model_references(models_dir, model_refs):
        return False
    return has_required_configs(source_root, models_dir)


def disk_usage_snapshot(*paths: Path) -> dict[str, dict[str, int | str]]:
    snapshot: dict[str, dict[str, int | str]] = {}
    for path in paths:
        key = str(path)
        try:
            usage = shutil.disk_usage(path)
        except OSError as exc:
            snapshot[key] = {"error": str(exc)}
            continue
        snapshot[key] = {
            "total": usage.total,
            "used": usage.used,
            "free": usage.free,
        }
    return snapshot


def append_progress(output_dir: Path, stage: str, **details: object) -> None:
    payload = {
        "stage": stage,
        "pid": os.getpid(),
        "details": details,
    }
    append_jsonl_entry(resolve_log_write_paths(output_dir, "bootstrap_progress.jsonl"), payload)


def append_if_value(command: list[str], flag: str, value: str | None) -> None:
    if value is None:
        return
    command.extend([flag, value])


def main() -> int:
    args = parse_args()
    project_root = resolve_project_root()
    source_root = resolve_app_root(project_root)
    workflow_path = resolve_workflow_path(project_root, args.workflow)
    model_refs = required_model_references(workflow_path)
    mounted_models_dir = Path(args.models_dir).expanduser()
    output_dir = Path(args.output_dir).expanduser()
    preloaded_models_dir = resolve_preloaded_models_dir()
    print(
        "BOOTSTRAP_START "
        f"workflow={workflow_path} "
        f"mounted_models_dir={mounted_models_dir} "
        f"preloaded_models_dir={preloaded_models_dir}",
        flush=True,
    )
    append_progress(
        output_dir,
        "bootstrap_start",
        workflow=str(workflow_path),
        required_model_count=len(model_refs),
        mounted_models_dir=str(mounted_models_dir),
        preloaded_models_dir=str(preloaded_models_dir) if preloaded_models_dir is not None else None,
        disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp")),
    )

    tmp_root = resolve_runner_tmp_root(default=TMP_ROOT)
    print(f"BOOTSTRAP_TMP_ROOT {tmp_root}", flush=True)
    append_progress(
        output_dir,
        "tmp_root_resolved",
        tmp_root=str(tmp_root),
        disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root),
    )

    scratch_root: Path | None = None
    if models_bundle_is_complete(source_root, mounted_models_dir, model_refs):
        models_dir = mounted_models_dir
        stage_mode = "mounted_direct"
        print(f"BOOTSTRAP_MODELS stage_mode={stage_mode} models_dir={models_dir}", flush=True)
        append_progress(
            output_dir,
            "model_stage_complete",
            stage_mode=stage_mode,
            models_dir=str(models_dir),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root),
        )
        completed = run_runner_subprocess(
            args=args,
            project_root=project_root,
            workflow_path=workflow_path,
            models_dir=models_dir,
            output_dir=output_dir,
            tmp_root=tmp_root,
            scratch_root=scratch_root,
        )
        return completed.returncode

    if preloaded_models_dir is not None and models_bundle_is_complete(source_root, preloaded_models_dir, model_refs):
        models_dir = preloaded_models_dir
        stage_mode = "preloaded_direct"
        print(f"BOOTSTRAP_MODELS stage_mode={stage_mode} models_dir={models_dir}", flush=True)
        append_progress(
            output_dir,
            "model_stage_complete",
            stage_mode=stage_mode,
            models_dir=str(models_dir),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root),
        )
        completed = run_runner_subprocess(
            args=args,
            project_root=project_root,
            workflow_path=workflow_path,
            models_dir=models_dir,
            output_dir=output_dir,
            tmp_root=tmp_root,
            scratch_root=scratch_root,
        )
        return completed.returncode

    with tempfile.TemporaryDirectory(prefix="ltx-models-", dir=str(tmp_root)) as scratch_dir:
        scratch_root = Path(scratch_dir)
        models_dir = stage_writable_models_dir(source_root, Path(scratch_dir))
        append_progress(
            output_dir,
            "scratch_dir_created",
            scratch_dir=str(scratch_root),
            models_dir=str(models_dir),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root, scratch_root),
        )
        stage_required_models(
            workflow_path,
            models_dir,
            mounted_models_dir=mounted_models_dir,
            preloaded_models_dir=preloaded_models_dir,
        )
        print(f"BOOTSTRAP_MODELS stage_mode=staged_scratch models_dir={models_dir}", flush=True)
        append_progress(
            output_dir,
            "model_stage_complete",
            stage_mode="staged_scratch",
            models_dir=str(models_dir),
            model_file_count=sum(1 for path in models_dir.rglob("*") if path.is_file()),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root, scratch_root),
        )
        completed = run_runner_subprocess(
            args=args,
            project_root=project_root,
            workflow_path=workflow_path,
            models_dir=models_dir,
            output_dir=output_dir,
            tmp_root=tmp_root,
            scratch_root=scratch_root,
        )
        return completed.returncode


def run_runner_subprocess(
    *,
    args: argparse.Namespace,
    project_root: Path,
    workflow_path: Path,
    models_dir: Path,
    output_dir: Path,
    tmp_root: Path,
    scratch_root: Path | None,
) -> subprocess.CompletedProcess[bytes]:
    command = [
        sys.executable,
        str(project_root / "azureml" / "run_workflow_job.py"),
        "--workflow",
        str(workflow_path),
        "--models-dir",
        str(models_dir),
        "--output-dir",
        args.output_dir,
        "--startup-timeout",
        args.startup_timeout,
        "--run-timeout",
        args.run_timeout,
    ]
    if args.enable_custom_nodes:
        command.append("--enable-custom-nodes")

    append_if_value(command, "--positive-prompt", args.positive_prompt)
    append_if_value(command, "--negative-prompt", args.negative_prompt)
    append_if_value(command, "--seed", args.seed)
    append_if_value(command, "--steps", args.steps)
    append_if_value(command, "--cfg", args.cfg)
    append_if_value(command, "--sampler-name", args.sampler_name)
    append_if_value(command, "--scheduler", args.scheduler)
    append_if_value(command, "--denoise", args.denoise)
    append_if_value(command, "--width", args.width)
    append_if_value(command, "--height", args.height)
    append_if_value(command, "--duration-seconds", args.duration_seconds)
    append_if_value(command, "--length", args.length)
    append_if_value(command, "--batch-size", args.batch_size)
    append_if_value(command, "--fps", args.fps)
    append_if_value(command, "--filename-prefix", args.filename_prefix)
    for key in all_image_input_keys():
        append_if_value(command, f"--{key.replace('_', '-')}-url", getattr(args, f"{key}_url", None))
        append_if_value(command, f"--{key.replace('_', '-')}-filename", getattr(args, f"{key}_filename", None))

    child_env = os.environ.copy()
    child_env[RUNNER_TMP_ROOT_ENV] = str(tmp_root)
    child_env["TMPDIR"] = str(tmp_root)
    child_env["TMP"] = str(tmp_root)
    child_env["TEMP"] = str(tmp_root)

    append_progress(
        output_dir,
        "runner_start",
        cwd=str(tmp_root),
        command=command,
        scratch_root=str(scratch_root) if scratch_root is not None else None,
        disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root),
    )
    completed = subprocess.run(command, cwd=tmp_root, env=child_env, check=False)
    append_progress(
        output_dir,
        "runner_complete",
        returncode=completed.returncode,
        disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp"), tmp_root),
    )
    return completed


if __name__ == "__main__":
    raise SystemExit(main())
