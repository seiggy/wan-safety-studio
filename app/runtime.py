"""CPU preparation and local-only portal around the pinned MIT upstream runtime."""
from __future__ import annotations

import argparse
from dataclasses import replace
from datetime import datetime, timezone
import hashlib
import json
import logging
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider

from cost_guard import UPSTREAM_SHA, public_error
from config import build_credential, load_foundation, package_index_url, scope_fingerprint
from models import prepare_models, sha256

HERE = Path(__file__).resolve().parent
UPSTREAM = "https://github.com/jakeatmsft/azureml_vidgen_comfyui.git"
BASE_IMAGE_TAG = "pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime"
BASE_IMAGE = BASE_IMAGE_TAG + "@sha256:417bd75df6365104c283ea4c1651fb3530d9eb5a4c2fafa51943cff2a94e6385"
TRACER = trace.get_tracer("wan-safety-studio.operator")


def run(*args, cwd=None, capture=False):
    executable = shutil.which(str(args[0]))
    if executable is None:
        raise FileNotFoundError(f"Required executable is unavailable: {args[0]}")
    with TRACER.start_as_current_span(f"process.{Path(str(args[0])).stem}", record_exception=False, set_status_on_exception=False):
        result = subprocess.run(
            [executable, *map(str, args[1:])], cwd=cwd, check=True, text=True,
            stdout=subprocess.PIPE if capture else None,
        )
    return result.stdout.strip() if capture else None


def az_json(*args):
    return json.loads(run("az", *args, "--output", "json", "--only-show-errors", capture=True))


def version_key(cache: Path):
    digest = hashlib.sha256()
    for name in ("upstream-cost.patch", "config.py", "cost_guard.py", "runtime.py", "models.py",
                 "wan-pinned-manifest.json", "pyproject.toml", "uv.lock"):
        digest.update((HERE / name).read_bytes())
    digest.update(scope_fingerprint().encode())
    digest.update(package_index_url().encode())
    if (cache / "python-project" / "uv.lock").read_bytes() != (HERE / "uv.lock").read_bytes():
        raise ValueError("Cached dependency lock differs from the reviewed project lock; rerun Prepare.")
    return f"{UPSTREAM_SHA[:12]}-{digest.hexdigest()[:16]}"


def write_json(path: Path, value):
    partial = path.with_suffix(".pending")
    partial.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    partial.replace(path)


def authenticate():
    foundation = load_foundation()
    from azure.ai.ml import MLClient
    credential = build_credential()
    return credential, MLClient(credential, foundation["subscriptionId"],
                                foundation["resourceGroupName"], foundation["workspaceName"])


def read_foundation(cache: Path):
    if Path(os.environ["WAN_STUDIO_FOUNDATION"]).resolve() != (cache / "foundation.json").resolve():
        raise ValueError("Foundation selector must belong to the current operator cache.")
    return load_foundation()


def checkout(cache: Path, version: str):
    root = cache / f"source-{version}"
    stamp = cache / f"source-{version}.json"
    if not root.exists():
        run("git", "clone", "--filter=blob:none", "--no-checkout", UPSTREAM, root)
        run("git", "-C", root, "config", "core.autocrlf", "false")
        run("git", "-C", root, "checkout", "--detach", UPSTREAM_SHA)
        if run("git", "-C", root, "status", "--porcelain", capture=True):
            raise ValueError("Fresh upstream checkout is unexpectedly dirty.")
        run("git", "-C", root, "apply", "--ignore-space-change", "--check", HERE / "upstream-cost.patch")
        run("git", "-C", root, "apply", "--ignore-space-change", HERE / "upstream-cost.patch")
        for helper in ("cost_guard.py", "config.py"):
            shutil.copy2(HERE / helper, root / "azureml" / helper)
        diff = run("git", "-C", root, "diff", "--no-ext-diff", "--binary", capture=True)
        write_json(stamp, {"diffSha256": hashlib.sha256(diff.encode()).hexdigest()})
    if run("git", "-C", root, "rev-parse", "HEAD", capture=True) != UPSTREAM_SHA:
        raise ValueError("Upstream checkout SHA changed.")
    run("git", "-C", root, "apply", "--ignore-space-change", "--reverse", "--check", HERE / "upstream-cost.patch")
    diff = run("git", "-C", root, "diff", "--no-ext-diff", "--binary", capture=True)
    if hashlib.sha256(diff.encode()).hexdigest() != json.loads(stamp.read_text())["diffSha256"]:
        raise ValueError("Upstream checkout has unexpected edits; use a fresh version cache.")
    for helper in ("cost_guard.py", "config.py"):
        if sha256(root / "azureml" / helper) != sha256(HERE / helper):
            raise ValueError("Runtime safety/configuration helper was modified.")
    return root


