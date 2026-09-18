"""Local adaptation copied into the pinned upstream azureml package."""
from __future__ import annotations

import json
import re
from datetime import timedelta
from decimal import Decimal

try:
    from .config import assert_cli_scope, load_foundation, read_selected_json, scope_fingerprint
except ImportError:
    from config import assert_cli_scope, load_foundation, read_selected_json, scope_fingerprint

UPSTREAM_SHA = "dc0d29031b73a5ba7376d910adb7a58c206f32b1"
MAX_SECONDS = 7200


class ServerJobSafetyError(RuntimeError):
    pass


def public_error(error):
    return f"{type(error).__name__}: operation failed; inspect Status and run Stop after a generation failure. No automatic retry."


def validate_scope(args):
    foundation = load_foundation()
    if (args.subscription_id, args.resource_group, args.workspace_name) != (
        foundation["subscriptionId"], foundation["resourceGroupName"], foundation["workspaceName"]
    ):
        raise ValueError("Only the selected studio workspace is permitted.")
    assert_cli_scope()
    return foundation


def job_controls(args):
    """Validate before either upstream command() factory and API submission."""
    from azure.ai.ml.entities import CommandJobLimits, JobResourceConfiguration, ManagedIdentityConfiguration

    foundation = validate_scope(args)
    contract = read_selected_json("WAN_STUDIO_CONTRACT")
    gate = read_selected_json("WAN_STUDIO_GATE")
    if contract["scopeFingerprint"] != scope_fingerprint():
        raise ValueError("Selected deployment changed; Prepare must complete again.")
    if (gate["compute"], gate["profile"], gate["version"]) != (
            foundation["computeName"], contract["profile"], contract["version"]):
        raise ValueError("Start must arm this prepared profile; Stop disables all local submissions.")
    timeout = getattr(args, "timeout_seconds", MAX_SECONDS)
    if isinstance(timeout, bool) or not isinstance(timeout, int) or not 1 <= timeout <= MAX_SECONDS:
        raise ValueError("AzureML server timeout must be 1..7200 seconds.")
    if args.compute != foundation["computeName"] or contract["sourceSha"] != UPSTREAM_SHA:
        raise ValueError("Unapproved compute or source revision.")
    if args.profile != contract["profile"] or args.workflow != contract["workflow"]:
        raise ValueError("Only the prepared workflow/profile can run.")
    for field, key in (("environment_id", "environmentId"), ("models_path", "modelsRef"), ("code_path", "codeUri")):
        if getattr(args, field, None) != contract[key]:
            raise ValueError(f"Unprepared {field}; rerun Prepare before spending.")
    output = getattr(args, "generated_output_path", None)
    output_prefix = f"azureml://datastores/{foundation['datastoreName']}/paths/video-library/"
    if (not isinstance(output, str) or not output.startswith(output_prefix) or
            not output[len(output_prefix):].strip("/") or
            any(part in output for part in ("?", "#", "..", "\\", "%"))):
        raise ValueError("Job output must use the selected private datastore video-library namespace.")
    if getattr(args, "install_runtime_deps", False):
        raise ValueError("Dependencies must be built on CPU in Prepare, not installed on paid GPU.")
    if type(getattr(args, "batch_size", 1)) is not int or getattr(args, "batch_size", 1) != 1:
        raise ValueError("Only one video per job is permitted.")
    for key in ("input_image", "start_image", "end_image"):
        image = getattr(args, f"{key}_url", None)
        prefix = f"azureml://datastores/{foundation['datastoreName']}/paths/{foundation['deploymentName']}/web-inputs/"
        if image and (not isinstance(image, str) or not image.startswith(prefix) or
                      any(part in image for part in ("?", "#", "..", "\\", "%"))):
            raise ValueError("Images must use private datastore file inputs, not signed URLs.")
    if contract["computeIdentityClientId"] != foundation["computeIdentityClientId"]:
        raise ValueError("Prepared compute identity changed.")
    return {
        "limits": CommandJobLimits(timeout=timeout),
        "resources": JobResourceConfiguration(instance_count=1),
        "identity": ManagedIdentityConfiguration(client_id=contract["computeIdentityClientId"]),
        "environment_variables": {},
    }


def normalize_timeout(value):
    """Normalize SDK seconds and ARM ISO8601 day/time durations without assuming a missing limit."""
    if isinstance(value, bool):
        raise ValueError("Boolean is not a server timeout.")
    if isinstance(value, timedelta):
        seconds = Decimal(str(value.total_seconds()))
    elif isinstance(value, (int, float, Decimal)):
        seconds = Decimal(str(value))
    elif isinstance(value, str):
        text = value.strip()
        if re.fullmatch(r"\d+(?:\.\d+)?", text):
            seconds = Decimal(text)
        else:
            match = re.fullmatch(
                r"P(?:(?P<days>\d+)D)?(?:T(?:(?P<hours>\d+)H)?"
                r"(?:(?P<minutes>\d+)M)?(?:(?P<seconds>\d+(?:\.\d+)?)S)?)?", text
            )
            if not match or not any(part is not None for part in match.groupdict().values()):
                raise ValueError("Unrecognized server timeout; refusing to assume a limit.")
            seconds = sum(
                Decimal(match.group(name) or "0") * scale
                for name, scale in (("days", 86400), ("hours", 3600), ("minutes", 60), ("seconds", 1))
            )
    else:
        raise ValueError("Server timeout is missing or unrecognized.")
    if not seconds.is_finite() or seconds <= 0:
        raise ValueError("Server timeout must be finite and positive.")
    return seconds


def server_field(value, *names):
    for name in names:
        if isinstance(value, dict):
            if name in value:
                return value[name]
        if hasattr(value, name):
            return getattr(value, name)
    return None


def verify_server_controls(job, args):
    foundation = load_foundation()
    properties = job.get("properties", job) if isinstance(job, dict) else job
    timeout = normalize_timeout(server_field(server_field(properties, "limits"), "timeout"))
    requested_timeout = normalize_timeout(getattr(args, "timeout_seconds", MAX_SECONDS))
    if timeout > min(Decimal(MAX_SECONDS), requested_timeout):
        raise ValueError("Server timeout exceeds the approved/requested maximum.")
    count = server_field(server_field(properties, "resources"), "instance_count", "instanceCount")
    if type(count) is not int or count != 1:
        raise ValueError("Server instance count is missing or is not exactly one.")
    compute = server_field(properties, "compute", "computeId")
    if not isinstance(compute, str) or compute.lower() not in (
            foundation["computeName"].lower(), foundation["computeId"].lower()):
        raise ValueError("Server job is not assigned to the selected studio compute.")


def verify_created_job(client, created_job, args):
    """Read the persisted server job; cancel once if limits cannot be proven, without resubmitting."""
    name = created_job.name
    try:
        server_job = client.jobs.get(name)
        if server_field(server_job, "name") != name:
            raise ValueError("Server returned a different job.")
        verify_server_controls(server_job, args)
        return server_job
    except Exception:
        status = "ServerSafetyVerificationFailed-CancellationRequested"
        try:
            client.jobs.cancel(name)
        except Exception:
            status = "ServerSafetyVerificationFailed-CancellationFailed-RunStop"
        print(json.dumps({"name": name, "status": status}), flush=True)
        raise ServerJobSafetyError("Server job safety could not be proven; run Stop to verify compute/NAT release.") from None
