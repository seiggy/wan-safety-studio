"""Stage pinned WAN source and a locked CPU-build context. No Azure, Docker or model downloads."""
from __future__ import annotations

import argparse
import ast
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
from opentelemetry import trace

from artifacts import bundle, files, process_environment, safe_path, sha256, write_json

HERE = Path(__file__).resolve().parent
PROFILES = json.loads((HERE / "profiles.json").read_text())
SOURCE_SHA = PROFILES["upstream"]["sourceSha"]
HELPERS = set(PROFILES["wan"]["helpers"])
WEIGHTS = {".safetensors", ".ckpt", ".pt", ".pth", ".onnx", ".gguf"}
BUILD_INPUTS = ("artifacts.py", "prepare.py", "models.py", "verify.py", "probe.py", "profiles.json",
                "telemetry.py", "runtime-ltx.patch", "wan-pinned-manifest.json", "pyproject.toml",
                "requirements-build.lock", "requirements-runtime.in", "requirements-runtime.lock", "Dockerfile")


def code_path(name):
    if not safe_path(name):
        return False
    if name in HELPERS or name in {"LICENSE", "NOTICE.txt"}:
        return True
    parts = name.split("/")
    return (len(parts) > 1 and parts[0] == "src" and
            parts[1].lower() not in {"models", "input", "output", "outputs", "user", "custom_nodes", "tests", "tests-unit", ".ci", ".github", "blueprints"} and
            Path(name).suffix.lower() not in WEIGHTS)


def run_git(source, *args):
    with trace.get_tracer("wan.gpu.build").start_as_current_span("source.git", record_exception=False, set_status_on_exception=False):
        environment = dict(process_environment(), GIT_NO_LAZY_FETCH="1", GIT_LFS_SKIP_SMUDGE="1")
        return subprocess.check_output(["git", "-c", "filter.lfs.process=", "-c", "filter.lfs.smudge=",
                                        "-c", "filter.lfs.required=false", "-C", str(source), *args], env=environment)


def stage_source(source: Path, output: Path):
    commit = run_git(source, "rev-parse", SOURCE_SHA + "^{commit}").decode().strip()
    if commit != SOURCE_SHA:
        raise ValueError("Source repository does not contain the reviewed upstream commit.")
    # Export committed bytes, not a mutable checkout or LFS-smudged files.
    data = run_git(source, "archive", "--format=tar", SOURCE_SHA, "src", "azureml", "LICENSE", "NOTICE.txt")
    output.mkdir()
    with tarfile.open(fileobj=io.BytesIO(data)) as archive:
        for entry in archive:
            if entry.isdir():
                continue
            if not code_path(entry.name):
                continue
            if not entry.isfile() or entry.size > 64 * 1024 * 1024:
                raise ValueError(f"Unexpected non-code archive entry: {entry.name}")
            target = output.joinpath(*entry.name.split("/"))
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(entry) as stream, target.open("xb") as destination:
                shutil.copyfileobj(stream, destination)
            with target.open("rb") as check:
                if check.read(80).startswith(b"version https://git-lfs.github.com/spec/v1"):
                    raise ValueError(f"Selected source is an unresolved LFS object: {entry.name}")
    required = HELPERS | {"LICENSE", "NOTICE.txt", "src/main.py", "src/folder_paths.py"}
    if any(not (output / name).is_file() for name in required):
        raise ValueError("Pinned source lacks required runner, ComfyUI or attribution files.")
    validate_import_closure(output)


