#!/usr/bin/env python3

from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime
import json
from pathlib import Path
import shlex
import tempfile
from typing import Any

from azure.ai.ml import Input, MLClient, Output, command
from azure.ai.ml.constants import AssetTypes
from azure.ai.ml.entities import BuildContext, Data, Environment, Model

try:
    from .credential import build_credential
    from .cost_guard import job_controls, validate_scope, public_error, verify_created_job
    from .aml_project_layout import models_root as resolve_models_root
    from .aml_project_layout import project_root as resolve_project_root
    from . import defaults as aml_defaults
    from .workflow_profiles import (
        PROFILES,
        WorkflowProfile,
        all_image_input_keys,
        duration_seconds_to_length,
        get_profile,
    )
    from .workflow_utils import load_workflow_payload, stage_models_package
except ImportError:
    from credential import build_credential
    from cost_guard import job_controls, validate_scope, public_error, verify_created_job
    from aml_project_layout import models_root as resolve_models_root
    from aml_project_layout import project_root as resolve_project_root
    import defaults as aml_defaults
    from workflow_profiles import (
        PROFILES,
        WorkflowProfile,
        all_image_input_keys,
        duration_seconds_to_length,
        get_profile,
    )
    from workflow_utils import load_workflow_payload, stage_models_package


DEFAULT_SUBSCRIPTION_ID = aml_defaults.DEFAULT_SUBSCRIPTION_ID
DEFAULT_RESOURCE_GROUP = aml_defaults.DEFAULT_RESOURCE_GROUP
DEFAULT_WORKSPACE_NAME = aml_defaults.DEFAULT_WORKSPACE_NAME
DEFAULT_COMPUTE = aml_defaults.DEFAULT_COMPUTE
DEFAULT_PROFILE = aml_defaults.DEFAULT_PROFILE
DEFAULT_ENVIRONMENT_NAME = aml_defaults.DEFAULT_ENVIRONMENT_NAME
DEFAULT_MODELS_NAME = aml_defaults.DEFAULT_MODELS_NAME

COMPUTE_HELP = aml_defaults.COMPUTE_HELP
MISSING_COMPUTE_ERROR = aml_defaults.MISSING_COMPUTE_ERROR
normalize_compute_name = aml_defaults.normalize_compute_name

MODELS_ASSET_KIND_DATA = "data"
MODELS_ASSET_KIND_MODEL = "model"
MODELS_ASSET_KINDS = (MODELS_ASSET_KIND_DATA, MODELS_ASSET_KIND_MODEL)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout-seconds", type=int, default=7200)
    parser.add_argument("--subscription-id", default=DEFAULT_SUBSCRIPTION_ID)
    parser.add_argument("--resource-group", default=DEFAULT_RESOURCE_GROUP)
    parser.add_argument("--workspace-name", default=DEFAULT_WORKSPACE_NAME)
    parser.add_argument("--profile", default=DEFAULT_PROFILE, choices=sorted(PROFILES))
    parser.add_argument(
        "--compute",
        default=DEFAULT_COMPUTE,
        required=DEFAULT_COMPUTE is None,
        help=COMPUTE_HELP,
    )
    parser.add_argument("--environment-name", default=None)
    parser.add_argument("--models-name", default=None)
    parser.add_argument("--version", default=datetime.now().strftime("%Y%m%d%H%M%S"))
    parser.add_argument(
        "--code-path",
        default=None,
        help="Remote AzureML datastore URI to the uploaded repo folder. Mounted as a job input to avoid AML code staging.",
    )
    parser.add_argument(
        "--models-path",
        default=None,
        help=(
            "Remote AzureML datastore URI or workspace asset reference for the models input. "
            "When --models-asset-kind=model, pass an azureml:<model>:<version> reference."
        ),
    )
    parser.add_argument(
        "--models-asset-kind",
        choices=MODELS_ASSET_KINDS,
        default=None,
        help=(
            "How to treat the models input and auto-staged workflow bundle. "
            "'data' preserves the existing uri_folder flow. "
            "'model' registers the minimal workflow bundle as a custom_model asset "
            "and mounts it as a model input."
        ),
    )
    parser.add_argument(
        "--environment-id",
        default=None,
        help="Existing AzureML environment reference to use directly. Skips environment registration.",
    )
    parser.add_argument(
        "--environment-image",
        default=None,
        help="Container image to register as this environment version instead of building from the local context.",
    )
    parser.add_argument(
        "--workflow",
        default=None,
        help="Repo-relative path to the workflow JSON. Supports API prompts and editor workflows.",
    )
    parser.add_argument(
        "--skip-model-upload",
        "--skip-data-upload",
        dest="skip_data_upload",
        action="store_true",
        help=(
            "Reuse an existing registered models asset version instead of uploading a new minimal bundle. "
            "For --models-asset-kind=data this expects a data asset; for model it expects a custom_model asset."
        ),
    )
    parser.add_argument(
        "--stream",
        action="store_true",
        help="Stream the submitted job after creation.",
    )
    parser.add_argument(
        "--install-runtime-deps",
        action="store_true",
        help="Install azureml/context/requirements-azureml-runtime.txt at job start.",
    )
    parser.add_argument(
        "--positive-prompt",
        "--clip-text-encode-prompt",
        dest="positive_prompt",
        default=None,
        help=(
            "Positive prompt override. For the LTX profile, --clip-text-encode-prompt "
            "maps to the workflow's CLIP Text Encode (Prompt) node."
        ),
    )
    parser.add_argument(
        "--negative-prompt",
        default=None,
    )
    parser.add_argument("--seed", type=int, default=857413335685417)
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
    args = parser.parse_args()
    args.compute = normalize_compute_name(args.compute)
    if not args.compute:
        parser.error(MISSING_COMPUTE_ERROR)
    apply_profile_defaults(args)
    validate_required_image_inputs(parser, args)
    return args