def safe_snapshot_path(name: str):
    parts = name.replace("\\", "/").split("/")
    denied = {".git", ".venv", "__pycache__", "node_modules"}
    if any(p.lower() in denied or p.lower().startswith(".env") for p in parts):
        return False
    if len(parts) > 1 and parts[1].lower() in {"models", "output", "outputs", "input", "user"}:
        return False
    if Path(parts[-1]).suffix.lower() in {".safetensors", ".ckpt", ".pt", ".pth", ".onnx", ".gguf"}:
        return False
    return parts[0] in ("src", "azureml", "LICENSE", "NOTICE.txt")


def snapshot(root: Path, target: Path):
    target.mkdir(parents=True, exist_ok=True)
    tracked = [p for p in run("git", "-C", root, "ls-files", "-z", capture=True).split("\0") if p]
    names = [p for p in tracked if safe_snapshot_path(p)] + ["azureml/cost_guard.py", "azureml/config.py"]
    names = [p for p in names if not Path(p).name.startswith(".")]
    records = []
    for name in sorted(set(names)):
        source = root / name
        # The pinned LTX tokenizer JSON is 33 MB; model-weight directories stay excluded.
        if source.is_symlink() or not source.is_file() or source.stat().st_size > 64 * 1024 * 1024:
            raise ValueError(f"Unexpected/non-code file in snapshot: {name}")
        destination = target / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        records.append({"path": name, "sha256": sha256(destination), "bytes": destination.stat().st_size})
    return records


def pin_runtime_base(dockerfile: str):
    lines = dockerfile.splitlines()
    if not lines or lines[0] != f"FROM {BASE_IMAGE_TAG}" or sum(line.startswith("FROM ") for line in lines) != 1:
        raise ValueError("Unexpected upstream runtime base; refusing an unpinned or additional build stage.")
    lines[0] = f"FROM {BASE_IMAGE}"
    lines[1:1] = [
        f"ARG PIP_INDEX_URL={package_index_url()}",
        "ENV PIP_INDEX_URL=${PIP_INDEX_URL}",
        'ENV PIP_CONFIG_FILE=/dev/null PIP_EXTRA_INDEX_URL=""',
    ]
    return "\n".join(lines) + "\n"


def build_local_image(root: Path, cache: Path, version: str, profile: str):
    foundation = load_foundation()
    context = cache / f"image-{version}-{profile}"
    context.mkdir(parents=True, exist_ok=True)
    for name in ("requirements-azureml.txt", "Dockerfile"):
        shutil.copy2(root / "azureml" / "context" / name, context / name)
    dockerfile = pin_runtime_base((context / "Dockerfile").read_text())
    # No host /tmp operations; container build scratch is kept under /opt as well.
    dockerfile = dockerfile.replace("/tmp/requirements-azureml.txt", "/opt/requirements-azureml.txt")
    dockerfile += "\nENV HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1\n"
    if profile == "minimax_h3":
        from azureml.web_submit import MINIMAX_H3_COMFY_REVISION
        dockerfile += (
            "RUN git init -q /opt/comfy-h3 && "
            "git -C /opt/comfy-h3 remote add origin https://github.com/Comfy-Org/ComfyUI.git && "
            f"git -C /opt/comfy-h3 fetch --depth=1 origin {MINIMAX_H3_COMFY_REVISION} && "
            "git -C /opt/comfy-h3 checkout -q FETCH_HEAD && "
            "python -m pip install --no-cache-dir comfy-kitchen==0.2.31 "
            "comfyui-frontend-package==1.51.9 comfyui-embedded-docs==0.5.10 "
            "comfy-aimdo==0.4.15 'av>=17.0.0'\n"
        )
    (context / "Dockerfile").write_text(dockerfile, encoding="utf-8")
    tag = f"{foundation['registryLoginServer']}/comfyui:{version}-{profile}"
    if run("docker", "info", "--format", "{{.OSType}}", capture=True) != "linux":
        raise ValueError("Docker must use Linux containers before Prepare.")
    run("docker", "build", "--platform", "linux/amd64", "--build-arg",
        f"PIP_INDEX_URL={package_index_url()}", "--tag", tag, context)
    run("docker", "run", "--rm", "--network", "none", "--env", "CUDA_VISIBLE_DEVICES=",
        "--env", "NVIDIA_VISIBLE_DEVICES=void", "--entrypoint", "python", tag, "-c",
        "import torch,aiohttp,av,PIL,safetensors; assert not torch.cuda.is_available(); print('CPU runtime imports OK')")
    return tag


