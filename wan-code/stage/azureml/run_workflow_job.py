#!/usr/bin/env python3

from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
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


emit_preimport_breadcrumb("run_workflow_job_preimport.log", "module_import_start")

try:
    from .aml_project_layout import app_root as resolve_app_root
    from .aml_project_layout import project_root as resolve_project_root
    from .workflow_profiles import all_image_input_keys
    from . import workflow_utils
except ImportError:
    from aml_project_layout import app_root as resolve_app_root
    from aml_project_layout import project_root as resolve_project_root
    from workflow_profiles import all_image_input_keys
    import workflow_utils


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--workflow",
        required=True,
        help="Path to a ComfyUI workflow JSON file. Supports API prompts and editor workflows.",
    )
    parser.add_argument("--models-dir", required=True, help="Mounted AzureML input that contains the local models/ tree.")
    parser.add_argument("--output-dir", required=True, help="AzureML output folder.")
    parser.add_argument("--port", type=int, default=8188)
    parser.add_argument("--startup-timeout", type=int, default=900)
    parser.add_argument("--run-timeout", type=int, default=7200)
    parser.add_argument("--enable-custom-nodes", action="store_true")
    parser.add_argument(
        "--comfy-low-vram",
        action="store_true",
        help="Start ComfyUI in low-VRAM mode so large model layers are streamed from system memory.",
    )
    parser.add_argument(
        "--comfy-disable-smart-memory",
        action="store_true",
        help="Aggressively offload inactive models from VRAM between workflow stages.",
    )
    parser.add_argument(
        "--comfy-reserve-vram",
        type=float,
        default=None,
        help="Amount of GPU memory, in GiB, for ComfyUI to keep free.",
    )
    parser.add_argument(
        "--positive-prompt",
        "--clip-text-encode-prompt",
        dest="positive_prompt",
        default=None,
    )
    parser.add_argument("--negative-prompt", default=None)
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--steps", type=int, default=None)
    parser.add_argument("--cfg", type=float, default=None)
    parser.add_argument("--sampler-name", default=None)
    parser.add_argument("--scheduler", default=None)
    parser.add_argument("--denoise", type=float, default=None)
    parser.add_argument("--width", type=int, default=None)
    parser.add_argument("--height", type=int, default=None)
    parser.add_argument("--duration-seconds", type=float, default=None)
    parser.add_argument("--length", type=int, default=None)
    parser.add_argument("--batch-size", type=int, default=None)
    parser.add_argument("--fps", type=float, default=None)
    parser.add_argument("--filename-prefix", default=None)
    for key in all_image_input_keys():
        flag_prefix = key.replace("_", "-")
        parser.add_argument(f"--{flag_prefix}-url", default=None)
        parser.add_argument(f"--{flag_prefix}-filename", default=None)
    return parser.parse_args()


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")


def append_debug_progress(output_dir: Path, stage: str, **details: object) -> None:
    payload = {
        "stage": stage,
        "pid": os.getpid(),
        "details": details,
    }
    append_jsonl_entry(resolve_log_write_paths(output_dir, "run_debug_progress.jsonl"), payload)


def read_text_tail(path: Path, max_chars: int = 8000) -> str | None:
    if not path.exists():
        return None
    text = path.read_text(encoding="utf-8", errors="replace")
    if len(text) <= max_chars:
        return text
    return text[-max_chars:]


def linked_node_id(link_value: object) -> str | None:
    if isinstance(link_value, list) and len(link_value) == 2 and isinstance(link_value[0], str):
        return link_value[0]
    return None