def apply_profile_defaults(args: argparse.Namespace) -> None:
    profile = get_profile(args.profile)
    args.environment_name = args.environment_name or profile.environment_name
    args.models_name = args.models_name or profile.models_name
    args.models_asset_kind = args.models_asset_kind or profile.models_asset_kind
    args.workflow = args.workflow or profile.workflow
    if args.negative_prompt is None:
        args.negative_prompt = profile.negative_prompt
    if args.steps is None and profile.steps is not None:
        args.steps = profile.steps
    if args.cfg is None and profile.cfg is not None:
        args.cfg = profile.cfg
    if args.sampler_name is None and profile.sampler_name is not None:
        args.sampler_name = profile.sampler_name
    if args.scheduler is None and profile.scheduler is not None:
        args.scheduler = profile.scheduler
    if args.denoise is None and profile.denoise is not None:
        args.denoise = profile.denoise
    args.width = profile.width if args.width is None else args.width
    args.height = profile.height if args.height is None else args.height
    args.batch_size = profile.batch_size if args.batch_size is None else args.batch_size
    args.fps = profile.fps if args.fps is None else args.fps
    args.filename_prefix = profile.filename_prefix if args.filename_prefix is None else args.filename_prefix
    if args.duration_seconds is None:
        if args.length is not None and profile.length_mode == "seconds_plus_one" and args.fps:
            args.duration_seconds = max(1.0, (args.length - 1) / args.fps)
        else:
            args.duration_seconds = profile.duration_seconds
    if profile.length_mode == "seconds_plus_one":
        args.duration_seconds = max(1.0, int(float(args.duration_seconds) + 0.5))
    if args.length is None:
        args.length = duration_seconds_to_length(profile, args.duration_seconds, fps=args.fps)


def default_profile_length(profile: WorkflowProfile) -> int:
    return duration_seconds_to_length(profile, profile.duration_seconds, fps=profile.fps)


def validate_required_image_inputs(parser: argparse.ArgumentParser, args: argparse.Namespace) -> None:
    profile = get_profile(args.profile)
    for spec in profile.image_inputs:
        if not spec.required:
            continue
        if getattr(args, f"{spec.key}_url", None):
            continue
        flag = f"--{spec.key.replace('_', '-')}-url"
        parser.error(f"{spec.label} is required for the {profile.label} profile. Provide {flag}.")


def resolved_profile(args: argparse.Namespace) -> WorkflowProfile:
    return get_profile(args.profile)


def repo_root() -> Path:
    return resolve_project_root()


def ml_client(args: argparse.Namespace) -> MLClient:
    validate_scope(args)
    return MLClient(
        credential=build_credential(),
        subscription_id=args.subscription_id,
        resource_group_name=args.resource_group,
        workspace_name=args.workspace_name,
    )


def models_asset_kind(args: argparse.Namespace) -> str:
    return getattr(args, "models_asset_kind", MODELS_ASSET_KIND_DATA)


def register_environment(client: MLClient, args: argparse.Namespace, root: Path) -> Environment:
    profile = resolved_profile(args)
    if args.environment_image:
        env = Environment(
            name=args.environment_name,
            version=args.version,
            description=f"ComfyUI {profile.label} workflow environment built from a prebuilt container image.",
            image=args.environment_image,
        )
    else:
        env = Environment(
            name=args.environment_name,
            version=args.version,
            description=f"ComfyUI {profile.label} workflow environment built from the local repo.",
            build=BuildContext(
                path=str(root / "azureml" / "context"),
                dockerfile_path="Dockerfile",
            ),
        )
    return client.environments.create_or_update(env)


