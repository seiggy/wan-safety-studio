"""Shared private-pipeline I/O. No implicit credential chain, compute or tenant administration."""
from __future__ import annotations

import hashlib
import base64
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "build" / "gpu"))
from telemetry import configure_telemetry
from opentelemetry import trace

configure_telemetry("wan-studio-pipeline")
TRACER = trace.get_tracer("wan.studio.pipeline")
ML_API = "2024-10-01"
WORKSPACE_API = "2025-09-01"
ARM = "https://management.azure.com"
ALLOWED_ENV = {
    "PATH", "HOME", "USERPROFILE", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TMP", "TEMP",
    "TMPDIR", "APPDATA", "LOCALAPPDATA", "AZURE_CONFIG_DIR", "DOCKER_CONFIG", "DOCKER_HOST",
    "DOCKER_CONTEXT", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
    "DOTNET_ROOT", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT", "PIP_INDEX_URL",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def build_var(name, required=True):
    """Build provenance from any CI: STUDIO_BUILD_<name>, falling back to the GITHUB_<name> variables GitHub Actions sets."""
    github = {"SOURCE_SHA": "GITHUB_SHA", "RUN_ID": "GITHUB_RUN_ID", "RUN_ATTEMPT": "GITHUB_RUN_ATTEMPT", "OUTPUT": "GITHUB_OUTPUT"}[name]
    value = os.environ.get("STUDIO_BUILD_" + name) or os.environ.get(github)
    if required and not value:
        raise ValueError(f"STUDIO_BUILD_{name} is required (set it from your CI; GitHub Actions supplies {github}).")
    return value


def federated_identity_required():
    return os.environ.get("STUDIO_FEDERATED_IDENTITY") == "true" or os.environ.get("GITHUB_ACTIONS") == "true"


def wiring(target, region_key, field):
    """Pipeline-only workspace wiring (not host settings); a missing value must stop publication."""
    value = target.get(field)
    require(isinstance(value, str) and value,
            f"Pipeline:Regions:{region_key}:{field[0].upper() + field[1:]} is required to publish.")
    return value


SECRET_PATTERNS = (
    re.compile(r"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*)?"),
    re.compile(r"(?i)(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}"),
    re.compile(r"(?i)([?&;](?:sig|se|sv|sp|sr|st|skoid|sktid|skt|ske|sks|skv|token|access_token|code|key)=)[^&\s\"']+"),
    re.compile(r"(?i)((?:password|passwd|secret|token|authorization|credential)[\w-]*\s*[:=]\s*)\S+"),
    re.compile(r"[A-Za-z0-9+/=_-]{48,}"),
)


def redacted_tail(text, lines=30, limit=3000):
    """The end of a tool's stderr with anything token-, signature- or secret-shaped replaced."""
    tail = "\n".join((text or "").strip().splitlines()[-lines:])
    for pattern in SECRET_PATTERNS:
        tail = pattern.sub(lambda m: (m.group(1) if m.re.groups else "") + "[redacted]", tail)
    return tail[-limit:] or "no error output"


def arm_failure(method, resource_id, error, actor_id):
    """Name the resource, the verified caller and Azure's own reason: ids and error text only, never a token or request body."""
    try:
        detail = json.loads(error.read()).get("error", {})
        reason = f"{detail.get('code')}: {detail.get('message')}"[:600]
    except Exception:
        reason = "no error detail"
    return (f"ARM {method} failed (HTTP {error.code}); the live Azure resource could not be verified. "
            f"Resource: {resource_id}. Caller object id: {actor_id}. Azure said: {reason}")


@TRACER.start_as_current_span("pipeline.tool", record_exception=False, set_status_on_exception=False)
def run(*args, input=None):
    environment = {key: value for key, value in os.environ.items() if key in ALLOWED_ENV}
    program = Path(str(args[0])).stem.lower()
    if program not in ("az", "terraform"):
        environment.pop("AZURE_CONFIG_DIR", None)
    if program != "docker":
        environment.pop("PIP_INDEX_URL", None)
    environment.update(PYTHONIOENCODING="utf-8", PYTHONUTF8="1")
    command = [str(arg) for arg in args]
    command[0] = shutil.which(command[0]) or command[0]
    result = subprocess.run(command, input=input, text=True, encoding="utf-8", capture_output=True,
                            env=environment)
    if result.returncode:
        # Azure/Docker errors can contain signed URLs or tokens: only a redacted tail of stderr is shown.
        raise RuntimeError(f"{Path(str(args[0])).name} {args[1] if len(args) > 1 else ''} failed (exit {result.returncode}). "
                           f"Last error lines (redacted):\n{redacted_tail(result.stderr)}")
    return result.stdout


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def json_bytes(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()


def load(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON property.")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def contracts(settings, build=None, release=None, trainer=None):
    tool = os.environ.get("STUDIO_CONTRACT_TOOL")
    require(tool and Path(tool).is_file(), "Build tools/VideoSafetyStudio.Contracts and set STUDIO_CONTRACT_TOOL.")
    args = ["dotnet", tool, settings]
    if trainer:
        require(not build and not release, "A trainer binding is validated on its own.")
        args += ["--trainer", trainer]
    if build:
        args.append(build)
    if release:
        require(build, "Release validation requires its build.")
        args.append(release)
    return json.loads(run(*args))


def private_host(host):
    addresses = {item[4][0] for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)}
    ranges = [ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")]
    require(addresses and all(any(ipaddress.ip_address(value) in network for network in ranges) for value in addresses),
            "Private runner DNS returned an empty, public or unsupported address.")
    with socket.create_connection((host, 443), timeout=15):
        pass


class Azure:
    def __init__(self, scope):
        account = json.loads(run("az", "account", "show", "--only-show-errors", "-o", "json"))
        require(account["tenantId"].lower() == scope["tenantId"].lower() and
                account["id"].lower() == scope["subscriptionId"].lower() and
                account.get("environmentName") == "AzureCloud", "Selected Azure CLI scope/cloud differs from explicit inputs.")
        client = os.environ.get("AZURE_CLIENT_ID")
        if federated_identity_required():
            require(client and account["user"]["type"] == "servicePrincipal" and
                    account["user"]["name"].lower() == client.lower(), "Pipeline must use its explicit federated CLI identity.")
        from azure.identity import AzureCliCredential
        self.credential = AzureCliCredential(tenant_id=scope["tenantId"])
        self.scope = scope
        token = self.credential.get_token(ARM + "/.default").token
        self.actor_id = actor_id(token, scope["tenantId"], client)

    @TRACER.start_as_current_span("pipeline.arm", record_exception=False, set_status_on_exception=False)
    def arm(self, resource_id, api=ML_API, method="GET", body=None, missing=False):
        require(resource_id.startswith(f"/subscriptions/{self.scope['subscriptionId']}/"),
                "ARM request escaped the selected subscription.")
        url = ARM + resource_id + ("&" if "?" in resource_id else "?") + "api-version=" + api
        headers = {"Authorization": "Bearer " + self.credential.get_token(ARM + "/.default").token}
        data = None if body is None else json_bytes(body)
        if data is not None:
            headers["Content-Type"] = "application/json"
        try:
            with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers, method=method), timeout=120) as response:
                content = response.read()
                return json.loads(content) if content else None
        except urllib.error.HTTPError as error:
            if missing and error.code == 404:
                return None
            raise RuntimeError(arm_failure(method, resource_id, error, self.actor_id)) from None

    def owned(self, resource_id, tags, api):
        value = self.arm(resource_id, api)
        require(value and value.get("id", "").lower() == resource_id.lower() and
                all(value.get("tags", {}).get(key) == item for key, item in tags.items()), "Live resource identity/ownership changed.")
        return value

    def registry(self, region):
        value = self.owned(region["registryId"], region["ownershipTags"], "2023-07-01")
        properties = value["properties"]
        require(properties.get("publicNetworkAccess") == "Disabled" and not properties.get("adminUserEnabled") and
                not properties.get("anonymousPullEnabled"), "Registry is not private/Entra-only.")
        host = region["registryId"].rsplit("/", 1)[1] + ".azurecr.io"
        require(properties["loginServer"] == host, "Registry endpoint changed.")
        private_host(host)
        token = run("az", "acr", "login", "--name", host.split(".")[0], "--expose-token", "--query", "accessToken", "-o", "tsv").strip()
        run("docker", "login", host, "--username", "00000000-0000-0000-0000-000000000000", "--password-stdin", input=token)
        return host

    def container(self, region, field="releaseContainer"):
        value = self.owned(region["storageAccountId"], region["ownershipTags"], "2023-05-01")
        properties = value["properties"]
        require(properties.get("publicNetworkAccess") == "Disabled" and properties.get("allowSharedKeyAccess") is False and
                properties.get("allowBlobPublicAccess") is False, "Storage is not private/Entra-only.")
        host = region["storageAccountId"].rsplit("/", 1)[1] + ".blob.core.windows.net"
        private_host(host)
        return self.blob_container(host.split(".")[0], region[field])

    def blob_container(self, account, container):
        from azure.storage.blob import BlobServiceClient
        require(re.fullmatch(r"[a-z0-9]{3,24}", account) and re.fullmatch(r"[a-z0-9][a-z0-9-]{1,61}[a-z0-9]", container),
                "Invalid private storage location.")
        private_host(account + ".blob.core.windows.net")
        result = BlobServiceClient(f"https://{account}.blob.core.windows.net", credential=self.credential).get_container_client(container)
        require(result.get_container_properties().public_access is None, "Artifact container must be private and pre-created.")
        return result


@TRACER.start_as_current_span("pipeline.immutable-record", record_exception=False, set_status_on_exception=False)
def immutable_bytes(container, name, content):
    from azure.core.exceptions import ResourceExistsError
    blob = container.get_blob_client(name)
    try:
        blob.upload_blob(content, overwrite=False)
    except ResourceExistsError:
        require(blob.download_blob().readall() == content, "Refusing to overwrite different immutable bytes.")
    require(blob.download_blob().readall() == content, "Immutable record read-back differs.")


def actor_id(token, tenant, client=None):
    # Audit/binding only, not token authorization: ARM/storage validate the real CLI token.
    # An opaque token cannot supply this evidence; never ask for tenant-wide Graph access as a fallback.
    try:
        parts = token.split(".")
        require(len(parts) == 3, "Opaque ARM token.")
        claims = json.loads(base64.urlsafe_b64decode(parts[1] + "=" * (-len(parts[1]) % 4)))
        actor = claims["oid"]
        require(str(uuid.UUID(actor)) == actor and claims["tid"] == tenant and
                (not client or claims.get("appid", claims.get("azp")) == client), "Caller claim mismatch.")
        return actor
    except (ValueError, KeyError, TypeError):
        raise ValueError("Cannot bind the explicit CLI caller to a canonical principal/tenant/client; no publication permitted.") from None


def snapshot_id(records):
    return hashlib.sha256(json_bytes(sorted(records, key=lambda item: item["path"].encode("utf-16-be")))).hexdigest()


@TRACER.start_as_current_span("pipeline.verified-snapshot", record_exception=False, set_status_on_exception=False)
def snapshot(container, kind, records, source):
    """Create-only files; hash the actual remote bytes before trusting/reusing a snapshot."""
    from azure.core.exceptions import ResourceExistsError
    prefix = kind + "/" + snapshot_id(records)
    expected = {prefix + "/" + item["path"]: item for item in records}
    found = {item.name for item in container.list_blobs(name_starts_with=prefix + "/")}
    require(found.issubset(expected), "Immutable snapshot contains unexpected blobs.")
    for name, record in expected.items():
        blob = container.get_blob_client(name)
        if name not in found:
            with source(record) as stream:
                try:
                    blob.upload_blob(stream, overwrite=False, length=record["bytes"])
                except ResourceExistsError:
                    pass  # A racing create is accepted only after the same byte verification below.
        digest, count = hashlib.sha256(), 0
        for chunk in blob.download_blob().chunks():
            digest.update(chunk)
            count += len(chunk)
        require(count == record["bytes"] and digest.hexdigest() == record["sha256"],
                "Immutable snapshot bytes differ; no overwrite or regional binding is permitted.")
    require({item.name for item in container.list_blobs(name_starts_with=prefix + "/")} == set(expected),
            "Immutable snapshot changed while verifying.")
    return prefix


def download_source(container, prefix, directory):
    def open_file(record):
        path = directory / "transfer"
        with path.open("wb") as stream:
            container.get_blob_client(prefix + "/" + record["path"]).download_blob().readinto(stream)
        require(path.stat().st_size == record["bytes"] and sha(path) == record["sha256"], "Source snapshot bytes changed.")
        return path.open("rb")
    return open_file


def image_digest(host, repository, reference):
    return json.loads(run("az", "acr", "repository", "show", "--name", host.split(".")[0],
                          "--image", repository + reference, "-o", "json"))["digest"]


@TRACER.start_as_current_span("pipeline.image-transfer", record_exception=False, set_status_on_exception=False)
def promote_image(azure, source, target, repository, digest):
    require(re.fullmatch(r"sha256:[a-f0-9]{64}", digest), "Expected a registry manifest digest.")
    first, second = azure.registry(source), azure.registry(target)
    require(image_digest(first, repository, "@" + digest) == digest, "Source image missing or changed.")
    image = first + "/" + repository + "@" + digest
    run("docker", "pull", "--platform", "linux/amd64", image)
    if first != second:
        tag = second + "/" + repository + ":" + digest.split(":")[1]
        push = True
        existing = json.loads(run("az", "acr", "repository", "list", "--name", second.split(".")[0], "-o", "json"))
        if repository in existing:
            tags = json.loads(run("az", "acr", "repository", "show-tags", "--name", second.split(".")[0], "--repository", repository, "-o", "json"))
            if digest.split(":")[1] in tags:
                require(image_digest(second, repository, ":" + digest.split(":")[1]) == digest,
                        "Destination immutable image tag already names different bytes.")
                push = False
        if push:
            run("docker", "tag", image, tag)
            run("docker", "push", tag)
        require(image_digest(second, repository, ":" + digest.split(":")[1]) == digest,
                "Pushed destination tag changed the manifest digest.")
    require(image_digest(second, repository, "@" + digest) == digest, "Registry transfer changed the manifest digest.")
    return second + "/" + repository + "@" + digest