def update_ksamplers(prompt: dict, args: argparse.Namespace) -> None:
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in {"KSampler", "KSamplerAdvanced"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.seed is not None:
            if class_type == "KSamplerAdvanced":
                inputs["noise_seed"] = args.seed
            else:
                inputs["seed"] = args.seed
        if args.steps is not None:
            inputs["steps"] = args.steps
        if args.cfg is not None:
            inputs["cfg"] = args.cfg
        if args.sampler_name is not None:
            inputs["sampler_name"] = args.sampler_name
        if args.scheduler is not None:
            inputs["scheduler"] = args.scheduler
        if args.denoise is not None and "denoise" in inputs:
            inputs["denoise"] = args.denoise


def update_conditioning(prompt: dict, args: argparse.Namespace) -> None:
    for node in prompt.values():
        if node.get("class_type") not in {"KSampler", "WanImageToVideo", "Wan22ImageToVideoLatent"}:
            continue
        inputs = node.get("inputs", {})
        positive_id = linked_node_id(inputs.get("positive"))
        negative_id = linked_node_id(inputs.get("negative"))

        if args.positive_prompt is not None and positive_id and positive_id in prompt:
            prompt[positive_id].setdefault("inputs", {})["text"] = args.positive_prompt
        if args.negative_prompt is not None and negative_id and negative_id in prompt:
            prompt[negative_id].setdefault("inputs", {})["text"] = args.negative_prompt


def update_wan_latent_nodes(prompt: dict, args: argparse.Namespace) -> None:
    for node in prompt.values():
        if node.get("class_type") not in {"Wan22ImageToVideoLatent", "WanImageToVideo"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.width is not None:
            inputs["width"] = args.width
        if args.height is not None:
            inputs["height"] = args.height
        if args.length is not None:
            inputs["length"] = args.length
        if args.batch_size is not None:
            inputs["batch_size"] = args.batch_size


def update_save_nodes(prompt: dict, args: argparse.Namespace) -> None:
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in {"SaveWEBM", "SaveAnimatedWEBP", "SaveImage"}:
            continue
        inputs = node.setdefault("inputs", {})
        if args.filename_prefix is not None:
            inputs["filename_prefix"] = args.filename_prefix
        if args.fps is not None and "fps" in inputs:
            inputs["fps"] = args.fps


def patch_prompt(prompt: dict, args: argparse.Namespace) -> dict:
    update_ksamplers(prompt, args)
    update_conditioning(prompt, args)
    update_wan_latent_nodes(prompt, args)
    update_save_nodes(prompt, args)
    return prompt


def download_file(url: str, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(url) as response, destination.open("wb") as output:
        shutil.copyfileobj(response, output, length=16 * 1024 * 1024)


def prepare_input_images(prompt: dict, args: argparse.Namespace, input_dir: Path) -> None:
    if args.input_image_url is None:
        return

    load_image_nodes = [node for node in prompt.values() if node.get("class_type") == "LoadImage"]
    if not load_image_nodes:
        raise ValueError("--input-image-url was provided but the workflow does not contain a LoadImage node.")

    if args.input_image_filename is not None:
        target_filename = args.input_image_filename
    else:
        filenames = {
            node.get("inputs", {}).get("image")
            for node in load_image_nodes
            if isinstance(node.get("inputs", {}).get("image"), str) and node.get("inputs", {}).get("image")
        }
        if len(filenames) == 1:
            target_filename = next(iter(filenames))
        else:
            target_filename = Path(urllib.parse.urlsplit(args.input_image_url).path).name

    if not target_filename:
        raise ValueError("Could not determine a target filename for --input-image-url.")

    for node in load_image_nodes:
        node.setdefault("inputs", {})["image"] = target_filename

    download_file(args.input_image_url, input_dir / target_filename)


MODEL_INPUTS = {
    "UNETLoader": ("unet_name", ("diffusion_models", "unet")),
    "CLIPLoader": ("clip_name", ("text_encoders", "clip")),
    "VAELoader": ("vae_name", ("vae",)),
}

TMP_ROOT = Path("/tmp")
NODE_LOCAL_TMP_ROOT_ENV = "AZ_BATCH_NODE_ROOT_DIR"
COMFY_TMP_ROOT_ENV = "AZUREML_COMFYUI_TMP_ROOT"
NODE_LOCAL_TMP_DIRNAME = "azureml-comfyui-tmp"
AML_USER_LOGS_ENV = "AZUREML_CR_HT_CAP_user_logs_PATH"
AML_LOGS_ENV = "AZUREML_CR_HT_CAP_logs_PATH"
LTX_MINIMAL_CUSTOM_NODES_ENV = "AZUREML_LTX_MINIMAL_CUSTOM_NODES"
LTX_MINIMAL_CUSTOM_NODE_WORKFLOWS = frozenset({"ltx_2_first_last_ttp_workflow.json"})
LTX_REQUIRED_CUSTOM_NODE_PACKAGES = ("azureml_ltx_workflow_compat", "ComfyUI-LTXVideo")


def resolve_tmp_root(default: Path = TMP_ROOT) -> Path:
    candidates: list[Path] = []

    configured = os.getenv(COMFY_TMP_ROOT_ENV)
    if configured:
        candidates.append(Path(configured).expanduser())

    batch_root = os.getenv(NODE_LOCAL_TMP_ROOT_ENV)
    if batch_root:
        candidates.append(Path(batch_root) / NODE_LOCAL_TMP_DIRNAME)

    # AML compute nodes typically expose the large ephemeral disk under /mnt, while /tmp
    # often lands on the much smaller container/root overlay filesystem.
    candidates.append(Path("/mnt") / NODE_LOCAL_TMP_DIRNAME)

    for candidate in candidates:
        try:
            candidate.mkdir(parents=True, exist_ok=True)
        except OSError:
            continue
        if os.access(candidate, os.W_OK | os.X_OK):
            return candidate

    default.mkdir(parents=True, exist_ok=True)
    return default


def resolve_aml_log_dir(*env_names: str) -> Path | None:
    for env_name in env_names:
        configured = os.getenv(env_name)
        if not configured:
            continue
        candidate = Path(configured).expanduser()
        try:
            candidate.mkdir(parents=True, exist_ok=True)
        except OSError:
            continue
        if os.access(candidate, os.W_OK | os.X_OK):
            return candidate
    return None


def resolve_log_write_paths(output_dir: Path, filename: str) -> list[Path]:
    watched_logs_dir = resolve_aml_log_dir(AML_USER_LOGS_ENV, AML_LOGS_ENV)
    if watched_logs_dir is not None:
        return [watched_logs_dir / filename]
    return [output_dir / filename]


def append_jsonl_entry(paths: list[Path], payload: dict[str, object]) -> None:
    line = json.dumps(payload, sort_keys=True) + "\n"
    last_error: OSError | None = None
    wrote_any = False
    for path in paths:
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as handle:
                handle.write(line)
            wrote_any = True
        except OSError as exc:
            last_error = exc
    if not wrote_any and last_error is not None:
        raise last_error


def validate_required_models(prompt: dict, models_dir: Path) -> None:
    missing: list[str] = []
    for node in prompt.values():
        class_type = node.get("class_type")
        if class_type not in MODEL_INPUTS:
            continue
        input_name, folders = MODEL_INPUTS[class_type]
        filename = node.get("inputs", {}).get(input_name)
        if not isinstance(filename, str) or not filename:
            continue
        if any((models_dir / folder / filename).is_file() for folder in folders):
            continue
        expected_paths = ", ".join(str(models_dir / folder / filename) for folder in folders)
        missing.append(f"{class_type}:{filename} (looked in {expected_paths})")
    if missing:
        raise FileNotFoundError(
            "Mounted models input is missing workflow-required files:\n" + "\n".join(missing)
        )


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


def http_get_json(url: str) -> dict:
    with urllib.request.urlopen(url) as response:
        return json.loads(response.read().decode("utf-8"))


def http_post_json(url: str, payload: dict) -> dict:
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(request) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(
            f"HTTP POST {url} failed with status {exc.code}: {exc.reason}\nResponse body:\n{body}"
        ) from exc


def build_startup_failure(
    message: str,
    *,
    proc: subprocess.Popen[bytes] | None = None,
    log_path: Path | None = None,
    last_error: Exception | None = None,
) -> str:
    parts = [message]
    if proc is not None and proc.poll() is not None:
        parts[0] = f"{message} (exit code {proc.returncode})"
    if last_error is not None:
        parts.append(f"Last probe error: {last_error}")
    if log_path is not None:
        log_tail = read_text_tail(log_path)
        if log_tail:
            parts.append("--- ComfyUI log tail ---")
            parts.append(log_tail)
    return "\n".join(parts)


def wait_for_server(
    base_url: str,
    timeout_seconds: int,
    *,
    proc: subprocess.Popen[bytes] | None = None,
    log_path: Path | None = None,
) -> None:
    deadline = time.time() + timeout_seconds
    last_error: Exception | None = None
    while time.time() < deadline:
        if proc is not None and proc.poll() is not None:
            raise RuntimeError(
                build_startup_failure(
                    "ComfyUI exited before becoming ready",
                    proc=proc,
                    log_path=log_path,
                    last_error=last_error,
                )
            )
        try:
            http_get_json(f"{base_url}/history")
            return
        except Exception as exc:  # pragma: no cover - best effort polling path
            last_error = exc
            time.sleep(2)
    raise TimeoutError(
        build_startup_failure(
            f"ComfyUI did not become ready within {timeout_seconds}s",
            proc=proc,
            log_path=log_path,
            last_error=last_error,
        )
    )


def wait_for_history(base_url: str, prompt_id: str, timeout_seconds: int) -> dict:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        history = http_get_json(f"{base_url}/history/{prompt_id}")
        if prompt_id in history:
            return history[prompt_id]
        time.sleep(5)
    raise TimeoutError(f"Workflow {prompt_id} did not finish within {timeout_seconds}s")


def copy_tree(src: Path, dst: Path) -> list[str]:
    copied: list[str] = []
    dst.mkdir(parents=True, exist_ok=True)
    for path in sorted(src.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(src)
        target = dst / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
        copied.append(str(relative))
    return copied


def copy_file(src: Path, dst: Path) -> str:
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    return str(dst)


def terminate_process(proc: subprocess.Popen[bytes]) -> None:
    if proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=30)


def create_job_root(tmp_root: Path = TMP_ROOT) -> Path:
    return Path(tempfile.mkdtemp(prefix="comfyui-job-", dir=str(tmp_root)))


def workflow_uses_ltx_minimal_custom_nodes(workflow_path: Path) -> bool:
    return workflow_path.name in LTX_MINIMAL_CUSTOM_NODE_WORKFLOWS


def custom_node_whitelist(
    *,
    workflow_path: Path,
    enable_custom_nodes: bool,
) -> tuple[str, ...]:
    if not enable_custom_nodes:
        return ()
    if workflow_uses_ltx_minimal_custom_nodes(workflow_path):
        return LTX_REQUIRED_CUSTOM_NODE_PACKAGES
    return ()


def build_runtime_env(
    *,
    source_root: Path,
    models_dir: Path,
    job_root: Path,
    tmp_root: Path = TMP_ROOT,
    minimal_ltx_custom_nodes: bool = False,
) -> dict[str, str]:
    cache_dir = job_root / "cache"
    home_dir = job_root / "home"

    env = os.environ.copy()
    env["COMFY_MODELS_DIR"] = str(models_dir)
    env["COMFY_APP_DIR"] = str(source_root)
    env["TMPDIR"] = str(tmp_root)
    env["TMP"] = str(tmp_root)
    env["TEMP"] = str(tmp_root)
    env["HOME"] = str(home_dir)
    env["XDG_CACHE_HOME"] = str(cache_dir)
    env["XDG_CONFIG_HOME"] = str(job_root / "config")
    env["XDG_DATA_HOME"] = str(job_root / "share")
    env["HF_HOME"] = str(cache_dir / "huggingface")
    env["HUGGINGFACE_HUB_CACHE"] = str(cache_dir / "huggingface" / "hub")
    env["HF_ASSETS_CACHE"] = str(cache_dir / "huggingface" / "assets")
    env["HF_DATASETS_CACHE"] = str(cache_dir / "huggingface" / "datasets")
    env["TRANSFORMERS_CACHE"] = str(cache_dir / "huggingface" / "transformers")
    env["SENTENCE_TRANSFORMERS_HOME"] = str(cache_dir / "sentence_transformers")
    env["TORCH_HOME"] = str(cache_dir / "torch")
    env["TORCH_EXTENSIONS_DIR"] = str(cache_dir / "torch_extensions")
    env["TORCHINDUCTOR_CACHE_DIR"] = str(cache_dir / "torchinductor")
    env["PIP_CACHE_DIR"] = str(cache_dir / "pip")
    env["CUDA_CACHE_PATH"] = str(cache_dir / "cuda")
    env["TRITON_CACHE_DIR"] = str(cache_dir / "triton")
    env["NUMBA_CACHE_DIR"] = str(cache_dir / "numba")
    env["MPLCONFIGDIR"] = str(cache_dir / "matplotlib")
    env["PYTHONPYCACHEPREFIX"] = str(cache_dir / "pycache")
    env["JOBLIB_TEMP_FOLDER"] = str(tmp_root / "joblib")
    env["HF_HUB_DISABLE_TELEMETRY"] = "1"
    env["DO_NOT_TRACK"] = "1"
    if minimal_ltx_custom_nodes:
        env[LTX_MINIMAL_CUSTOM_NODES_ENV] = "1"
    else:
        env.pop(LTX_MINIMAL_CUSTOM_NODES_ENV, None)
    return env


def build_comfy_command(
    *,
    source_root: Path,
    workflow_path: Path,
    job_root: Path,
    output_dir: Path,
    extra_model_paths: Path,
    port: int,
    enable_custom_nodes: bool,
    low_vram: bool = False,
    disable_smart_memory: bool = False,
    reserve_vram_gb: float | None = None,
) -> list[str]:
    comfy_user_dir = job_root / "user"
    comfy_output_dir = job_root / "output"
    comfy_input_dir = job_root / "input"
    comfy_db_path = comfy_user_dir / "comfyui.db"
    command = [
        sys.executable,
        str(source_root / "main.py"),
        "--base-directory",
        str(job_root),
        "--listen",
        "127.0.0.1",
        "--port",
        str(port),
        "--disable-auto-launch",
        "--preview-method",
        "none",
        "--temp-directory",
        str(job_root),
        "--output-directory",
        str(comfy_output_dir),
        "--input-directory",
        str(comfy_input_dir),
        "--user-directory",
        str(comfy_user_dir),
        "--database-url",
        f"sqlite:///{comfy_db_path}",
        "--extra-model-paths-config",
        str(extra_model_paths),
        "--disable-api-nodes",
        "--cache-none",
        "--log-stdout",
    ]
    if low_vram:
        command.append("--lowvram")
    if disable_smart_memory:
        command.append("--disable-smart-memory")
    if reserve_vram_gb is not None:
        command.extend(["--reserve-vram", f"{reserve_vram_gb:g}"])
    whitelist = custom_node_whitelist(
        workflow_path=workflow_path,
        enable_custom_nodes=enable_custom_nodes,
    )
    if not enable_custom_nodes:
        command.append("--disable-all-custom-nodes")
    elif whitelist:
        command.append("--disable-all-custom-nodes")
        command.append("--whitelist-custom-nodes")
        command.extend(whitelist)
    append_debug_progress(
        output_dir,
        "comfy_command_built",
        workflow=str(workflow_path),
        enable_custom_nodes=enable_custom_nodes,
        custom_node_whitelist=list(whitelist),
        low_vram=low_vram,
        disable_smart_memory=disable_smart_memory,
        reserve_vram_gb=reserve_vram_gb,
    )
    return command


def main() -> int:
    args = parse_args()

    project_root = resolve_project_root()
    source_root = resolve_app_root(project_root)
    workflow_path = Path(args.workflow)
    if not workflow_path.is_absolute():
        workflow_path = project_root / workflow_path
    workflow_path = workflow_path.resolve()
    models_dir = Path(args.models_dir).expanduser()
    output_dir = Path(args.output_dir).expanduser()
    append_debug_progress(
        output_dir,
        "runner_start",
        workflow=str(workflow_path),
        models_dir=str(models_dir),
        aml_output_dir=str(output_dir),
        disk_usage=disk_usage_snapshot(Path("/"), Path("/tmp")),
    )

    # Prefer the large node-local AML temp disk over the smaller container/root overlay.
    tmp_root = resolve_tmp_root()
    job_root = create_job_root(tmp_root)
    append_debug_progress(
        output_dir,
        "job_root_created",
        tmp_root=str(tmp_root),
        job_root=str(job_root),
        disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
    )
    comfy_output_dir = job_root / "output"
    comfy_input_dir = job_root / "input"
    comfy_user_dir = job_root / "user"
    comfy_custom_nodes_dir = job_root / "custom_nodes"
    comfy_db_path = comfy_user_dir / "comfyui.db"
    runtime_logs_dir = job_root / "logs"
    runtime_metadata_dir = job_root / "metadata"
    comfy_log_path = runtime_logs_dir / "comfyui_stdout.log"
    extra_model_paths = project_root / "azureml" / "extra_model_paths.yaml"
    runtime_home_dir = job_root / "home"
    runtime_cache_dir = job_root / "cache"
    runtime_config_dir = job_root / "config"
    runtime_data_dir = job_root / "share"

    comfy_output_dir.mkdir(parents=True, exist_ok=True)
    comfy_input_dir.mkdir(parents=True, exist_ok=True)
    comfy_user_dir.mkdir(parents=True, exist_ok=True)
    comfy_custom_nodes_dir.mkdir(parents=True, exist_ok=True)
    runtime_logs_dir.mkdir(parents=True, exist_ok=True)
    runtime_metadata_dir.mkdir(parents=True, exist_ok=True)
    runtime_home_dir.mkdir(parents=True, exist_ok=True)
    runtime_cache_dir.mkdir(parents=True, exist_ok=True)
    runtime_config_dir.mkdir(parents=True, exist_ok=True)
    runtime_data_dir.mkdir(parents=True, exist_ok=True)

    if not workflow_path.is_file():
        raise FileNotFoundError(f"Workflow file not found: {workflow_path}")
    if not extra_model_paths.is_file():
        raise FileNotFoundError(f"Extra model paths config not found: {extra_model_paths}")

    workflow_payload = workflow_utils.load_workflow_payload(workflow_path)
    minimal_ltx_custom_nodes = workflow_uses_ltx_minimal_custom_nodes(workflow_path)

    env = build_runtime_env(
        source_root=source_root,
        models_dir=models_dir,
        job_root=job_root,
        tmp_root=tmp_root,
        minimal_ltx_custom_nodes=minimal_ltx_custom_nodes,
    )
    command = build_comfy_command(
        source_root=source_root,
        workflow_path=workflow_path,
        job_root=job_root,
        output_dir=output_dir,
        extra_model_paths=extra_model_paths,
        port=args.port,
        enable_custom_nodes=args.enable_custom_nodes,
        low_vram=getattr(args, "comfy_low_vram", False),
        disable_smart_memory=getattr(args, "comfy_disable_smart_memory", False),
        reserve_vram_gb=getattr(args, "comfy_reserve_vram", None),
    )
    watched_log_dir = resolve_aml_log_dir(AML_USER_LOGS_ENV, AML_LOGS_ENV)
    if watched_log_dir is not None:
        comfy_log_path = watched_log_dir / comfy_log_path.name

    proc: subprocess.Popen[bytes] | None = None
    comfy_log_handle = None
    try:
        comfy_log_handle = comfy_log_path.open("wb")
        proc = subprocess.Popen(
            command,
            cwd=job_root,
            env=env,
            stdout=comfy_log_handle,
            stderr=subprocess.STDOUT,
        )
        append_debug_progress(
            output_dir,
            "comfyui_spawned",
            comfy_pid=proc.pid,
            log_path=str(comfy_log_path),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        base_url = f"http://127.0.0.1:{args.port}"
        wait_for_server(base_url, args.startup_timeout, proc=proc, log_path=comfy_log_path)
        append_debug_progress(
            output_dir,
            "comfyui_ready",
            comfy_pid=proc.pid,
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        if workflow_utils.workflow_is_api_prompt(workflow_payload):
            prompt = workflow_payload
        elif workflow_utils.workflow_is_editor_graph(workflow_payload):
            workflow_utils.prepare_editor_workflow(workflow_payload, args)
            object_info = http_get_json(f"{base_url}/object_info")
            debug_class_types = {
                "LoadImage",
                "ResizeImageMaskNode",
                "LTXVPreprocess",
                "LTXVImgToVideoConditionOnly",
                "LTXVImgToVideoInplace",
                "CreateVideo",
                "SaveVideo",
                "ImageResizeKJv2",
                "LTXVFirstLastFrameControl_TTP",
                "VHS_VideoCombine",
            }
            workflow_editor_nodes = [
                {
                    "id": str(node.get("id")),
                    "class_type": workflow_utils._editor_node_class_type(node),
                }
                for node in workflow_payload.get("nodes", [])
                if workflow_utils._editor_node_class_type(node) in debug_class_types
            ]
            reachable_debug = sorted(
                workflow_utils._collect_editor_reachable_node_ids(workflow_payload, object_info)
            )
            print(
                "EDITOR_DEBUG object_info_present="
                + json.dumps({name: (name in object_info) for name in sorted(debug_class_types)}, sort_keys=True)
            )
            print("EDITOR_DEBUG workflow_nodes=" + json.dumps(workflow_editor_nodes, sort_keys=True))
            print("EDITOR_DEBUG reachable_ids=" + json.dumps(reachable_debug))

            prompt = workflow_utils.convert_editor_workflow_to_prompt(
                workflow_payload,
                object_info,
            )
            prompt_class_counter = Counter(
                str(node.get("class_type"))
                for node in prompt.values()
                if isinstance(node, dict) and node.get("class_type") in debug_class_types
            )
            print("EDITOR_DEBUG prompt_classes=" + json.dumps(prompt_class_counter, sort_keys=True))

            has_any_image = (
                args.input_image_url is not None
                or getattr(args, "start_image_url", None) is not None
                or getattr(args, "end_image_url", None) is not None
            )
            if (
                has_any_image
                and not any(
                    isinstance(node, dict) and node.get("class_type") == "LoadImage"
                    for node in prompt.values()
                )
                and any(
                    node.get("class_type") == "LoadImage"
                    for node in workflow_editor_nodes
                )
            ):
                print("EDITOR_DEBUG reconverting_without_reachability=true")
                prompt = workflow_utils.convert_editor_workflow_to_prompt(
                    workflow_payload,
                    object_info,
                    reachable_only=False,
                )
                prompt_class_counter = Counter(
                    str(node.get("class_type"))
                    for node in prompt.values()
                    if isinstance(node, dict) and node.get("class_type") in debug_class_types
                )
                print(
                    "EDITOR_DEBUG prompt_classes_after_reconvert="
                    + json.dumps(prompt_class_counter, sort_keys=True)
                )
        else:
            raise ValueError(f"Unsupported workflow format in {workflow_path}")
        prompt = workflow_utils.patch_prompt(prompt, args)
        workflow_utils.prepare_input_images(prompt, args, comfy_input_dir)
        workflow_utils.validate_required_models(prompt, models_dir)
        append_debug_progress(
            output_dir,
            "prompt_prepared",
            prompt_node_count=len(prompt),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        final_prompt_path = runtime_metadata_dir / "final_prompt.json"
        write_json(final_prompt_path, prompt)
        submission = http_post_json(f"{base_url}/prompt", {"prompt": prompt})
        prompt_id = submission["prompt_id"]
        append_debug_progress(
            output_dir,
            "prompt_submitted",
            prompt_id=prompt_id,
        )
        history = wait_for_history(base_url, prompt_id, args.run_timeout)
        append_debug_progress(
            output_dir,
            "history_complete",
            prompt_id=prompt_id,
            history_keys=sorted(history.keys()),
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        history_path = runtime_metadata_dir / "history.json"
        write_json(history_path, history)
        copied_files = copy_tree(comfy_output_dir, output_dir)
        run_summary_path = runtime_metadata_dir / "run_summary.json"
        write_json(
            run_summary_path,
            {
                "prompt_id": prompt_id,
                "models_dir": str(models_dir),
                "output_files": copied_files,
            },
        )
        copy_file(final_prompt_path, output_dir / final_prompt_path.name)
        copy_file(history_path, output_dir / history_path.name)
        copy_file(run_summary_path, output_dir / run_summary_path.name)
        append_debug_progress(
            output_dir,
            "outputs_copied",
            output_files=copied_files,
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        if not copied_files:
            raise RuntimeError("Workflow completed but no files were written to the ComfyUI output directory.")
        return 0
    except Exception as exc:
        append_debug_progress(
            output_dir,
            "runner_exception",
            error=repr(exc),
            proc_returncode=proc.poll() if proc is not None else None,
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        raise
    finally:
        append_debug_progress(
            output_dir,
            "runner_finally",
            proc_returncode=proc.poll() if proc is not None else None,
            disk_usage=disk_usage_snapshot(Path("/"), Path("/mnt"), tmp_root, job_root),
        )
        if proc is not None:
            terminate_process(proc)
        if comfy_log_handle is not None:
            comfy_log_handle.close()
        if comfy_log_path.exists():
            try:
                copy_file(comfy_log_path, output_dir / comfy_log_path.name)
            except OSError:
                pass
        shutil.rmtree(job_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