@contextmanager
def staged_models_bundle(args: argparse.Namespace, root: Path):
    profile = resolved_profile(args)
    models_dir = resolve_models_root(root)
    if not models_dir.is_dir():
        raise FileNotFoundError(f"Could not find the local models directory at {models_dir}")

    workflow_path = Path(args.workflow)
    if not workflow_path.is_absolute():
        workflow_path = root / workflow_path
    workflow_payload = load_workflow_payload(workflow_path.resolve())

    with tempfile.TemporaryDirectory(prefix=f"aml-models-{profile.key}-") as temp_dir:
        staged_models_dir = Path(temp_dir) / "models"
        stage_models_package(models_dir, staged_models_dir, workflow_payload)
        yield profile, staged_models_dir


def register_models_data(client: MLClient, args: argparse.Namespace, root: Path) -> Data:
    with staged_models_bundle(args, root) as (profile, staged_models_dir):
        data = Data(
            name=args.models_name,
            version=args.version,
            description=f"Minimal ComfyUI model package for the {profile.label} workflow.",
            path=str(staged_models_dir),
            type="uri_folder",
        )
        return client.data.create_or_update(data)


def register_models_model(client: MLClient, args: argparse.Namespace, root: Path) -> Model:
    with staged_models_bundle(args, root) as (profile, staged_models_dir):
        model = Model(
            name=args.models_name,
            version=args.version,
            description=f"Minimal ComfyUI model package for the {profile.label} workflow.",
            path=str(staged_models_dir),
            type=AssetTypes.CUSTOM_MODEL,
        )
        return client.models.create_or_update(model)


def build_models_input(models_ref: str, *, asset_kind: str) -> Input:
    if asset_kind == MODELS_ASSET_KIND_MODEL:
        return Input(type=AssetTypes.CUSTOM_MODEL, path=models_ref, mode="ro_mount")
    return Input(type="uri_folder", path=models_ref, mode="ro_mount")


def workspace_asset_ref(name: str | None, version: str | None, fallback: str | None = None) -> str:
    if name and version:
        return f"azureml:{name}:{version}"
    if fallback:
        return fallback
    raise ValueError("Could not determine a workspace asset reference.")


def build_job(
    args: argparse.Namespace,
    root: Path,
    *,
    environment_ref: str,
    models_ref: str,
    profile: WorkflowProfile,
):
    controls = job_controls(args)
    command_prefix = ""
    if args.install_runtime_deps:
        command_prefix = (
            "python -m pip install --upgrade pip setuptools wheel && "
            "python -m pip install -r azureml/context/requirements-azureml-runtime.txt && "
        )
    runner_script = "azureml/bootstrap_ltx_and_run.py" if profile.uses_ltx_runner else "azureml/run_workflow_job.py"
    workflow_arg = shlex.quote(args.workflow)
    runner_script_arg = shlex.quote(runner_script)
    inputs = {
        "models": build_models_input(models_ref, asset_kind=models_asset_kind(args)),
    }
    runner_command_parts = [
        f"python {runner_script_arg}",
        f"--workflow {workflow_arg}",
        "--models-dir ${{inputs.models}}",
        "--output-dir ${{outputs.generated}}",
        "--startup-timeout 900",
        "--run-timeout 7200",
    ]
    if profile.comfy_low_vram:
        runner_command_parts.append("--comfy-low-vram")
    if profile.comfy_disable_smart_memory:
        runner_command_parts.append("--comfy-disable-smart-memory")
    if profile.comfy_reserve_vram_gb is not None:
        runner_command_parts.append(f"--comfy-reserve-vram {profile.comfy_reserve_vram_gb:g}")

    def append_input(
        name: str,
        value: Any,
        *,
        flag_name: str | None = None,
        quote: bool = False,
    ) -> None:
        if value is None:
            return
        if value == "":
            runner_flag = (flag_name or name).replace("_", "-")
            runner_command_parts.append(f'--{runner_flag} ""')
            return
        is_image_url = name in {f"{key}_url" for key in all_image_input_keys()}
        inputs[name] = Input(type="uri_file", path=value, mode="download") if is_image_url else value
        rendered_value = (
            '"${{inputs.%s}}"' % name
            if quote
            else "${{inputs.%s}}" % name
        )
        if is_image_url:
            rendered_value = '"file://${{inputs.%s}}"' % name
        runner_flag = flag_name or name
        runner_command_parts.append(f"--{runner_flag.replace('_', '-')} {rendered_value}")

    positive_prompt_input_name = profile.submit_prompt_input_name
    append_input(
        positive_prompt_input_name,
        args.positive_prompt,
        flag_name="positive_prompt",
        quote=True,
    )
    append_input("negative_prompt", args.negative_prompt, quote=True)
    append_input("seed", args.seed)
    append_input("steps", args.steps)
    append_input("cfg", args.cfg)
    append_input("sampler_name", args.sampler_name, quote=True)
    append_input("scheduler", args.scheduler, quote=True)
    append_input("denoise", args.denoise)
    append_input("width", args.width)
    append_input("height", args.height)
    append_input("duration_seconds", args.duration_seconds)
    append_input("length", args.length)
    append_input("batch_size", args.batch_size)
    append_input("fps", args.fps)
    append_input("filename_prefix", args.filename_prefix, quote=True)
    for key in all_image_input_keys():
        append_input(f"{key}_url", getattr(args, f"{key}_url", None), quote=True)
        append_input(f"{key}_filename", getattr(args, f"{key}_filename", None), quote=True)

    runner_command = command_prefix + " ".join(runner_command_parts)
    if profile.enable_custom_nodes:
        runner_command += " --enable-custom-nodes"

    generated_output_kwargs = {"type": "uri_folder", "mode": "rw_mount"}
    generated_output_path = getattr(args, "generated_output_path", None)
    if generated_output_path:
        generated_output_kwargs["path"] = generated_output_path

    command_kwargs = {
        **controls,
        "command": runner_command,
        "inputs": inputs,
        "outputs": {"generated": Output(**generated_output_kwargs)},
        "environment": environment_ref,
        "compute": args.compute,
        "experiment_name": profile.experiment_name,
        "display_name": f"{profile.display_name_prefix}{args.version}",
    }

    if args.code_path:
        inputs["repo"] = Input(type="uri_folder", path=args.code_path, mode="ro_mount")
        command_kwargs["command"] = f"cd \"${{{{inputs.repo}}}}\" && {runner_command}"
    else:
        command_kwargs["code"] = str(root)

    return command(**command_kwargs)