def build_image(root: Path, cache: Path, version: str, profile: str):
    foundation = load_foundation()
    tag = build_local_image(root, cache, version, profile)
    run("az", "acr", "login", "--name", foundation["registryName"], "--only-show-errors")
    run("docker", "push", tag)
    details = az_json("acr", "repository", "show", "--name", foundation["registryName"], "--image", f"comfyui:{version}-{profile}")
    digest = details["digest"]
    if not re.fullmatch(r"sha256:[a-f0-9]{64}", digest):
        raise ValueError("Registry did not return a content-addressed digest.")
    return f"{foundation['registryLoginServer']}/comfyui@{digest}"


def upload_tree(source: Path, prefix: str):
    foundation = load_foundation()
    run("az", "storage", "blob", "upload-batch", "--account-name", foundation["storageAccountName"], "--auth-mode", "login",
        "--destination", foundation["containerName"], "--destination-path", prefix, "--source", source,
        "--overwrite", "false", "--only-show-errors", "--output", "none")


def verify_local_snapshot(image: str, code: Path, workflow: str, profile: str = "wan"):
    payload = json.loads((code / workflow).read_text(encoding="utf-8"))
    required = sorted({node["class_type"] for node in payload.values() if isinstance(node, dict) and "class_type" in node})
    if not required:
        raise ValueError("Prepared workflow has no API node classes.")
    comfy_source = "/opt/comfy-h3" if profile == "minimax_h3" else "/code/src"
    check = (
        "import asyncio,sys; from pathlib import Path; "
        "Path('/check/user').mkdir(parents=True); Path('/check/temp').mkdir(); "
        "sys.argv=['main.py','--cpu','--disable-all-custom-nodes','--base-directory','/check',"
        "'--temp-directory','/check/temp','--user-directory','/check/user',"
        "'--database-url','sqlite:////check/comfyui.db']; "
        f"sys.path.insert(0,{comfy_source!r}); import main; import nodes; "
        "asyncio.run(nodes.init_extra_nodes(init_custom_nodes=False,init_api_nodes=False)); "
        f"missing=set({required!r})-set(nodes.NODE_CLASS_MAPPINGS); "
        "assert not missing, f'Prepared snapshot is missing workflow nodes: {sorted(missing)}'; "
        "print('CPU staged-snapshot imports and workflow node classes OK')"
    )
    run("docker", "run", "--rm", "--pull", "never", "--network", "none",
        "--env", "CUDA_VISIBLE_DEVICES=", "--env", "NVIDIA_VISIBLE_DEVICES=void",
        "--env", "PYTHONDONTWRITEBYTECODE=1",
        "--env", "TMPDIR=/check/temp", "--tmpfs", "/check:rw,mode=1777",
        "--mount", f"type=bind,source={code.resolve()},target=/code,readonly",
        "--workdir", comfy_source, "--entrypoint", "python", image, "-c", check)


def blob_service(credential):
    from azure.storage.blob import BlobServiceClient
    return BlobServiceClient(f"https://{load_foundation()['storageAccountName']}.blob.core.windows.net", credential=credential)


def datastore_url():
    foundation = load_foundation()
    return (f"https://management.azure.com{foundation['workspaceId']}/datastores/"
            f"{foundation['datastoreName']}?api-version=2024-04-01")


