"""Container operations through one selected engine: the WSL container CLI (wslc), Podman or Docker.

STUDIO_CONTAINER_ENGINE picks the engine (`wslc`, `podman` or `docker`); unset means wslc, which the private GitHub runners
use. STUDIO_WSLC, STUDIO_PODMAN or STUDIO_DOCKER may name the executable. Every engine must give the same guarantees, and a
failed check stops the pipeline:
- linux/amd64 only: every built or pulled image is inspected for Os/Architecture (and built with --platform where the engine
  has it).
- read-only root during offline checks: the container's writable layer must still be empty afterwards (SizeRw == 0); only
  the explicit tmpfs mounts may change. Docker and Podman also get --read-only; wslc has no such flag, and the SizeRw check
  is stricter than it anyway.
- one Docker v2 manifest per image, so no attestation index or OCI media types: Docker builds with buildx
  (--provenance=false, --sbom=false, oci-mediatypes=false) and pushes from the build; Podman builds --format docker and
  pushes --format v2s2; the caller confirms the manifest in the registry.
- built digest bound to the built image: wslc reads the pushed RepoDigests of the image named by --iidfile; Podman reports it
  through push --digestfile; Docker through buildx --metadata-file. The caller then pulls that digest and checks it is the
  image that was built.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import uuid

PLATFORM = {"Os": "linux", "Architecture": "amd64"}
DOCKER_V2_MANIFEST = "application/vnd.docker.distribution.manifest.v2+json"
TOKEN_USER = "00000000-0000-0000-0000-000000000000"
ENGINES = ("wslc", "podman", "docker")
DIGEST = re.compile(r"sha256:[a-f0-9]{64}")
# Engine selection, passed to Python child steps (such as verify.py) so they use the same engine.
SELECTION_ENV = ("STUDIO_CONTAINER_ENGINE", "STUDIO_WSLC", "STUDIO_PODMAN", "STUDIO_DOCKER")
# What each native engine needs from the environment to find its daemon or runtime, builder, storage and registry auth file.
ENGINE_ENV = {
    # On Windows the Docker CLI finds its system plugins (buildx) and credential helpers under ProgramFiles and ProgramData.
    "docker": ("DOCKER_CONFIG", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "BUILDX_BUILDER",
               "BUILDX_CONFIG", "ProgramFiles", "ProgramData"),
    "podman": ("USER", "LOGNAME", "XDG_RUNTIME_DIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "DBUS_SESSION_BUS_ADDRESS",
               "CONTAINER_HOST", "CONTAINER_CONNECTION", "CONTAINERS_CONF", "CONTAINERS_REGISTRIES_CONF",
               "CONTAINERS_STORAGE_CONF", "REGISTRY_AUTH_FILE"),
}


def engine_environment(program):
    """The extra variables the named program may see: an engine's own settings, or nothing for any other tool."""
    # Case-insensitive: Windows reports environment names upper-cased (PROGRAMFILES).
    names = {name.upper() for name in ENGINE_ENV.get(Path(str(program)).stem.lower(), ())}
    return {key: value for key, value in os.environ.items() if key.upper() in names}


def engine_name():
    value = os.environ.get("STUDIO_CONTAINER_ENGINE", "").strip().lower() or "wslc"
    _require(value in ENGINES, f"STUDIO_CONTAINER_ENGINE must be one of {', '.join(ENGINES)}; got {value}.")
    return value


def executable(engine=None):
    engine = engine or engine_name()
    if engine == "wslc":
        candidates = (os.environ.get("STUDIO_WSLC"), shutil.which("wslc.exe"), shutil.which("wslc"),
                      "/mnt/c/Program Files/WSL/wslc.exe", r"C:\Program Files\WSL\wslc.exe")
        missing = "The WSL container CLI (wslc) is required; install WSL with wslc or set STUDIO_WSLC to its path."
    else:
        override = os.environ.get("STUDIO_" + engine.upper())
        candidates = (override, shutil.which(engine + ".exe"), shutil.which(engine))
        missing = f"{engine} was not found on PATH; install it or set STUDIO_{engine.upper()} to its path."
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return str(candidate)
    raise ValueError(missing)


