#!/usr/bin/env python3

from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.request

try:
    from .aml_project_layout import app_root as resolve_app_root
    from .aml_project_layout import project_root as resolve_project_root
except ImportError:
    from aml_project_layout import app_root as resolve_app_root
    from aml_project_layout import project_root as resolve_project_root


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workflow", required=True)
    parser.add_argument("--models-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--unet-url", required=True)
    parser.add_argument("--text-encoder-url", required=True)
    parser.add_argument("--vae-url", required=True)
    parser.add_argument("--startup-timeout", default="900")
    parser.add_argument("--run-timeout", default="7200")
    parser.add_argument("--positive-prompt", default=None)
    parser.add_argument("--negative-prompt", default=None)
    parser.add_argument("--seed", default=None)
    parser.add_argument("--steps", default=None)
    parser.add_argument("--cfg", default=None)
    parser.add_argument("--sampler-name", default=None)
    parser.add_argument("--scheduler", default=None)
    parser.add_argument("--denoise", default=None)
    parser.add_argument("--width", default=None)
    parser.add_argument("--height", default=None)
    parser.add_argument("--length", default=None)
    parser.add_argument("--batch-size", default=None)
    parser.add_argument("--fps", default=None)
    parser.add_argument("--filename-prefix", default=None)
    return parser.parse_args()


def download_file(url: str, destination: Path) -> None:
    if destination.exists() and destination.stat().st_size > 0:
        return
    destination.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output, length=16 * 1024 * 1024)


def copy_configs(source_root: Path, models_dir: Path) -> None:
    source = source_root / "models" / "configs"
    if not source.is_dir():
        return
    destination = models_dir / "configs"
    destination.mkdir(parents=True, exist_ok=True)
    for config_path in sorted(source.glob("*.yaml")):
        shutil.copy2(config_path, destination / config_path.name)


def append_if_value(command: list[str], flag: str, value: str | None) -> None:
    if value is None:
        return
    command.extend([flag, value])


def main() -> int:
    args = parse_args()
    project_root = resolve_project_root()
    source_root = resolve_app_root(project_root)
    models_dir = Path(args.models_dir).resolve()

    copy_configs(source_root, models_dir)
    download_file(
        args.unet_url,
        models_dir / "diffusion_models" / "wan2.2_ti2v_5B_fp16.safetensors",
    )
    download_file(
        args.text_encoder_url,
        models_dir / "text_encoders" / "umt5_xxl_fp8_e4m3fn_scaled.safetensors",
    )
    download_file(
        args.vae_url,
        models_dir / "vae" / "wan2.2_vae.safetensors",
    )

    command = [
        sys.executable,
        str(project_root / "azureml" / "run_workflow_job.py"),
        "--workflow",
        args.workflow,
        "--models-dir",
        str(models_dir),
        "--output-dir",
        args.output_dir,
        "--startup-timeout",
        args.startup_timeout,
        "--run-timeout",
        args.run_timeout,
    ]

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
    append_if_value(command, "--length", args.length)
    append_if_value(command, "--batch-size", args.batch_size)
    append_if_value(command, "--fps", args.fps)
    append_if_value(command, "--filename-prefix", args.filename_prefix)

    completed = subprocess.run(command, cwd=project_root, check=False)
    return completed.returncode


if __name__ == "__main__":
    raise SystemExit(main())