def register_datastore(cache: Path):
    foundation = load_foundation()
    body = cache / "datastore.json"
    write_json(body, {"properties": {
        "datastoreType": "AzureBlob", "accountName": foundation["storageAccountName"], "containerName": foundation["containerName"],
        "endpoint": "core.windows.net", "protocol": "https", "credentials": {"credentialsType": "None"},
        "serviceDataAccessAuthIdentity": "WorkspaceUserAssignedIdentity", "tags": foundation["ownershipTags"],
    }})
    run("az", "rest", "--method", "put", "--url", datastore_url(), "--body", "@" + str(body),
        "--only-show-errors", "--output", "none")


def same_model_uri(actual: str, expected: str):
    foundation = load_foundation()
    full_prefix = (f"azureml://subscriptions/{foundation['subscriptionId']}/resourceGroups/{foundation['resourceGroupName']}/"
                   f"workspaces/{foundation['workspaceName']}/datastores/")
    return actual.replace(full_prefix, "azureml://datastores/").rstrip("/") == expected.rstrip("/")


def verify_cloud(manifest, credential, client):
    foundation = load_foundation()
    with TRACER.start_as_current_span("azureml.prepared-assets.verify", record_exception=False, set_status_on_exception=False):
        environment = client.environments.get(manifest["environmentName"], manifest["version"])
        model = client.models.get(manifest["modelsName"], manifest["modelsVersion"])
        datastore = az_json("rest", "--method", "get", "--url", datastore_url())["properties"]
        if environment.image != manifest["image"] or not same_model_uri(model.path, manifest["modelsUri"]):
            raise ValueError("Prepared environment/model registration changed.")
        if (datastore["accountName"], datastore["containerName"],
            datastore["credentials"]["credentialsType"], datastore["serviceDataAccessAuthIdentity"]) != (
            foundation["storageAccountName"], foundation["containerName"], "None", "WorkspaceUserAssignedIdentity"
        ):
            raise ValueError("Prepared datastore points outside this studio.")
        container = blob_service(credential).get_container_client(foundation["containerName"])
        for section, prefix in (("models", manifest["modelsPrefix"]), ("code", manifest["codePrefix"])):
            blobs = {b.name: b for b in container.list_blobs(name_starts_with=prefix + "/", include=["metadata"])}
            if len(blobs) != len(manifest[section]):
                raise ValueError(f"Prepared {section} snapshot contains missing or unexpected blobs.")
            for item in manifest[section]:
                properties = blobs[f"{prefix}/{item['path']}"]
                if properties.size != item["bytes"] or properties.metadata.get("sha256") != item["sha256"]:
                    raise ValueError(f"Prepared blob changed: {section}/{item['path']}")
        digest = az_json("acr", "repository", "show", "--name", foundation["registryName"],
                         "--image", "comfyui@" + manifest["image"].split("@")[1])["digest"]
        if digest != manifest["image"].split("@")[1]:
            raise ValueError("Prepared image digest missing.")