def kind_of(cli):
    stem = Path(str(cli)).stem.lower()
    return stem if stem in ENGINES else "wslc"


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

    def __init__(self, run, cli=None, engine=None):
        self.run = run
        self.engine = engine or (kind_of(cli) if cli else engine_name())
        _require(self.engine in ENGINES, f"Unsupported container engine {self.engine}.")
        self.cli = cli or executable(self.engine)

    def __call__(self, *args, input=None):
        return self.run(self.cli, *args, input=input)

    def path(self, path):
        """A path the engine can open: Docker and Podman run natively; wslc.exe needs its Windows form."""
        return host_path(path) if self.engine == "wslc" else str(Path(path).resolve())

    def preflight(self):
        """Fail before any work when the engine lacks a feature the build needs. Returns the engine's version text."""
        if self.engine == "docker":
            try:
                version = self("buildx", "version")
            except (RuntimeError, OSError, subprocess.CalledProcessError) as error:
                raise ValueError("Docker needs the buildx plugin (for --platform, --secret, --metadata-file and a pushed "
                                 "Docker v2 manifest). Install docker-buildx, or use --container-engine podman or wslc.") from error
            return version.strip()
        if self.engine == "podman":
            build, push = self("build", "--help"), self("push", "--help")
            for flag, text in (("--secret", build), ("--iidfile", build), ("--format", build), ("--digestfile", push)):
                _require(flag in text, f"This Podman lacks {flag}, which the build needs. Upgrade Podman, or use "
                                       f"--container-engine docker or wslc.")
            return self("--version").strip()
        return self("--version").strip()

    def inspect(self, kind, reference, size=False):
        value = json.loads(self("inspect", "--type", kind, *(["--size"] if size else []), reference))
        _require(isinstance(value, list) and len(value) == 1, f"{self.engine} inspect returned no single {kind}.")
        item = value[0]
        if kind == "image" and re.fullmatch(r"[a-f0-9]{64}", str(item.get("Id", ""))):
            # Podman reports the image ID without the algorithm prefix that --iidfile, Docker and wslc use.
            item["Id"] = "sha256:" + item["Id"]
        return item

    def require_platform(self, reference):
        image = self.inspect("image", reference)
        actual = {key: image.get(key) for key in PLATFORM}
        _require(actual == PLATFORM, f"Image {reference} is {actual}, not linux/amd64.")
        return image

    def login(self, host, token):
        self("login", host, "--username", TOKEN_USER, "--password-stdin", input=token)
        # wslc keeps registry credentials in the Windows credential store, Docker and Podman in their own config file: none
        # is the job folder for certain, so record the host and the cleanup step logs out even if this process dies.
        record = os.environ.get("STUDIO_REGISTRY_LOG")
        if record:
            with open(record, "a", encoding="utf-8") as stream:
                stream.write(host + "\n")

    def pull(self, image):
        self("pull", "--quiet", *(["--platform", "linux/amd64"] if self.engine != "wslc" else []), image)
        return self.require_platform(image)

    def is_built_image(self, image, image_id):
        """Pull image (repository@digest) and confirm it is the image the build produced. Docker with the containerd image
        store names an image by its manifest digest rather than its config digest, so either identity is accepted there."""
        pulled = self.pull(image)["Id"]
        if self.engine == "docker":
            return pulled in (image_id, image.rsplit("@", 1)[1])
        return pulled == image_id

    def tag(self, source, target):
        self("tag", source, target)

    def push(self, tag, digestfile=None):
        if self.engine == "podman":
            self("push", "--quiet", "--format", "v2s2", *(["--digestfile", str(digestfile)] if digestfile else []), tag)
        else:
            self("push", "--quiet", tag)

    def _secrets(self, secrets):
        args = []
        for name, kind, value in secrets:
            _require(kind in ("src", "env"), "Unsupported build secret source.")
            args += ["--secret", f"id={name},{kind}={self.path(value) if kind == 'src' else value}"]
        return args

    def build_and_push(self, tag, dockerfile, context, build_args=(), secrets=()):
        """Build, require linux/amd64, push, and return (image ID, manifest digest) for the pushed tag.
        `secrets` are (id, "src", path) or (id, "env", variable); an env secret also needs the variable in WSLENV for wslc."""
        if self.engine == "docker":
            return self._docker_build_and_push(tag, dockerfile, context, build_args, secrets)
        args = ["build"]
        args += ["--format", "docker", "--platform", "linux/amd64"] if self.engine == "podman" else ["--progress", "plain"]
        args += ["--tag", tag, "--file", self.path(dockerfile)]
        for value in build_args:
            args += ["--build-arg", value]
        args += self._secrets(secrets)
        with tempfile.TemporaryDirectory() as folder:
            iid = Path(folder) / "image-id"
            self(*args, "--iidfile", self.path(iid), self.path(context))
            image_id = iid.read_text(encoding="utf-8").strip()
        if re.fullmatch(r"[a-f0-9]{64}", image_id):
            image_id = "sha256:" + image_id
        _require(image_id.startswith("sha256:"), f"{self.engine} build wrote no image ID.")
        self.require_platform(image_id)
        if self.engine == "podman":
            with tempfile.TemporaryDirectory() as folder:
                file = Path(folder) / "digest"
                self.push(tag, digestfile=file)
                digest = file.read_text(encoding="utf-8").strip() if file.is_file() else ""
            _require(DIGEST.fullmatch(digest), "podman push reported no registry digest.")
            return image_id, digest
        self.push(tag)
        repository = tag.rsplit(":", 1)[0]
        digests = {item.split("@", 1)[1] for item in self.inspect("image", image_id).get("RepoDigests") or []
                   if item.split("@", 1)[0] == repository}
        _require(len(digests) == 1, "The pushed image has no single registry digest for its repository.")
        return image_id, digests.pop()

    def _docker_build_and_push(self, tag, dockerfile, context, build_args, secrets):
        """buildx pushes straight from the build (no local image), so the pushed digest and the image's config digest
        come from its --metadata-file."""
        args = ["buildx", "build", "--progress", "plain", "--platform", "linux/amd64", "--provenance=false", "--sbom=false",
                "--output", "type=image,push=true,oci-mediatypes=false", "--tag", tag, "--file", self.path(dockerfile)]
        for value in build_args:
            args += ["--build-arg", value]
        args += self._secrets(secrets)
        with tempfile.TemporaryDirectory() as folder:
            metadata = Path(folder) / "metadata.json"
            self(*args, "--metadata-file", str(metadata), self.path(context))
            values = json.loads(metadata.read_text(encoding="utf-8")) if metadata.is_file() else {}
        digest, image_id = values.get("containerimage.digest", ""), values.get("containerimage.config.digest", "")
        _require(DIGEST.fullmatch(str(digest)) and DIGEST.fullmatch(str(image_id)),
                 "docker buildx wrote no pushed manifest digest and image config digest to its metadata file.")
        return image_id, digest

    def run_offline(self, image, entrypoint, arguments=(), env=(), tmpfs=(), mounts=(), workdir=None):
        """Run a local image with no network, no pull and nothing written to its root filesystem."""
        name = "studio-check-" + uuid.uuid4().hex[:12]
        args = ["run", "--name", name, "--pull", "never", "--network", "none"]
        if self.engine != "wslc":
            args.append("--read-only")
        for value in tmpfs:
            args += ["--tmpfs", value]
        for value in env:
            args += ["--env", value]
        for source, target in mounts:
            location = self.path(source)
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
            # "/." copies the directory's contents. wslc copies a directory only into a directory target given with a
            # trailing separator; Docker and Podman create the missing destination folder themselves.
            target = host_path(destination) + "\\" if self.engine == "wslc" else self.path(destination)
            self("cp", f"{name}:{source.rstrip('/')}/.", target)
        finally:
            self("rm", "--force", name)