def summarize_asset(asset: Any) -> dict[str, Any]:
    return {
        "name": getattr(asset, "name", None),
        "version": getattr(asset, "version", None),
        "id": getattr(asset, "id", None),
    }


def resolve_environment_ref(client: MLClient, args: argparse.Namespace, root: Path) -> tuple[str, dict[str, Any]]:
    if args.environment_id:
        environment_ref = args.environment_id
        environment_info = {"id": environment_ref}
    else:
        environment = register_environment(client, args, root)
        environment_ref = environment.id or f"azureml:{args.environment_name}:{args.version}"
        environment_info = summarize_asset(environment)
    return environment_ref, environment_info


def resolve_models_ref(client: MLClient, args: argparse.Namespace, root: Path) -> tuple[str, dict[str, Any]]:
    asset_kind = models_asset_kind(args)
    if args.models_path:
        models_ref = args.models_path
        models_info = {"id": models_ref, "asset_kind": asset_kind}
    elif args.skip_data_upload:
        models_ref = workspace_asset_ref(args.models_name, args.version)
        models_info = {
            "name": args.models_name,
            "version": args.version,
            "id": models_ref,
            "asset_kind": asset_kind,
        }
    elif asset_kind == MODELS_ASSET_KIND_MODEL:
        model = register_models_model(client, args, root)
        models_ref = workspace_asset_ref(getattr(model, "name", None), getattr(model, "version", None), model.id)
        models_info = summarize_asset(model)
        models_info["asset_kind"] = asset_kind
    else:
        data = register_models_data(client, args, root)
        models_ref = data.id or f"azureml:{args.models_name}:{args.version}"
        models_info = summarize_asset(data)
        models_info["asset_kind"] = asset_kind
    return models_ref, models_info


def create_job_submission(args: argparse.Namespace) -> tuple[MLClient, Any, dict[str, Any]]:
    job_controls(args)
    root = repo_root()
    client = ml_client(args)
    profile = resolved_profile(args)
    environment_ref, environment_info = resolve_environment_ref(client, args, root)
    models_ref, models_info = resolve_models_ref(client, args, root)
    job = build_job(
        args,
        root,
        environment_ref=environment_ref,
        models_ref=models_ref,
        profile=profile,
    )
    created_job = client.jobs.create_or_update(job)
    created_job = verify_created_job(client, created_job, args)
    result = {
        "profile": profile.key,
        "environment": environment_info,
        "models": models_info,
        "job": {
            "name": created_job.name,
            "display_name": created_job.display_name,
            "status": created_job.status,
            "studio_url": created_job.studio_url,
        },
    }
    return client, created_job, result


def main() -> int:
    args = parse_args()
    client, created_job, result = create_job_submission(args)
    print(json.dumps({"environment": result["environment"]}, indent=2))
    print(json.dumps({"models": result["models"]}, indent=2))
    print(json.dumps({"job": result["job"]}, indent=2))

    if args.stream:
        client.jobs.stream(created_job.name)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