def prepare(args):
    from azure.ai.ml.entities import Environment, Model
    credential, client = authenticate()
    foundation = read_foundation(args.cache)
    version = version_key(args.cache)
    fingerprint = scope_fingerprint()
    previous = None
    if (args.cache / f"prepared-{args.profile}.json").is_file():
        previous = json.loads((args.cache / f"prepared-{args.profile}.json").read_text())
        if previous["version"] == version:
            prepared(args)
            print(f"Already prepared and cloud-verified: {args.profile} ({version}).")
            return
    root = checkout(args.cache, version)
    sys.path.insert(0, str(root))
    target = args.cache / f"assets-{version}-{args.profile}"
    local_models = args.local_models_path
    if local_models is None and previous and previous.get("sourceSha") == UPSTREAM_SHA:
        previous_models = Path(previous["assetRoot"]) / "models"
        if previous_models.is_dir():
            local_models = previous_models
    if local_models is None and args.profile == "wan":
        cached_models = args.cache / "models" / "wan"
        if cached_models.is_dir():
            local_models = cached_models
    profile, model_records = prepare_models(root, args.profile, target / "models", local_models=local_models)
    code_records = snapshot(root, target / "code")
    image = build_image(root, args.cache, version, args.profile)
    verify_local_snapshot(image, target / "code", profile.workflow, args.profile)
    run("az", "storage", "container", "create", "--account-name", foundation["storageAccountName"],
        "--name", foundation["containerName"],
        "--auth-mode", "login", "--public-access", "off", "--only-show-errors", "--output", "none")
    with TRACER.start_as_current_span("azureml.assets.register", record_exception=False, set_status_on_exception=False):
        register_datastore(args.cache)
        code_prefix, models_prefix = f"code/{version}-{args.profile}", f"models/{version}-{args.profile}"
        # Reuse only checksum-matched model blobs in the currently selected container.
        if previous and previous["profile"] == args.profile and previous["models"] == model_records:
            models_prefix = previous["modelsPrefix"]
            if not re.fullmatch(rf"models/[a-z0-9-]+-{re.escape(args.profile)}", models_prefix):
                raise ValueError("Previous model snapshot prefix is outside the immutable model namespace.")
        service = blob_service(credential).get_container_client(foundation["containerName"])
        for kind, prefix, records in (("code", code_prefix, code_records), ("models", models_prefix, model_records)):
            # Native CLI upload uses Entra, never workspace SharedKey code staging.
            existing = {b.name: b for b in service.list_blobs(name_starts_with=prefix + "/", include=["metadata"])}
            if not existing:
                upload_tree(target / kind, prefix)
            for record in records:
                blob_name = f"{prefix}/{record['path']}"
                blob = service.get_blob_client(blob_name)
                if existing and blob_name not in existing:
                    run("az", "storage", "blob", "upload", "--account-name", foundation["storageAccountName"],
                        "--auth-mode", "login", "--container-name", foundation["containerName"], "--name", blob_name,
                        "--file", target / kind / record["path"], "--overwrite", "false",
                        "--only-show-errors", "--output", "none")
                properties = blob.get_blob_properties()
                if properties.size != record["bytes"]:
                    raise ValueError("An existing immutable asset path has different content/size.")
                if properties.metadata.get("sha256") not in (None, record["sha256"]):
                    raise ValueError("An existing immutable asset checksum differs.")
                if existing and blob_name in existing and properties.metadata.get("sha256") is None:
                    # Recover an interrupted CLI upload only after comparing the remote bytes.
                    digest = hashlib.sha256()
                    for chunk in blob.download_blob().chunks():
                        digest.update(chunk)
                    if digest.hexdigest() != record["sha256"]:
                        raise ValueError("Existing blob content differs from the validated local asset.")
                blob.set_blob_metadata({"sha256": record["sha256"]})
        license_prefix = f"licenses/{version}-{args.profile}"
        if not list(service.list_blobs(name_starts_with=license_prefix + "/")):
            upload_tree(target / "licenses", license_prefix)
        code_uri = f"azureml://datastores/{foundation['datastoreName']}/paths/{code_prefix}/"
        models_uri = f"azureml://datastores/{foundation['datastoreName']}/paths/{models_prefix}/"
        env_name = f"{foundation['deploymentName']}-{args.profile}"
        # Azure ML requires a positive integer model version; keep the content hash in its name.
        models_name, models_version = f"{foundation['deploymentName']}-{args.profile}-models-{version}", "1"
        environment = client.environments.create_or_update(Environment(
            name=env_name, version=version, image=image, tags=foundation["ownershipTags"],
            description=f"MIT upstream {UPSTREAM_SHA}; local CPU build; immutable ACR digest.",
        ))
        client.models.create_or_update(Model(
            name=models_name, version=models_version, type="custom_model", path=models_uri, tags=foundation["ownershipTags"],
            description=f"Workflow-only models; immutable origins/checksums/license records retained at licenses/{version}-{args.profile}.",
        ))
    manifest = {
        "sourceSha": UPSTREAM_SHA, "version": version, "profile": args.profile, "scopeFingerprint": fingerprint,
        "workflow": profile.workflow, "sourceRoot": str(root), "assetRoot": str(target),
        "image": image, "environmentName": env_name, "environmentId": environment.id,
        "modelsName": models_name, "modelsVersion": models_version,
        "modelsRef": f"azureml:{models_name}:{models_version}",
        "modelsUri": models_uri, "codeUri": code_uri, "codePrefix": code_prefix, "modelsPrefix": models_prefix,
        "computeIdentityClientId": foundation["computeIdentityClientId"],
        "models": model_records, "code": code_records, "preparedUtc": datetime.now(timezone.utc).isoformat(),
    }
    verify_cloud(manifest, credential, client)
    write_json(args.cache / f"prepared-{args.profile}.json", manifest)
    print(f"Prepared {args.profile} ({version}); compute was not enabled.")


