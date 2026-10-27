"""Portable build records and deterministic code archives; no Azure dependency."""
from __future__ import annotations

import copy
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import tarfile


def process_environment():
    allowed = {"PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "HOME", "USERPROFILE",
               "APPDATA", "LOCALAPPDATA", "TMP", "TEMP", "TMPDIR", "DOCKER_HOST", "DOCKER_CONTEXT",
               "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "SSL_CERT_FILE", "SSL_CERT_DIR",
               "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE"}
    return {key: value for key, value in os.environ.items() if key in allowed}


def sha256(path: Path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, allow_nan=False, separators=(",", ":")).encode("utf-8")


def artifact_id(manifest):
    value = copy.deepcopy(manifest)
    value["artifactId"] = ""
    for section in ("code", "models", "licenses", "buildInputs"):
        value[section].sort(key=lambda file: file["path"].encode("utf-16-be"))
    return hashlib.sha256(canonical(value)).hexdigest()


def safe_path(name):
    denied = {".git", ".venv", "__pycache__", "node_modules", ".cache", ".pytest_cache", ".mypy_cache"}
    return (isinstance(name, str) and bool(name) and
            not any(ord(c) < 32 or c in "\\:?#%" for c in name) and
            all(part and part not in {".", ".."} and part.lower() not in denied and
                not part.lower().startswith(".env") for part in name.split("/")))


def files(root: Path):
    records = []
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Symlink in build output: {path.name}")
        if path.is_file():
            name = path.relative_to(root).as_posix()
            if not safe_path(name):
                raise ValueError(f"Invalid artifact path: {name}")
            records.append({"path": name, "bytes": path.stat().st_size, "sha256": sha256(path)})
    if not records:
        raise ValueError(f"Artifact directory is empty: {root.name}")
    return sorted(records, key=lambda item: item["path"].encode("utf-16-be"))


def verify_files(root, expected):
    if files(root) != sorted(expected, key=lambda item: item["path"].encode("utf-16-be")):
        raise ValueError(f"Build files changed or contain missing/unexpected files: {root.name}")


def write_json(path: Path, value):
    with path.with_suffix(path.suffix + ".pending").open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(value, ensure_ascii=True, allow_nan=False, indent=2) + "\n")
    path.with_suffix(path.suffix + ".pending").replace(path)


def bundle(root: Path, target: Path):
    records = files(root)
    with tarfile.open(target, "w", format=tarfile.PAX_FORMAT) as archive:
        for record in records:
            info = tarfile.TarInfo(record["path"])
            info.size = record["bytes"]
            info.mode = 0o644
            info.mtime = info.uid = info.gid = 0
            info.uname = info.gname = ""
            with (root / PurePosixPath(record["path"])).open("rb") as stream:
                archive.addfile(info, stream)
    return records
