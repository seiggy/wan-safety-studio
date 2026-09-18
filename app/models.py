"""Resolve upstream workflow requirements and download verified, revision-pinned assets on CPU."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import shutil
import urllib.request
from urllib.parse import quote

from opentelemetry import trace

TRACER = trace.get_tracer("wan-safety-studio.models")
WAN_REVISIONS = {
    "Comfy-Org/Wan_2.2_ComfyUI_Repackaged": "c4f60d30c55a624e35427060fdd217579a6c1d77",
    "Comfy-Org/Wan_2.1_ComfyUI_repackaged": "617a7633e636506f850e043bc4605f290a466a8e",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def public_json(url: str):
    with TRACER.start_as_current_span("huggingface.metadata", record_exception=False, set_status_on_exception=False):
        with urllib.request.urlopen(url, timeout=90) as response:
            return json.load(response)


def load_wan_manifest(path: Path | None = None):
    entries = json.loads((path or Path(__file__).with_name("wan-pinned-manifest.json")).read_text(encoding="utf-8-sig"))
    if len(entries) != 4 or len({(e["folder_name"], e["filename"]) for e in entries}) != 4:
        raise ValueError("WAN requires exactly four unique pinned models.")
    for entry in entries:
        match = re.fullmatch(r"https://huggingface\.co/([^/]+/[^/]+)/resolve/([a-f0-9]{40})/(.+)", entry["url"])
        if not match:
            raise ValueError("WAN model URL must use an immutable Hugging Face revision.")
        repo, revision, remote_path = match.groups()
        if WAN_REVISIONS.get(repo) != revision or remote_path != f"split_files/{entry['folder_name']}/{entry['filename']}":
            raise ValueError("WAN model source differs from the approved repository/revision/path.")
        if type(entry["size"]) is not int or entry["size"] <= 0 or not re.fullmatch(r"[a-f0-9]{64}", entry["sha256"]):
            raise ValueError("WAN model requires an exact positive size and SHA256.")
    return entries


def stage_local_model(source: Path, destination: Path, expected_hash: str, size: int):
    if not source.is_file() or source.stat().st_size != size or sha256(source) != expected_hash:
        raise ValueError(f"Local model is missing or fails pinned size/SHA256 validation: {source.name}. No download fallback.")
    partial = destination.with_suffix(destination.suffix + ".partial")
    try:
        partial.unlink(missing_ok=True)
        try:
            partial.hardlink_to(source)
        except OSError:
            shutil.copy2(source, partial)
        if partial.stat().st_size != size or sha256(partial) != expected_hash:
            raise ValueError("Local model changed during staging; preparation rejected.")
        partial.replace(destination)
    finally:
        partial.unlink(missing_ok=True)


def workflow_sources(root: Path, profile_key: str):
    from azureml import workflow_utils
    from azureml.workflow_profiles import get_profile

    profile = get_profile(profile_key)
    workflow = workflow_utils.load_workflow_payload(root / profile.workflow)
    required = workflow_utils.collect_required_model_references(workflow)
    if profile_key == "minimax_h3":
        entries = json.loads((root / "azureml" / "minimax_h3_model_manifest.json").read_text())
        # Upstream's generic collector does not understand all H3 loader types.
        sources = {(e["folder_name"], e["filename"]): e["url"] for e in entries}
        if len(sources) != 4:
            raise ValueError("The pinned H3 manifest must contain exactly four models.")
    elif profile_key in ("ltx", "ltx_i2v"):
        from azureml.bootstrap_ltx_and_run import LTX_MODEL_URLS
        sources = {(r.folder_name, r.filename): url for r, url in LTX_MODEL_URLS.items()}
        sources = {(r.folder_name, r.filename): sources[(r.folder_name, r.filename)] for r in required}
    else:
        sources = {(e["folder_name"], e["filename"]): e["url"] for e in load_wan_manifest()}
    if not required or not {(r.folder_name, r.filename) for r in required}.issubset(sources):
        raise ValueError("Workflow contains unrecognized/missing model download origins; no GPU enabled.")
    return profile, sources


def prepare_models(root: Path, profile_key: str, target: Path, *, local_models: Path | None = None):
    if local_models is not None:
        local_models = local_models.resolve(strict=True)
        if not local_models.is_dir():
            raise ValueError("LocalModelsPath must be a directory containing the workflow model subfolders.")
    profile, sources = workflow_sources(root, profile_key)
    wan_entries = load_wan_manifest() if profile_key == "wan" else []
    repos = {}
    records = []
    for (folder, name), url in sorted(sources.items()):
        match = re.fullmatch(r"https://huggingface\.co/([^/]+/[^/]+)/resolve/(?:main|[a-f0-9]{40})/(.+)", url)
        if not match:
            raise ValueError("Only the pinned upstream Hugging Face origins are accepted.")
        repo, remote_path = match.groups()
        if repo not in repos:
            revision = (
                WAN_REVISIONS[repo] if profile_key == "wan"
                else public_json(f"https://huggingface.co/api/models/{repo}")["sha"]
            )
            if not re.fullmatch(r"[a-f0-9]{40}", revision):
                raise ValueError("Hugging Face did not provide an immutable revision.")
            if profile_key == "wan":
                metadata = {"cardData": {"license": "apache-2.0"}, "siblings": [
                    {"rfilename": f"split_files/{e['folder_name']}/{e['filename']}",
                     "lfs": {"sha256": e["sha256"], "size": e["size"]}}
                    for e in wan_entries if e["url"].startswith(f"https://huggingface.co/{repo}/")
                ] + [{"rfilename": "README.md"}]}
            else:
                metadata = public_json(f"https://huggingface.co/api/models/{repo}/revision/{revision}?blobs=true")
            repos[repo] = (revision, metadata)
        revision, metadata = repos[repo]
        entries = [item for item in metadata["siblings"] if item["rfilename"] == remote_path]
        if len(entries) != 1 or not entries[0].get("lfs"):
            raise ValueError(f"Missing authoritative LFS size/SHA256 for {folder}/{name}.")
        lfs = entries[0]["lfs"]
        expected_hash, size = lfs["sha256"], lfs["size"]
        if not re.fullmatch(r"[a-f0-9]{64}", expected_hash) or not isinstance(size, int) or size <= 0:
            raise ValueError("Malformed upstream model checksum/size.")
        destination = target / folder / name
        if not destination.resolve().is_relative_to(target.resolve()):
            raise ValueError("Model path escaped its staging directory.")
        destination.parent.mkdir(parents=True, exist_ok=True)
        immutable_url = f"https://huggingface.co/{repo}/resolve/{revision}/{quote(remote_path, safe='/')}"
        if not (destination.is_file() and destination.stat().st_size == size and sha256(destination) == expected_hash):
            if local_models is not None:
                source = local_models / folder / name
                if not source.resolve().is_relative_to(local_models):
                    raise ValueError("Local model path escaped the selected cache.")
                print(f"CPU cache reuse: {folder}/{name}; checking pinned size/SHA256.", flush=True)
                stage_local_model(source, destination, expected_hash, size)
            else:
                download_model(immutable_url, destination, expected_hash, size, folder, name)
        records.append({
            "path": f"{folder}/{name}", "bytes": size, "sha256": expected_hash,
            "source": immutable_url, "repository": repo, "revision": revision,
            "license": (metadata.get("cardData") or {}).get("license", "See source model card"),
        })
    # Preserve each upstream model card/license next to the prepared manifest, not model weights in git.
    licenses = target.parent / "licenses"
    licenses.mkdir(parents=True, exist_ok=True)
    for repo, (revision, metadata) in repos.items():
        (licenses / (repo.replace("/", "--") + ".json")).write_text(
            json.dumps({"repository": repo, "revision": revision, "cardData": metadata.get("cardData", {})}, indent=2),
            encoding="utf-8",
        )
        for item in metadata["siblings"]:
            leaf = Path(item["rfilename"]).name
            if leaf.lower().startswith(("license", "copying")) or item["rfilename"] == "README.md":
                url = f"https://huggingface.co/{repo}/resolve/{revision}/{quote(item['rfilename'], safe='/')}"
                with TRACER.start_as_current_span("huggingface.license.download", record_exception=False, set_status_on_exception=False):
                    with urllib.request.urlopen(url, timeout=90) as response:
                        content = response.read(2 * 1024 * 1024 + 1)
                if len(content) > 2 * 1024 * 1024:
                    raise ValueError("Unexpected model license file size.")
                (licenses / (repo.replace("/", "--") + "--" + leaf)).write_bytes(content)
    return profile, records


def download_model(immutable_url: str, destination: Path, expected_hash: str, size: int, folder: str, name: str):
    print(f"CPU preparation: {folder}/{name} ({size / 1024**3:.2f} GiB). No compute enabled.", flush=True)
    partial = destination.with_suffix(destination.suffix + ".partial")
    try:
        with TRACER.start_as_current_span("huggingface.model.download", record_exception=False, set_status_on_exception=False):
            with urllib.request.urlopen(immutable_url, timeout=180) as response, partial.open("wb") as handle:
                for chunk in iter(lambda: response.read(8 * 1024 * 1024), b""):
                    handle.write(chunk)
        if partial.stat().st_size != size or sha256(partial) != expected_hash:
            raise ValueError("Model size/SHA256 mismatch; preparation rejected.")
        partial.replace(destination)
    finally:
        partial.unlink(missing_ok=True)
