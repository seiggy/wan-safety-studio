"""Build one independent image; GPU publication remains unready until the staged CPU probe succeeds."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import sys

from pipeline import Azure, ROOT, build_var, contracts, image_digest, immutable_bytes, json_bytes, load, require, run


# Executables each image must contain and run, checked offline with no network and a read-only root.
VERIFY = {
    "server": (("dotnet", "--info"), ("ffmpeg", "-version"), ("ffprobe", "-version")),
    "yolox": (("python", "-m", "vision.trainers.yolox", "--help"),),
}
TRAINER_FIELDS = ("name", "version", "module", "presets", "defaultPreset", "licenseCode", "licenseWeights")


def gpu_cache(name):
    """An optional runner cache path. Unset means the build fetches that input itself; a set value must name an existing folder."""
    value = os.environ.get(name, "").strip()
    if value:
        require(Path(value).is_dir(), f"{name} does not name an existing folder on this runner.")
    return value or None


def fetch_upstream(destination):
    """The pinned upstream commit, fetched by exact SHA into a fresh folder. prepare.py then verifies the commit identity."""
    upstream = load(ROOT / "build/gpu/profiles.json")["upstream"]
    require(re.fullmatch(r"https://[a-z0-9.-]+/[A-Za-z0-9._/-]+\.git", upstream["repository"]) and
            re.fullmatch(r"[a-f0-9]{40}", upstream["sourceSha"]), "build/gpu/profiles.json has no valid pinned upstream.")
    destination.mkdir(parents=True)
    run("git", "-C", destination, "init", "--quiet")
    run("git", "-C", destination, "fetch", "--quiet", "--depth", "1", upstream["repository"], upstream["sourceSha"])
    return str(destination)


def build(component, settings, work):
    receipt = contracts(settings)["foundation"]
    azure = Azure(receipt["scope"])
    require(load(settings).get("Pipeline", {}).get("PublisherPrincipalId") == azure.actor_id,
            "Build must use the explicit Pipeline.PublisherPrincipalId.")
    control = receipt["regions"][receipt["controlRegion"]]
    host = azure.registry(control)
    commit = build_var("SOURCE_SHA")
    require(re.fullmatch(r"[a-f0-9]{40}", commit), "Build requires an exact source revision.")
    repository = {"server": "wan/server", "gpu": "wan/runtime", "yolox": "wan/yolox"}[component]
    tag = f"{host}/{repository}:unready-{build_var('RUN_ID')}-{build_var('RUN_ATTEMPT')}"
    metadata = work / "image.json"
    args = ["docker", "buildx", "build", "--platform", "linux/amd64", "--provenance=false", "--sbom=false",
            "--output", "type=image,push=true,oci-mediatypes=false", "--tag", tag, "--metadata-file", metadata]
    for secret, variable in (("npmrc", "STUDIO_NPMRC"), ("nugetconfig", "STUDIO_NUGET_CONFIG"), ("netrc", "STUDIO_NETRC")):
        if os.environ.get(variable):
            require(Path(os.environ[variable]).is_file(), "Configured build secret file is missing.")
            args += ["--secret", f"id={secret},src={os.environ[variable]}"]
    if component == "gpu":
        stage, assets = work / "stage", work / "assets"
        # Self-contained by default: the pinned commit and the pinned weights are fetched into this run's work folder and
        # verified (commit identity, size and SHA-256). The cache variables only let a runner reuse inputs it already holds.
        source = gpu_cache("STUDIO_UPSTREAM_SOURCE") or fetch_upstream(work / "upstream")
        models, licenses = gpu_cache("STUDIO_MODEL_CACHE"), gpu_cache("STUDIO_LICENSE_CACHE")
        fetch = [] if models and licenses else ["--download"]
        run(sys.executable, ROOT / "build/gpu/prepare.py", "--source", source,
            "--output", stage, "--workflows", ROOT / "contracts/fixtures/wan-workflows")
        run(sys.executable, ROOT / "build/gpu/models.py", "--output", assets,
            "--models", models or work / "model-cache", "--licenses", licenses or work / "license-cache", *fetch)
        selected = load(stage / "stage.json")
        approved_feed()
        args += ["--build-arg", "RUNTIME_LOCK_SHA256=" + selected["dependencyLockSha256"],
                 "--secret", "id=pip-index,env=PIP_INDEX_URL", "--file", stage / "image/Dockerfile", stage / "image"]
    elif component == "server":
        args += ["--file", ROOT / "src/VideoSafetyStudio.Server/Dockerfile", ROOT]
    else:
        require(os.environ.get("STUDIO_APPROVE_TRAINER_WEIGHTS") == "true",
                "Trainer build downloads pinned release weights; approve this separately in its protected build environment.")
        approved_feed()
        args += ["--secret", "id=pip-index,env=PIP_INDEX_URL", "--file", ROOT / "trainers/yolox/Dockerfile", ROOT / "trainers"]
    run(*args)
    digest = load(metadata)["containerimage.digest"]
    require(re.fullmatch(r"sha256:[a-f0-9]{64}", digest) and image_digest(host, repository, "@" + digest) == digest,
            "Build metadata does not match the registry manifest.")
    image = f"{host}/{repository}@{digest}"
    run("docker", "pull", "--platform", "linux/amd64", image)
    if component == "gpu":
        run(sys.executable, ROOT / "build/gpu/verify.py", "--stage", stage, "--assets", assets, "--image", image)
        from publish_gpu_build import publish
        identity = publish(settings, stage, assets)
    else:
        for command in VERIFY[component]:
            run("docker", "run", "--rm", "--pull", "never", "--network", "none", "--read-only",
                "--tmpfs", "/tmp:rw,mode=1777", "--env", "HOME=/tmp", "--env", "PYTHONDONTWRITEBYTECODE=1",
                "--env", "NVIDIA_VISIBLE_DEVICES=void", "--entrypoint", command[0], image, *command[1:])
        if component == "server":
            # Inspect the real image filesystem, not source strings: no operator, CI, infrastructure or customer files may ship.
            container = run("docker", "create", "--pull", "never", image).strip()
            try:
                run("docker", "cp", f"{container}:/app", work / "app")
            finally:
                run("docker", "rm", container)
            run(sys.executable, ROOT / "scripts/ci/check_server_content.py", work / "app")
        record = {"schemaVersion": 1, "component": component, "sourceSha": commit, "imageDigest": digest}
        if component == "yolox":
            # The trainer metadata is sealed with the image so promotion publishes exactly what was built and verified.
            metadata = load(ROOT / "trainers/yolox/trainer.json")
            record["trainer"] = {key: metadata[key] for key in TRAINER_FIELDS}
        identity = hashlib.sha256(json_bytes(record)).hexdigest()
        immutable_bytes(azure.container(control), f"images/{component}/{identity}.json", json_bytes(record))
    print(f"Verified {component} artifact {identity}. No deployment, GPU allocation, generation or training.")
    output = build_var("OUTPUT", required=False)
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"artifact={identity}\ndigest={digest}\n")


def approved_feed():
    from urllib.parse import urlsplit
    value = os.environ["PIP_INDEX_URL"]
    parsed = urlsplit(value)
    require(parsed.scheme == "https" and parsed.hostname in ("packagefeedproxy.microsoft.io", "pkgs.dev.azure.com") and
            not parsed.username and not parsed.password and not parsed.query and not parsed.fragment,
            "Use one approved HTTPS feed without embedded credentials.")
    return value


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--component", choices=("server", "gpu", "yolox"), required=True)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    args = parser.parse_args()
    args.work.mkdir(mode=0o700)
    build(args.component, args.settings, args.work)
