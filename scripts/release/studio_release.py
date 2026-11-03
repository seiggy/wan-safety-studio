"""Shared preflight and reporting for the standalone release scripts (scripts/release/*.sh and *.ps1).

Standard library only, so it runs before the pipeline virtual environment exists. Each subcommand prints KEY=VALUE lines
on stdout for the calling script and explains every refusal on stderr with exit code 1 (2 for a usage error).
It never prints a token or the settings document.
"""
from __future__ import annotations

import argparse
import base64
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
from urllib.parse import urlsplit
import uuid

ROOT = Path(__file__).resolve().parents[2]
REGION = re.compile(r"^[a-z][a-z0-9]+$")
ARTIFACT = re.compile(r"^[a-f0-9]{64}$")
PRIVATE = [ipaddress.ip_network(value) for value in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")]
APPROVED_FEEDS = ("packagefeedproxy.microsoft.io", "pkgs.dev.azure.com")
ENGINES = ("docker", "podman", "wslc")


class Refused(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Refused(message)


def is_guid(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value.lower()
    except ValueError:
        return False


def inside_checkout(path):
    resolved = Path(path).resolve()
    return resolved == ROOT or ROOT in resolved.parents


def load_json(text):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"The settings document has a duplicate property: {key}.")
            result[key] = value
        return result
    try:
        value = json.loads(text, object_pairs_hook=unique)
    except json.JSONDecodeError as error:
        raise Refused(f"The settings document is not valid JSON: {error.msg} (line {error.lineno}).") from None
    require(isinstance(value, dict), "The settings document must be a JSON object.")
    return value


def read_settings(path):
    return load_json(Path(path).read_text(encoding="utf-8-sig"))


def registry_host(region):
    return region["Platform"]["RegistryId"].rsplit("/", 1)[1].lower() + ".azurecr.io"


def blob_host(region):
    return region["Storage"]["AccountId"].rsplit("/", 1)[1].lower() + ".blob.core.windows.net"


def check_settings(settings, stage, regions):
    """Structure the stage needs, checked before any login or download. The contract tool checks the full contract later."""
    studio, pipeline = settings.get("Studio"), settings.get("Pipeline")
    require(isinstance(studio, dict), "The settings document has no Studio section.")
    require(isinstance(pipeline, dict), "The settings document has no Pipeline section (the pipelines' own settings).")
    require(is_guid(studio.get("TenantId")), "Studio.TenantId must be the tenant GUID.")
    require(is_guid(studio.get("SubscriptionId")), "Studio.SubscriptionId must be the subscription GUID.")
    configured = studio.get("Regions")
    require(isinstance(configured, dict) and configured, "Studio.Regions must list at least one region.")
    for key, region in configured.items():
        require(REGION.match(key), f"Region key {key} is invalid; use the lowercase Azure region name, such as eastus2.")
        for path in (("Platform", "RegistryId"), ("Storage", "AccountId")):
            value = (region.get(path[0]) or {}).get(path[1]) if isinstance(region, dict) else None
            require(isinstance(value, str) and value.count("/") >= 8,
                    f"Studio.Regions.{key}.{'.'.join(path)} must be the resource ID.")
    control = studio.get("ControlRegion")
    require(control in configured, "Studio.ControlRegion must be one of the keys under Studio.Regions.")
    if stage in ("build", "promote"):
        require(is_guid(pipeline.get("PublisherPrincipalId")),
                "Pipeline.PublisherPrincipalId must be the object ID (GUID) of the identity you sign in to Azure CLI as.")
    if stage == "deploy":
        site = pipeline.get("SiteId")
        require(isinstance(site, str) and "/providers/microsoft.web/sites/" in site.lower(),
                "Pipeline.SiteId must be the resource ID of the studio App Service.")
    if stage == "promote":
        unknown = [key for key in regions if key not in configured]
        require(not unknown, f"Unknown region(s) {', '.join(unknown)}; configured: {', '.join(sorted(configured))}.")
        selected = sorted(set(regions)) if regions else sorted(configured)
        wiring = pipeline.get("Regions") or {}
        for key in selected:
            for field in ("WorkspaceIdentityId", "KeyVaultId"):
                require(isinstance((wiring.get(key) or {}).get(field), str) and wiring[key][field],
                        f"Pipeline.Regions.{key}.{field} is required to promote to {key}.")
    else:
        require(not regions, f"The {stage} stage always uses the control region ({control}); do not pass a region.")
        selected = [control]
    return {"TENANT_ID": studio["TenantId"], "SUBSCRIPTION_ID": studio["SubscriptionId"], "CONTROL_REGION": control,
            "REGIONS": " ".join(selected), "PUBLISHER": pipeline.get("PublisherPrincipalId") or ""}


def command_settings(args):
    """Materialize the settings document into the job folder, as Write-StudioPipelineInputs.ps1 does in CI."""
    inline, cached = os.environ.get("STUDIO_SETTINGS_JSON"), os.environ.get("STUDIO_SETTINGS_PATH")
    if args.settings:
        source, text = args.settings, None
    else:
        require(not (inline and cached), "Set only one of STUDIO_SETTINGS_JSON and STUDIO_SETTINGS_PATH.")
        require(inline or cached,
                "Pass the settings file (--settings / -Settings), or set STUDIO_SETTINGS_PATH or STUDIO_SETTINGS_JSON.")
        source, text = cached, inline
    if source:
        require(Path(source).is_file(), f"Settings file not found: {source}")
        require(not inside_checkout(source),
                "Keep the settings document outside the repository checkout; it holds your tenant's resource IDs.")
        text = Path(source).read_text(encoding="utf-8-sig")
    values = check_settings(load_json(text), args.stage, args.region or [])
    out = Path(args.out)
    require(not inside_checkout(out), "The job folder must be outside the checkout.")
    out.write_text(text, encoding="utf-8")
    if os.name != "nt":
        out.chmod(0o600)
    emit(values)


def command_feed(args):
    require(args.url, "The GPU and trainer images install Python packages through your approved feed. Set PIP_INDEX_URL, "
                      "or configure pip's global index-url (pip config set global.index-url <feed>).")
    parsed = urlsplit(args.url)
    require(parsed.scheme == "https" and parsed.hostname in APPROVED_FEEDS and not parsed.username and not parsed.password
            and not parsed.query and not parsed.fragment,
            f"PIP_INDEX_URL must be one HTTPS Azure Artifacts feed ({' or '.join(APPROVED_FEEDS)}) without embedded "
            "credentials; build_image.py refuses anything else.")
    emit({"PIP_INDEX_URL": args.url})


def az(*arguments):
    program = shutil.which("az") or shutil.which("az.cmd")
    require(program, "Azure CLI (az) was not found on PATH.")
    result = subprocess.run([program, *arguments, "--only-show-errors", "-o", "json"], capture_output=True, text=True,
                            stdin=subprocess.DEVNULL)
    require(result.returncode == 0, f"az {' '.join(arguments[:2])} failed: {(result.stderr or '').strip()[-600:]}")
    return json.loads(result.stdout)


def token_claims(token):
    parts = token.split(".")
    require(len(parts) == 3, "Azure CLI returned an opaque ARM token; the caller's object ID cannot be read from it.")
    return json.loads(base64.urlsafe_b64decode(parts[1] + "=" * (-len(parts[1]) % 4)))


def command_identity(args):
    """The Azure CLI caller must be in the settings' tenant and subscription and, to build or promote, be the publisher."""
    settings = read_settings(args.settings)
    studio, pipeline = settings["Studio"], settings.get("Pipeline", {})
    account = az("account", "show")
    require(account.get("environmentName") == "AzureCloud", "Azure CLI must use the AzureCloud environment.")
    require(account["tenantId"].lower() == studio["TenantId"].lower(),
            f"Azure CLI is signed in to tenant {account['tenantId']}, but Studio.TenantId is {studio['TenantId']}.")
    require(account["id"].lower() == studio["SubscriptionId"].lower(),
            f"Azure CLI's subscription is {account['id']}, but Studio.SubscriptionId is {studio['SubscriptionId']}. "
            f"Run: az account set --subscription {studio['SubscriptionId']}")
    claims = token_claims(az("account", "get-access-token", "--resource", "https://management.azure.com/")["accessToken"])
    caller, kind = claims.get("oid", ""), account.get("user", {}).get("type", "")
    require(is_guid(caller) and claims.get("tid", "").lower() == studio["TenantId"].lower(),
            "The Azure CLI token has no object ID for the settings tenant.")
    publisher = pipeline.get("PublisherPrincipalId") or ""
    if args.stage in ("build", "promote"):
        require(caller.lower() == publisher.lower(),
                f"Azure CLI is signed in as object ID {caller} ({kind} {account.get('user', {}).get('name', '')}), but "
                f"Pipeline.PublisherPrincipalId is {publisher}. Sign in as the publisher principal (a service principal, a "
                "managed identity, or your own account if you set PublisherPrincipalId to your object ID: "
                "az ad signed-in-user show --query id -o tsv).")
    emit({"CALLER": caller, "CALLER_TYPE": kind})


def private_endpoint(host, port=443, timeout=10):
    try:
        addresses = {item[4][0] for item in socket.getaddrinfo(host, port, type=socket.SOCK_STREAM)}
    except socket.gaierror as error:
        return f"does not resolve ({error.strerror})"
    public = sorted(value for value in addresses if not any(ipaddress.ip_address(value) in net for net in PRIVATE))
    if not addresses or public:
        return f"resolves to public or unsupported address(es) {', '.join(public) or 'none'}; private DNS is not in effect"
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return None
    except OSError as error:
        return f"resolves privately ({', '.join(sorted(addresses))}) but port {port} is unreachable ({error})"


def command_network(args):
    """The same private DNS and reachability rule pipeline.private_host enforces, for every endpoint the stage uses."""
    settings = read_settings(args.settings)
    regions = settings["Studio"]["Regions"]
    control = settings["Studio"]["ControlRegion"]
    hosts = [registry_host(regions[control]), blob_host(regions[control])]
    for key in args.region or []:
        hosts += [registry_host(regions[key]), blob_host(regions[key])]
        if args.stage == "promote" and args.component == "gpu":
            hosts += list(regions[key].get("Network", {}).get("PrivateConnectivityHosts") or [])
    problems = []
    for host in dict.fromkeys(hosts):
        problem = private_endpoint(host)
        print(f"  {host}: {problem or 'private and reachable'}", file=sys.stderr)
        if problem:
            problems.append(f"{host} {problem}")
    require(not problems, "This machine cannot reach the studio's private endpoints. Run from a host on the studio's "
                          "network (VPN, peered VNet or a jump host) whose DNS resolves the private DNS zones:\n  " +
                          "\n  ".join(problems))
    emit({"HOSTS": " ".join(dict.fromkeys(hosts))})


def command_engine(args):
    """Fail before any work when the container engine is missing or lacks a feature the build needs."""
    sys.path.insert(0, str(ROOT / "build" / "gpu"))
    import containers

    # The same stripped environment scripts/ci/pipeline.py gives the engine, so the check sees what the build will see.
    base = {"PATH", "HOME", "USERPROFILE", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TMP", "TEMP", "TMPDIR", "APPDATA",
            "LOCALAPPDATA", "SSL_CERT_FILE", "SSL_CERT_DIR", *containers.SELECTION_ENV}
    environment = {key: value for key, value in os.environ.items() if key in base}
    environment.update(containers.engine_environment(args.engine))

    def run(*command, input=None):
        result = subprocess.run([str(item) for item in command], capture_output=True, text=True, input=input, env=environment,
                                stdin=None if input is not None else subprocess.DEVNULL)
        if result.returncode:
            raise RuntimeError((result.stderr or result.stdout or "").strip()[-400:])
        return result.stdout
    try:
        cli = containers.Containers(run, engine=args.engine)
        version = cli.preflight()
    except (ValueError, RuntimeError, OSError) as error:
        raise Refused(f"Container engine {args.engine} is not usable: {error}") from None
    emit({"ENGINE": args.engine, "ENGINE_CLI": cli.cli, "ENGINE_VERSION": version.splitlines()[0] if version else ""})


SCRIPTS = {
    "bash": {"build": "scripts/release/build-artifact.sh", "promote": "scripts/release/promote-release.sh",
             "deploy": "scripts/release/deploy-server.sh"},
    "pwsh": {"build": r".\scripts\release\Build-StudioArtifact.ps1", "promote": r".\scripts\release\Publish-StudioRelease.ps1",
             "deploy": r".\scripts\release\Deploy-StudioServer.ps1"},
}


def command_banner(args):
    """The final report: the artifact ID, prominently, and the next step for the shell the user runs."""
    bash = args.shell == "bash"

    def command(stage, component=None, regions=()):
        parts = [SCRIPTS[args.shell][stage]]
        parts.append(("--settings " if bash else "-Settings ") + (args.settings or "<settings.json>"))
        if component:
            parts.append(("--component " if bash else "-Component ") + component)
        parts.append(("--artifact " if bash else "-Artifact ") + args.artifact)
        if regions:
            parts.append(" ".join("--region " + key for key in regions) if bash else "-Region " + ",".join(regions))
        return "  " + " ".join(parts)

    label = {"gpu": "RELEASE ARTIFACT ID (sealed GPU build)", "server": "SERVER ARTIFACT ID",
             "yolox": "TRAINER ARTIFACT ID (yolox)"}[args.component]
    rule = "=" * 78
    ok, failed = args.ok or [], args.failed or []
    lines = ["", rule, f"  {label}", "", f"  {args.artifact}", rule]
    if args.stage == "promote":
        lines += [f"  Promoted to:      {', '.join(ok) or 'none'}", f"  Not promoted to:  {', '.join(failed) or 'none'}", rule]
    lines.append("")
    if args.stage == "build" and args.component == "gpu":
        lines += ["Copy this Release artifact ID and keep it: after promotion you paste it into Administration to select",
                  "the release in each region.",
                  "Next: promote this release (to every configured region unless you pass regions):",
                  command("promote"),
                  "Administration accepts it for a region only after it has been promoted there."]
    elif args.stage == "build" and args.component == "yolox":
        lines += ["Next: promote the trainer to your regions:", command("promote", "yolox")]
    elif args.stage == "build":
        lines += ["This is the server artifact ID, not a Release artifact ID: never paste it into Administration.",
                  "Next: deploy it to the studio App Service:", command("deploy")]
    elif args.stage == "promote" and args.component == "gpu":
        if ok:
            lines += ["Next, in the studio, signed in with the StudioAdmin role, open Administration. On the card of each",
                      "region listed under 'Promoted to' whose phase is Off, paste this ID into 'Release artifact ID'",
                      "and choose 'Select release'.",
                      "- Select release only verifies the promoted release and records it as the region's approved release.",
                      "  It starts no compute and costs nothing. A region that is not Off refuses it: Stop that region first.",
                      "- 'Start <region>', after you approve GPU spend, is the step that spends money.",
                      "- Repeat per region; the same ID is used in every region it was promoted to."]
        if failed:
            lines += ["Regions under 'Not promoted to' have no binding, so Select release fails there. Fix the error above,",
                      "then rerun for just those regions (promotion is idempotent and resumes where it stopped):",
                      command("promote", regions=failed)]
    elif args.stage == "promote":
        if ok:
            lines += ["The studio lists this trainer in the promoted regions when it next refreshes its trainer bindings.",
                      "No Administration step is needed."]
        if failed:
            lines += ["Rerun for the regions that were not promoted:", command("promote", "yolox", regions=failed)]
    else:
        lines += ["The studio App Service now runs this server image; submissions stay disarmed.",
                  "This is the server artifact ID, not a Release artifact ID: never paste it into Administration.",
                  "Next: open the site and sign in. In Administration, select a promoted Release artifact ID per region",
                  "(the promote script prints it), then Start a region when you want to generate."]
    print("\n".join(lines))


def emit(values):
    for key, value in values.items():
        print(f"{key}={value}")


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)
    stage = {"choices": ("build", "promote", "deploy"), "required": True}
    component = {"choices": ("gpu", "server", "yolox"), "default": "gpu"}

    item = commands.add_parser("settings")
    item.add_argument("--settings")
    item.add_argument("--out", required=True)
    item.add_argument("--stage", **stage)
    item.add_argument("--region", action="append")
    item.set_defaults(handler=command_settings)

    item = commands.add_parser("feed")
    item.add_argument("--url", default="")
    item.set_defaults(handler=command_feed)

    item = commands.add_parser("identity")
    item.add_argument("--settings", required=True)
    item.add_argument("--stage", **stage)
    item.set_defaults(handler=command_identity)

    item = commands.add_parser("network")
    item.add_argument("--settings", required=True)
    item.add_argument("--stage", **stage)
    item.add_argument("--component", **component)
    item.add_argument("--region", action="append")
    item.set_defaults(handler=command_network)

    item = commands.add_parser("engine")
    item.add_argument("--engine", choices=ENGINES, required=True)
    item.set_defaults(handler=command_engine)

    item = commands.add_parser("banner")
    item.add_argument("--stage", **stage)
    item.add_argument("--component", **component)
    item.add_argument("--artifact", required=True)
    item.add_argument("--shell", choices=("bash", "pwsh"), required=True)
    item.add_argument("--settings")
    item.add_argument("--ok", action="append")
    item.add_argument("--failed", action="append")
    item.set_defaults(handler=command_banner)
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        if getattr(args, "artifact", None) is not None:
            require(ARTIFACT.match(args.artifact), "An artifact ID is 64 lowercase hex characters.")
        args.handler(args)
    except Refused as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