def prepared(args, cloud=True):
    read_foundation(args.cache)
    manifest = json.loads((args.cache / f"prepared-{args.profile}.json").read_text())
    if (manifest["version"] != version_key(args.cache) or manifest["sourceSha"] != UPSTREAM_SHA or
            manifest["profile"] != args.profile or manifest["scopeFingerprint"] != scope_fingerprint()):
        raise ValueError("Source/adapter/lock changed; Prepare must complete again before Start.")
    root = checkout(args.cache, manifest["version"])
    if root.resolve() != Path(manifest["sourceRoot"]).resolve():
        raise ValueError("Prepared source path differs from the immutable checkout.")
    for item in manifest["code"]:
        if sha256(root / item["path"]) != item["sha256"]:
            raise ValueError(f"Pinned runtime changed: {item['path']}")
    os.environ["WAN_STUDIO_CONTRACT"] = str((args.cache / f"prepared-{args.profile}.json").resolve())
    os.environ["WAN_STUDIO_GATE"] = str((args.cache / "armed.json").resolve())
    sys.path.insert(0, str(root))
    if cloud:
        credential, client = authenticate()
        verify_cloud(manifest, credential, client)
    return manifest


def portal_settings(manifest):
    foundation = load_foundation()
    from azureml import web_submit
    flags = [
        "--host", "127.0.0.1", "--port", "51881",
        "--subscription-id", foundation["subscriptionId"], "--resource-group", foundation["resourceGroupName"],
        "--workspace-name", foundation["workspaceName"], "--compute", foundation["computeName"],
        "--default-profile", manifest["profile"], "--gallery-profile", manifest["profile"],
        "--code-path", manifest["codeUri"], "--code-version", manifest["version"],
        "--environment-id", manifest["environmentId"], "--environment-version", manifest["version"],
        "--models-path", manifest["modelsRef"], "--models-version", manifest["modelsVersion"], "--models-asset-kind", "model",
        "--storage-account", foundation["storageAccountName"], "--storage-container", foundation["containerName"],
        "--upload-storage-account", foundation["storageAccountName"], "--upload-storage-container", foundation["containerName"],
        "--gallery-storage-account", foundation["storageAccountName"], "--gallery-storage-container", foundation["containerName"],
        "--gallery-output-datastore", foundation["datastoreName"], "--gallery-output-prefix", "video-library",
        "--remote-root", foundation["deploymentName"], "--sas-ttl-hours", "3",
    ]
    old = sys.argv
    try:
        sys.argv = ["web_submit.py"] + flags
        settings = web_submit.resolve_settings(web_submit.parse_args())
    finally:
        sys.argv = old
    return replace(settings, profiles={manifest["profile"]: settings.profiles[manifest["profile"]]})


def hosted_release(args):
    """App Service entry: verify the Publish bundle offline (no Azure CLI) and materialize the gate."""
    release = HERE / "release"
    hostname = (load_foundation().get("portal") or {}).get("hostname", "")
    if not re.fullmatch(r"[a-z0-9-]+(\.[a-z0-9-]+)*\.azurewebsites\.net", hostname):
        raise ValueError("Foundation has no App Service portal hostname; run Deploy and Publish.")
    os.environ["WAN_STUDIO_PUBLIC_ORIGIN"] = "https://" + hostname
    contract = release / f"prepared-{args.profile}.json"
    manifest = json.loads(contract.read_text())
    if (manifest["sourceSha"], manifest["profile"], manifest["scopeFingerprint"]) != (
            UPSTREAM_SHA, args.profile, scope_fingerprint()):
        raise ValueError("Published release differs from this studio; run Publish again.")
    root = HERE / "upstream"
    bundled = [item for item in manifest["code"]
               if item["path"].startswith("azureml/") or item["path"] in ("LICENSE", "NOTICE.txt")]
    if not bundled or any(sha256(root / item["path"]) != item["sha256"] for item in bundled):
        raise ValueError("Published upstream runtime differs from the prepared manifest.")
    # Start/Stop set or remove this app setting through ARM; each change restarts the site.
    gate = args.cache / "armed.json"
    armed = os.environ.get("WAN_STUDIO_ARMED")
    if armed:
        value = json.loads(armed)
        write_json(gate, {key: value[key] for key in ("compute", "profile", "version")})
    else:
        gate.unlink(missing_ok=True)
    os.environ["WAN_STUDIO_CONTRACT"] = str(contract)
    os.environ["WAN_STUDIO_GATE"] = str(gate)
    sys.path.insert(0, str(root))
    return manifest


