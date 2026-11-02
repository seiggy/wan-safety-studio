"""Container operations through the WSL container CLI (wslc). Docker is not used.

wslc has no --platform, --read-only, --metadata-file, --provenance/--sbom or buildx push output. Each property those flags
gave is checked instead, and a failed check stops the pipeline:
- linux/amd64 only: every built or pulled image is inspected for Os/Architecture.
- read-only root during offline checks: the container's writable layer must still be empty afterwards (SizeRw == 0);
  only the explicit tmpfs mounts may change. This is stricter than --read-only: any root write fails the check.
- built digest: taken from the local image's RepoDigests after the push, bound to the image ID from --iidfile, then
  confirmed against the registry by the caller (single Docker v2 manifest, so no attestation index or OCI media types).
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import uuid

PLATFORM = {"Os": "linux", "Architecture": "amd64"}
DOCKER_V2_MANIFEST = "application/vnd.docker.distribution.manifest.v2+json"
TOKEN_USER = "00000000-0000-0000-0000-000000000000"


def executable():
    for candidate in (os.environ.get("STUDIO_WSLC"), shutil.which("wslc.exe"), shutil.which("wslc"),
                      "/mnt/c/Program Files/WSL/wslc.exe", r"C:\Program Files\WSL\wslc.exe"):
        if candidate and Path(candidate).is_file():
            return str(candidate)
    raise ValueError("The WSL container CLI (wslc) is required; install WSL with wslc or set STUDIO_WSLC to its path.")


def host_path(path):
    """wslc.exe is a Windows program: a Linux path on a WSL runner is passed in its Windows (\\\\wsl.localhost) form."""
    resolved = Path(path).resolve()
    if os.name == "nt":
        return str(resolved)
    return subprocess.run(["wslpath", "-w", str(resolved)], check=True, capture_output=True, text=True).stdout.strip()


def _require(condition, message):
    if not condition:
        raise ValueError(message)


class Containers:
    """`run(*args, input=None) -> stdout` executes one command and raises on a non-zero exit."""

    def __init__(self, run, cli=None):
        self.run, self.cli = run, cli or executable()

    def __call__(self, *args, input=None):
        return self.run(self.cli, *args, input=input)

    def inspect(self, kind, reference, size=False):
        value = json.loads(self("inspect", "--type", kind, *(["--size"] if size else []), reference))
        _require(isinstance(value, list) and len(value) == 1, f"wslc inspect returned no single {kind}.")
        return value[0]

    def require_platform(self, reference):
        image = self.inspect("image", reference)
        actual = {key: image.get(key) for key in PLATFORM}
        _require(actual == PLATFORM, f"Image {reference} is {actual}, not linux/amd64.")
        return image

    def login(self, host, token):
        self("login", host, "--username", TOKEN_USER, "--password-stdin", input=token)
        # wslc keeps registry credentials in the Windows credential store, not a job folder: record the host so the
        # workflow's cleanup step can log out even if this process dies.
        record = os.environ.get("STUDIO_REGISTRY_LOG")
        if record:
            with open(record, "a", encoding="utf-8") as stream:
                stream.write(host + "\n")

    def pull(self, image):
        self("pull", "--quiet", image)
        return self.require_platform(image)

    def tag(self, source, target):
        self("tag", source, target)

    def push(self, tag):
        self("push", "--quiet", tag)

    def build_and_push(self, tag, dockerfile, context, build_args=(), secrets=()):
        """Build, require linux/amd64, push, and return (image ID, manifest digest) for the pushed tag.
        `secrets` are (id, "src", path) or (id, "env", variable); an env secret also needs the variable in WSLENV."""
        args = ["build", "--progress", "plain", "--tag", tag, "--file", host_path(dockerfile)]
        for value in build_args:
            args += ["--build-arg", value]
        for name, kind, value in secrets:
            _require(kind in ("src", "env"), "Unsupported build secret source.")
            args += ["--secret", f"id={name},{kind}={host_path(value) if kind == 'src' else value}"]
        with tempfile.TemporaryDirectory() as folder:
            iid = Path(folder) / "image-id"
            self(*args, "--iidfile", host_path(iid), host_path(context))
            image_id = iid.read_text(encoding="utf-8").strip()
        _require(image_id.startswith("sha256:"), "wslc build wrote no image ID.")
        self.require_platform(image_id)
        self.push(tag)
        repository = tag.rsplit(":", 1)[0]
        digests = {item.split("@", 1)[1] for item in self.inspect("image", image_id).get("RepoDigests") or []
                   if item.split("@", 1)[0] == repository}
        _require(len(digests) == 1, "The pushed image has no single registry digest for its repository.")
        return image_id, digests.pop()

    def run_offline(self, image, entrypoint, arguments=(), env=(), tmpfs=(), mounts=(), workdir=None):
        """Run a local image with no network, no pull and nothing written to its root filesystem."""
        name = "studio-check-" + uuid.uuid4().hex[:12]
        args = ["run", "--name", name, "--pull", "never", "--network", "none"]
        for value in tmpfs:
            args += ["--tmpfs", value]
        for value in env:
            args += ["--env", value]
        for source, target in mounts:
            location = host_path(source)
            _require("," not in location, "Bind-mount paths cannot contain a comma.")
            args += ["--mount", f"type=bind,source={location},target={target},readonly"]
        if workdir:
            args += ["--workdir", workdir]
        try:
            output = self(*args, "--entrypoint", entrypoint, image, *arguments)
            written = self.inspect("container", name, size=True).get("SizeRw")
            _require(written == 0, f"The offline check wrote {written} bytes to the image root filesystem; only tmpfs is writable.")
            return output
        finally:
            self("rm", "--force", name)

    def copy_out(self, image, source, destination):
        """Copy a directory's contents out of a local image into a new folder (the image is never started)."""
        _require(not Path(destination).exists(), "Copy destination already exists.")
        name = "studio-copy-" + uuid.uuid4().hex[:12]
        self("create", "--name", name, "--pull", "never", image)
        try:
            # wslc copies a directory only into a directory target given with a trailing separator; "/." copies its contents.
            self("cp", f"{name}:{source.rstrip('/')}/.", host_path(destination) + "\\")
        finally:
            self("rm", "--force", name)