def validate_import_closure(code: Path):
    modules = {Path(name).stem for name in HELPERS if name.endswith(".py")}
    for name in HELPERS:
        if not name.endswith(".py"):
            continue
        tree = ast.parse((code / name).read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            if isinstance(node, ast.ImportFrom):
                names = {node.module.split(".")[0]} if node.module else {alias.name for alias in node.names}
                if node.level and not names.issubset(modules):
                    raise ValueError("Runner imported a helper outside the reviewed closure.")
                if names & {"azure", "credential", "defaults", "cost_guard", "config", "web_submit", "register_and_submit"}:
                    raise ValueError("Operator/submission dependency in the runtime import closure.")
            elif isinstance(node, ast.Import) and any(alias.name.split(".")[0] in
                    {"azure", "credential", "defaults", "cost_guard", "config", "web_submit", "register_and_submit"} for alias in node.names):
                raise ValueError("Operator/submission dependency in the runtime import closure.")


def stage_workflows(source: Path, target: Path):
    export = json.loads((source / "index.json").read_text(encoding="utf-8"))
    if not isinstance(export, dict) or not export:
        raise ValueError("Expected an index of workflows exported by the .NET renderer.")
    target.mkdir()
    model_names, required = set(), set()
    fields = {"UNETLoader": ("unet_name",), "CLIPLoader": ("clip_name",), "VAELoader": ("vae_name",),
              "LoraLoaderModelOnly": ("lora_name",)}
    for mode, name in export.items():
        if not re.fullmatch(r"[a-z][a-z0-9_-]*", mode) or name != mode + ".json":
            raise ValueError("Invalid exported mode filename.")
        payload = json.loads((source / name).read_text(encoding="utf-8"))
        if not payload or any(not isinstance(node, dict) or not isinstance(node.get("class_type"), str) for node in payload.values()):
            raise ValueError("Expected a rendered ComfyUI API workflow.")
        if "{{" in json.dumps(payload):
            raise ValueError("Workflow has unrendered placeholders.")
        for node in payload.values():
            required.add(node["class_type"])
            for field in fields.get(node["class_type"], ()):
                model_names.add(node["inputs"][field])
        shutil.copyfile(source / name, target / name)
    write_json(target / "index.json", export)
    return sorted(required), sorted(model_names)


def prepare(source, output, workflows):
    if output.exists():
        raise ValueError("Use a new output directory; never merge a build with stale files.")
    output.mkdir(parents=True)
    stage_source(source, output / "code")
    required, model_names = stage_workflows(workflows, output / "workflows")
    manifest = json.loads((HERE / "wan-pinned-manifest.json").read_text())
    if not set(model_names).issubset({entry["filename"] for entry in manifest}):
        raise ValueError("A served WAN mode needs a model outside the pinned manifest.")
    context = output / "image"
    context.mkdir()
    inputs_root = output / "build-inputs"
    inputs_root.mkdir()
    for name in BUILD_INPUTS:
        source_file = HERE / name
        if source_file.is_symlink():
            raise ValueError("Build inputs must be regular reviewed files.")
        # Git checkouts use different host line endings; the image and provenance use canonical LF bytes.
        (inputs_root / name).write_bytes(source_file.read_text(encoding="utf-8").encode("utf-8"))
    for name in ("Dockerfile", "requirements-runtime.lock", "probe.py"):
        shutil.copyfile(inputs_root / name, context / name)
    for line in (context / "requirements-runtime.lock").read_text().replace("\\\n", "").splitlines():
        if not re.fullmatch(r"[a-zA-Z0-9_.-]+==[a-zA-Z0-9.+_-]+(?: ; [^\\]+?)?(?:\s+--hash=sha256:[a-f0-9]{64})+", line):
            raise ValueError("Runtime lock contains an unpinned dependency, URL or installer option.")
    records = bundle(output / "code", output / "code.tar")
    inputs = files(inputs_root)
    # Manifests carry a reviewed hash of every build input, never local customer paths.
    write_json(output / "stage.json", {
        "sourceSha": SOURCE_SHA, "baseImage": PROFILES["wan"]["baseImage"],
        "dependencyLockSha256": sha256(context / "requirements-runtime.lock"),
        "code": records, "codeArchiveSha256": sha256(output / "code.tar"),
        "workflows": files(output / "workflows"), "imageContext": files(context),
        "requiredNodes": required, "buildInputs": inputs,
    })


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Git repository containing the reviewed upstream commit; mutable checkout bytes are never used.")
    parser.add_argument("--output", type=Path, required=True, help="New build directory.")
    parser.add_argument("--workflows", type=Path, required=True, help="Directory exported by the .NET WAN workflow check.")
    args = parser.parse_args()
    from telemetry import configure_telemetry
    configure_telemetry("wan-gpu-build")
    prepare(args.source.resolve(), args.output.resolve(), args.workflows.resolve())
    print("Staged pinned WAN source and image context; no image, weight or Azure operation was performed.")