def portal(args):
    from aiohttp import web
    if args.profile != "wan":
        raise ValueError("The authenticated portal currently supports only the validated WAN profile.")
    manifest, web_submit = None, None
    if args.hosted:
        manifest = hosted_release(args)
    elif (args.cache / f"prepared-{args.profile}.json").is_file():
        manifest = prepared(args)
    # Imported after hosted_release selects the public origin.
    from portal import create_app, foundation_settings, load_auth_config
    settings = foundation_settings()
    if manifest is not None:
        from azureml import web_submit
        settings = portal_settings(manifest)
        web_submit.upload_blob = upload_input_blob
    if args.hosted:
        auth = load_auth_config(HERE / "release")
        identity = build_credential()
        # Secretless: the managed identity's token is the app registration's federated assertion.
        secret = {"client_assertion": lambda: identity.get_token("api://AzureADTokenExchange/.default").token}
        host, port = "0.0.0.0", int(os.environ.get("PORT", "8000"))
    else:
        auth = load_auth_config(args.cache)
        with TRACER.start_as_current_span("keyvault.portal-secret.read", record_exception=False, set_status_on_exception=False):
            secret = az_json("keyvault", "secret", "show", "--vault-name", load_foundation()["keyVaultName"],
                             "--name", auth["secretName"])["value"]
        host, port = "127.0.0.1", 51881
    web.run_app(create_app(settings, manifest, args.cache, auth, secret, web_submit),
                host=host, port=port, access_log=None)


def upload_input_blob(local_path: Path, blob_name: str, settings):
    foundation = load_foundation()
    from azureml import web_submit
    if (settings.upload_storage_account, settings.upload_storage_container) != (
            foundation["storageAccountName"], foundation["containerName"]):
        raise ValueError("Input upload target must be the private studio container.")
    service = web_submit.make_blob_service_client(settings, account_name=foundation["storageAccountName"])
    with TRACER.start_as_current_span("azure.storage.input.upload", record_exception=False, set_status_on_exception=False):
        with local_path.open("rb") as handle:
            service.get_blob_client(container=foundation["containerName"], blob=blob_name).upload_blob(handle, overwrite=False)
    return f"azureml://datastores/{foundation['datastoreName']}/paths/{blob_name}"


def media_tool(tool: str, directory: Path, image: str, writable=False):
    if shutil.which(tool):
        return [tool]
    mount = f"type=bind,source={directory},target=/videos" + ("" if writable else ",readonly")
    return ["docker", "run", "--rm", "--pull", "never", "--network", "none",
            "--mount", mount, "--entrypoint", tool, image]


