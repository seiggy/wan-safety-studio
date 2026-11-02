"""Build one independent image; GPU publication remains unready until the staged CPU probe succeeds."""
import argparse
import hashlib
import os
from pathlib import Path
import re
import sys

from pipeline import (Azure, ROOT, build_var, containers, contracts, image_digest, immutable_bytes, json_bytes, load, require,
                      require_single_amd64_manifest, run)


# Executables each image must contain and run, checked offline with no network and a read-only root.
VERIFY = {
    "server": (("dotnet", "--info"), ("ffmpeg", "-version"), ("ffprobe", "-version")),
    "yolox": (("python", "-m", "vision.trainers.yolox", "--help"),),
}
TRAINER_FIELDS = ("name", "version", "module", "presets", "defaultPreset", "licenseCode", "licenseWeights")


def gpu_cache(name):
    """An optional persistent cache folder. Unset: the input lives only in this run's work folder. Set: it is created when missing and filled on first use (a reset or a new machine just refills it)."""
    value = os.environ.get(name, "").strip()
    if value:
        require(Path(value).is_absolute(), f"{name} must be an absolute path on this runner.")
        Path(value).mkdir(parents=True, exist_ok=True)
    return value or None


def fetch_upstream(destination):
    """The pinned upstream commit, fetched by exact SHA into the folder unless it already holds it (a reused cache). prepare.py then verifies the commit identity."""
    upstream = load(ROOT / "build/gpu/profiles.json")["upstream"]
    require(re.fullmatch(r"https://[a-z0-9.-]+/[A-Za-z0-9._/-]+\.git", upstream["repository"]) and
            re.fullmatch(r"[a-f0-9]{40}", upstream["sourceSha"]), "build/gpu/profiles.json has no valid pinned upstream.")
    destination.mkdir(parents=True, exist_ok=True)
    if not (destination / ".git").exists():
        require(not any(destination.iterdir()), "STUDIO_UPSTREAM_SOURCE is a non-empty folder that is not a git repository.")
        run("git", "-C", destination, "init", "--quiet")
    try:
        run("git", "-C", destination, "rev-parse", "--verify", "--quiet", upstream["sourceSha"] + "^{commit}")
    except RuntimeError:
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
    secrets = []
    for secret, variable in (("npmrc", "STUDIO_NPMRC"), ("nugetconfig", "STUDIO_NUGET_CONFIG"), ("netrc", "STUDIO_NETRC")):
        if os.environ.get(variable):
            require(Path(os.environ[variable]).is_file(), "Configured build secret file is missing.")
            secrets.append((secret, "src", os.environ[variable]))
    build_args = []
    if component == "gpu":
        stage, assets = work / "stage", work / "assets"
        # Self-contained by default: the pinned commit and the pinned weights are fetched into this run's work folder and
        # verified (commit identity, size and SHA-256). The cache variables only let a runner reuse inputs it already holds.
        # A cache folder fills itself on the first run and is only verified afterwards; an unset one lives and dies with the work folder.
        source = fetch_upstream(Path(gpu_cache("STUDIO_UPSTREAM_SOURCE") or work / "upstream"))
        models, licenses = gpu_cache("STUDIO_MODEL_CACHE"), gpu_cache("STUDIO_LICENSE_CACHE")
        run(sys.executable, ROOT / "build/gpu/prepare.py", "--source", source,
            "--output", stage, "--workflows", ROOT / "contracts/fixtures/wan-workflows")
        run(sys.executable, ROOT / "build/gpu/models.py", "--output", assets,
            "--models", models or work / "model-cache", "--licenses", licenses or work / "license-cache", "--download")
        selected = load(stage / "stage.json")
        approved_feed()
        build_args.append("RUNTIME_LOCK_SHA256=" + selected["dependencyLockSha256"])
        secrets.append(("pip-index", "env", "PIP_INDEX_URL"))
        dockerfile, context = stage / "image/Dockerfile", stage / "image"
    elif component == "server":
        dockerfile, context = ROOT / "src/VideoSafetyStudio.Server/Dockerfile", ROOT
    else:
        require(os.environ.get("STUDIO_APPROVE_TRAINER_WEIGHTS") == "true",
                "Trainer build downloads pinned release weights; approve this separately in its protected build environment.")
        approved_feed()
        secrets.append(("pip-index", "env", "PIP_INDEX_URL"))
        dockerfile, context = ROOT / "trainers/yolox/Dockerfile", ROOT / "trainers"
    cli = containers()
    image_id, digest = cli.build_and_push(tag, dockerfile, context, build_args, secrets)
    require(re.fullmatch(r"sha256:[a-f0-9]{64}", digest) and image_digest(host, repository, "@" + digest) == digest,
            "Pushed image digest does not match the registry manifest.")
    require_single_amd64_manifest(host, repository, digest)
    image = f"{host}/{repository}@{digest}"
    require(cli.pull(image)["Id"] == image_id, "The registry digest does not name the image that was built.")
    if component == "gpu":
        run(sys.executable, ROOT / "build/gpu/verify.py", "--stage", stage, "--assets", assets, "--image", image)
        from publish_gpu_build import publish
        identity = publish(settings, stage, assets)
    else:
        for command in VERIFY[component]:
            cli.run_offline(image, command[0], command[1:], tmpfs=("/tmp:rw,mode=1777",),
                            env=("HOME=/tmp", "PYTHONDONTWRITEBYTECODE=1", "NVIDIA_VISIBLE_DEVICES=void"))
        if component == "server":
            # Inspect the real image filesystem, not source strings: no operator, CI, infrastructure or customer files may ship.
            cli.copy_out(image, "/app", work / "app")
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
