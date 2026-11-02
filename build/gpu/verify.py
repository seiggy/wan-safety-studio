"""CPU-probe an already-built digest-pinned image and seal the portable manifest. Never pushes/submits."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
from opentelemetry import trace
from models import load_wan_manifest

from artifacts import artifact_id, process_environment, sha256, verify_files, write_json
from containers import Containers


def probe_spec(image: str, stage: Path):
    """The offline CPU probe: no network, no pull, read-only staged inputs, and nothing written to the image root."""
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9./:_-]*@sha256:[a-f0-9]{64}", image):
        raise ValueError("CPU probe needs a previously built image addressed by its registry manifest digest, not a mutable tag or image-config ID.")
    if any("," in str(path.resolve()) for path in (stage,)):
        raise ValueError("Bind-mount paths cannot contain a comma.")
    return {
        "entrypoint": "python",
        "env": ("CUDA_VISIBLE_DEVICES=", "NVIDIA_VISIBLE_DEVICES=void", "PYTHONDONTWRITEBYTECODE=1", "TMPDIR=/check/temp",
                "HOME=/check", "XDG_CACHE_HOME=/check/cache"),
        "tmpfs": ("/check:rw,mode=1777", "/tmp:rw,mode=1777"),
        "mounts": ((stage / "code", "/code"), (stage / "workflows", "/workflows"), (stage / "image", "/validation")),
        "workdir": "/code/src",
        "arguments": ("-c",
                      "import hashlib,pathlib,runpy; "
                      "assert hashlib.sha256(pathlib.Path('/opt/wan/requirements-runtime.lock').read_bytes()).digest() == "
                      "hashlib.sha256(pathlib.Path('/validation/requirements-runtime.lock').read_bytes()).digest(), 'Runtime lock changed'; "
                      "runpy.run_path('/validation/probe.py',run_name='__main__')"),
    }


def run_tool(*args, input=None):
    stdin = {"input": input} if input is not None else {"stdin": subprocess.DEVNULL}
    try:
        return subprocess.run([str(arg) for arg in args], check=True, capture_output=True, text=True,
                              env=process_environment(), **stdin).stdout
    except subprocess.CalledProcessError as error:
        print(error.stdout or "", error.stderr or "", sep="\n", file=sys.stderr)
        raise


def verify(stage: Path, assets: Path, image: str):
    if (stage / "manifest.json").exists():
        raise ValueError("Manifest already exists; use a new build, not an overwrite.")
    selected = json.loads((stage / "stage.json").read_text())
    model_assets = json.loads((assets / "models.json").read_text())
    expected_models = {
        f"{entry['folder_name']}/{entry['filename']}": (entry["size"], entry["sha256"], entry["url"])
        for entry in load_wan_manifest(stage / "build-inputs" / "wan-pinned-manifest.json")
    }
    found_models = {item["path"]: (item["bytes"], item["sha256"], item["source"]) for item in model_assets["models"]}
    if found_models != expected_models or len(found_models) != len(model_assets["models"]):
        raise ValueError("Model snapshot does not exactly match the reviewed WAN manifest.")
    repositories = {item["repository"] for item in model_assets["models"]}
    required_licenses = {repository.replace("/", "--") + suffix for repository in repositories for suffix in ("--README.md", ".json")}
    if not required_licenses.issubset({item["path"] for item in model_assets["licenses"]}):
        raise ValueError("Model snapshot is missing pinned licence/provenance records.")
    for root, records in ((stage / "build-inputs", selected["buildInputs"]),
                          (stage / "code", selected["code"]), (stage / "workflows", selected["workflows"]),
                          (stage / "image", selected["imageContext"]), (assets / "licenses", model_assets["licenses"])):
        verify_files(root, records)
    model_files = [{key: item[key] for key in ("path", "bytes", "sha256")} for item in model_assets["models"]]
    verify_files(assets / "models", model_files)
    if sha256(stage / "code.tar") != selected["codeArchiveSha256"]:
        raise ValueError("Staged deterministic code archive changed.")
    # The image must include the exact reviewed lock and probe script, not just provide a plausible name.
    spec = probe_spec(image, stage)
    cli = Containers(run_tool)
    with trace.get_tracer("wan.gpu.build").start_as_current_span("runtime.image.inspect", record_exception=False, set_status_on_exception=False):
        metadata = cli.require_platform(image)
    marker = metadata.get("Config", {}).get("Labels", {}).get("studio.runtime-lock-sha256")
    if marker != selected["dependencyLockSha256"]:
        raise ValueError("Image lock label differs from the reviewed staged dependency lock.")
    with trace.get_tracer("wan.gpu.build").start_as_current_span("runtime.cpu-probe", record_exception=False, set_status_on_exception=False):
        cli.run_offline(image, **spec)
    for root, records in ((stage / "build-inputs", selected["buildInputs"]),
                          (stage / "code", selected["code"]), (stage / "workflows", selected["workflows"]),
                          (stage / "image", selected["imageContext"]), (assets / "models", model_files),
                          (assets / "licenses", model_assets["licenses"])):
        verify_files(root, records)
    if sha256(stage / "code.tar") != selected["codeArchiveSha256"]:
        raise ValueError("Staged archive changed during CPU verification.")
    manifest = {
        "schemaVersion": 1, "artifactId": "", "profile": "wan", "sourceSha": selected["sourceSha"],
        "baseImage": selected["baseImage"], "dependencyLockSha256": selected["dependencyLockSha256"],
        "imageDigest": image.split("@")[1], "code": selected["code"],
        "models": model_assets["models"], "licenses": model_assets["licenses"],
        "buildInputs": selected["buildInputs"],
    }
    manifest["artifactId"] = artifact_id(manifest)
    write_json(stage / "manifest.json", manifest)
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--image", required=True)
    args = parser.parse_args()
    from telemetry import configure_telemetry
    configure_telemetry("wan-gpu-build")
    result = verify(args.stage.resolve(), args.assets.resolve(), args.image)
    print(f"CPU-verified WAN artifact: {result['artifactId']}. No Azure publication or GPU execution.")