def verify_smoke_videos(directory: Path, image: str):
    for source in directory.rglob("*.webm"):
        destination = source.with_suffix(".mp4")
        if destination.exists():
            continue
        command = media_tool("ffmpeg", source.parent, image, writable=True)
        source_path = str(source) if len(command) == 1 else "/videos/" + source.name
        destination_path = str(destination) if len(command) == 1 else "/videos/" + destination.name
        run(*command, "-nostdin", "-v", "error", "-n", "-i", source_path,
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", destination_path)
    videos = list(directory.rglob("*.mp4"))
    if not videos:
        raise ValueError("Job completed but no video was downloaded.")
    for video in videos:
        probe = media_tool("ffprobe", video.parent, image)
        probe_path = str(video) if len(probe) == 1 else "/videos/" + video.name
        metadata = json.loads(run(*probe, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height:format=duration", "-of", "json", probe_path, capture=True))
        if (not metadata.get("streams") or float(metadata["format"]["duration"]) <= 0 or
                (metadata["streams"][0]["width"], metadata["streams"][0]["height"]) != (512, 512)):
            raise ValueError("Downloaded smoke video is invalid or not 512x512.")
    return len(videos)


def submit(args):
    manifest = prepared(args)
    from azureml import web_submit, workflow_profiles, register_and_submit
    settings = portal_settings(manifest)
    image = args.input_image.resolve(strict=True)
    web_submit.validate_image(image)
    image_url = upload_input_blob(image, web_submit.make_blob_name(settings, image.name), settings)
    submit_args = web_submit.build_submission_args(
        settings, settings.profiles["wan"], prompt="A colorful geometric toy gently moves in a studio.",
        negative_prompt="", duration_seconds=1.0,
        uploaded_images={"input_image": {"url": image_url, "filename": image.name}},
    )
    submit_args.width = submit_args.height = 512
    submit_args.steps, submit_args.batch_size, submit_args.timeout_seconds = 4, 1, args.timeout_minutes * 60
    submit_args.length = workflow_profiles.duration_seconds_to_length(workflow_profiles.get_profile("wan"), 1.0)
    with TRACER.start_as_current_span("azureml.job.submit", record_exception=False, set_status_on_exception=False):
        client, job, _ = register_and_submit.create_job_submission(submit_args)
    image_url = None
    job_record = {"name": job.name, "status": job.status}
    write_json(args.cache / "last-job.json", job_record)
    print(json.dumps(job_record, indent=2))
    if not args.wait:
        return
    deadline = time.monotonic() + args.timeout_minutes * 60 + 1800
    while job.status not in ("Completed", "Failed", "Canceled", "NotResponding"):
        if time.monotonic() > deadline:
            client.jobs.cancel(job.name)
            raise TimeoutError("Client wait (including queue allowance) expired; job canceled. Run Stop.")
        time.sleep(15)
        with TRACER.start_as_current_span("azureml.job.poll", record_exception=False, set_status_on_exception=False):
            job = client.jobs.get(job.name)
    if job.status != "Completed":
        raise RuntimeError(f"Smoke job {job.name} ended {job.status}; run Stop.")
    download_directory = args.download_directory / job.name
    download_directory.mkdir(parents=True, exist_ok=True)
    with TRACER.start_as_current_span("azureml.video.download", record_exception=False, set_status_on_exception=False):
        client.jobs.download(job.name, output_name="generated", download_path=str(download_directory))
    count = verify_smoke_videos(download_directory, manifest["image"])
    print(f"Verified {count} local MP4 file(s). Run Stop immediately (also in caller finally).")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cache", type=Path, required=True)
    commands = parser.add_subparsers(dest="action", required=True)
    for name in ("prepare", "verify", "portal", "submit"):
        sub = commands.add_parser(name)
        sub.add_argument("--profile", choices=("wan",) if name == "submit" else ("wan", "ltx", "ltx_i2v", "minimax_h3"), required=True)
        if name == "prepare":
            sub.add_argument("--local-models-path", type=Path)
        if name == "portal":
            sub.add_argument("--hosted", action="store_true")
        if name == "submit":
            sub.add_argument("--input-image", type=Path, required=True)
            sub.add_argument("--timeout-minutes", type=int, choices=range(1, 121), default=120)
            sub.add_argument("--wait", action="store_true")
            sub.add_argument("--download-directory", type=Path)
    args = parser.parse_args()
    if args.action == "submit" and args.wait and args.download_directory is None:
        parser.error("--wait requires --download-directory")
    args.cache = args.cache.resolve()
    if args.action == "submit" and args.download_directory is not None:
        args.download_directory = args.download_directory.resolve()
    trace.set_tracer_provider(TracerProvider())
    logging.getLogger("azure").setLevel(logging.CRITICAL + 1)
    logging.getLogger("azure.core.pipeline.policies.http_logging_policy").disabled = True
    args.cache.mkdir(parents=True, exist_ok=True)
    # Upstream image upload helpers use tempfile; confine their scratch to the private cache.
    scratch = args.cache / "scratch"
    scratch.mkdir(exist_ok=True)
    import tempfile
    tempfile.tempdir = str(scratch)
    {"prepare": prepare, "verify": prepared, "portal": portal, "submit": submit}[args.action](args)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Never render SDK request objects/tracebacks that might contain an input SAS.
        print(public_error(error), file=sys.stderr)
        raise SystemExit(1)
