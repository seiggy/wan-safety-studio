"""Shared, fail-closed boundary for the operator-selected Terraform configuration."""
from __future__ import annotations

import hashlib
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
from urllib.parse import urlsplit
from uuid import UUID

PUBLIC_PYPI_INDEX = "https://pypi.org/simple"


def read_selected_json(variable: str):
    path = Path(os.environ[variable])
    if not path.is_absolute():
        raise ValueError(f"{variable} must select an absolute JSON path.")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"{variable} must contain a JSON object.")
    return value


def identifier(value):
    if not isinstance(value, str) or str(UUID(value)) != value.lower():
        raise ValueError("Expected a canonical Azure UUID.")
    return value.lower()


def load_config():
    value = read_selected_json("WAN_STUDIO_CONFIG")
    for key in ("subscription_id", "tenant_id", "operator_principal_id"):
        value[key] = identifier(value[key])
    if (not re.fullmatch(r"[a-z][a-z0-9-]{1,14}[a-z0-9]", value["deployment_name"]) or
            "--" in value["deployment_name"]):
        raise ValueError("deployment_name must be a 3..16 character lowercase DNS-safe slug.")
    if not re.fullmatch(r"[a-z][a-z0-9]+", value["location"]):
        raise ValueError("location must be an Azure region name.")
    if value["gpu_subnet_dedicated"] is not True:
        raise ValueError("A dedicated GPU subnet must be explicitly acknowledged.")
    for key, default in (("compute_enabled", False), ("manage_compute_egress", False)):
        value.setdefault(key, default)
        if type(value[key]) is not bool:
            raise ValueError(f"{key} must be a JSON boolean.")
    value.setdefault("max_payg_hourly_usd", 4)
    price = value["max_payg_hourly_usd"]
    if type(price) not in (int, float) or not math.isfinite(price) or price <= 0:
        raise ValueError("max_payg_hourly_usd must be finite and positive.")
    network = ipaddress.ip_network(value["gpu_subnet_cidr"], strict=True)
    if network.version != 4:
        raise ValueError("gpu_subnet_cidr must be an IPv4 network.")
    for key in ("gpu_subnet_id", "private_endpoint_subnet_id"):
        resource_id(value[key], "Microsoft.Network", "virtualNetworks", child="subnets")
        require_subscription(value[key], value["subscription_id"])
    if value["gpu_subnet_id"].lower() == value["private_endpoint_subnet_id"].lower():
        raise ValueError("GPU and private endpoint subnets must be separate.")
    dns = value["private_dns_zone_ids"]
    if not isinstance(dns, dict) or set(dns) != {"blob", "file", "vault", "registry", "api", "notebooks"}:
        raise ValueError("Exactly the six private DNS zone IDs are required.")
    zone_names = {
        "blob": "privatelink.blob.core.windows.net", "file": "privatelink.file.core.windows.net",
        "vault": "privatelink.vaultcore.azure.net", "registry": "privatelink.azurecr.io",
        "api": "privatelink.api.azureml.ms", "notebooks": "privatelink.notebooks.azure.net",
    }
    for key, zone in dns.items():
        resource_id(zone, "Microsoft.Network", "privateDnsZones")
        require_subscription(zone, value["subscription_id"])
        if zone.rsplit("/", 1)[1].lower() != zone_names[key]:
            raise ValueError("Private DNS zone name differs from the required Azure service zone.")
    resource_id(value["log_analytics_workspace_id"], "Microsoft.OperationalInsights", "workspaces")
    require_subscription(value["log_analytics_workspace_id"], value["subscription_id"])
    return value


def resource_id(value, provider, kind, *, child=None):
    pattern = (r"/subscriptions/([a-f0-9-]{36})/resourceGroups/[^/]+/providers/"
               + re.escape(provider) + "/" + re.escape(kind) + r"/[^/]+")
    if child:
        pattern += "/" + re.escape(child) + r"/[^/]+"
    if not isinstance(value, str) or not re.fullmatch(pattern, value, re.IGNORECASE):
        raise ValueError("Unexpected Azure resource ID.")
    identifier(value.split("/")[2])


def require_subscription(resource, subscription):
    if resource.split("/")[2].lower() != subscription:
        raise ValueError("Landing-zone resources must belong to the selected subscription.")


def load_foundation():
    config = load_config()
    value = read_selected_json("WAN_STUDIO_FOUNDATION")
    for output, setting in (
        ("subscriptionId", "subscription_id"), ("tenantId", "tenant_id"),
        ("deploymentName", "deployment_name"), ("location", "location"),
        ("gpuSubnetId", "gpu_subnet_id"), ("gpuSubnetCidr", "gpu_subnet_cidr"),
        ("privateEndpointSubnetId", "private_endpoint_subnet_id"),
        ("manageComputeEgress", "manage_compute_egress"), ("maxPaygHourlyUsd", "max_payg_hourly_usd"),
    ):
        if value[output] != config[setting]:
            raise ValueError("Foundation outputs differ from the selected configuration; refresh them.")
    if (type(value["maxJobSeconds"]) is not int or value["maxJobSeconds"] != 7200 or
            type(value["instanceCount"]) is not int or value["instanceCount"] != 1):
        raise ValueError("Foundation must retain the two-hour, single-instance job limits.")
    if type(value["computeEnabled"]) is not bool or type(value["manageComputeEgress"]) is not bool:
        raise ValueError("Foundation compute/egress switches must be booleans.")
    if type(value["maxPaygHourlyUsd"]) not in (int, float):
        raise ValueError("Foundation hourly price ceiling must be numeric.")
    for key in ("resourceGroupName", "workspaceName", "computeName", "storageAccountName",
                "registryName", "keyVaultName", "containerName", "datastoreName"):
        if not isinstance(value[key], str) or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_.-]*", value[key]):
            raise ValueError("Foundation contains an invalid resource name.")
    prefix = f"/subscriptions/{value['subscriptionId']}/resourceGroups/{value['resourceGroupName']}/providers/"
    expected = {
        "workspaceId": prefix + f"Microsoft.MachineLearningServices/workspaces/{value['workspaceName']}",
        "storageAccountId": prefix + f"Microsoft.Storage/storageAccounts/{value['storageAccountName']}",
        "keyVaultId": prefix + f"Microsoft.KeyVault/vaults/{value['keyVaultName']}",
    }
    expected["computeId"] = expected["workspaceId"] + f"/computes/{value['computeName']}"
    for key, resource in expected.items():
        if not isinstance(value[key], str) or value[key].lower() != resource.lower():
            raise ValueError("Foundation resource IDs differ from the selected workspace scope.")
    for key in ("workspaceIdentityId", "computeIdentityId"):
        resource_id(value[key], "Microsoft.ManagedIdentity", "userAssignedIdentities")
        if not value[key].lower().startswith(prefix.lower()):
            raise ValueError("Foundation identity is outside the selected resource group.")
    identifier(value["computeIdentityClientId"])
    if value["registryLoginServer"] != value["registryName"] + ".azurecr.io":
        raise ValueError("Foundation registry endpoint differs from its registry name.")
    if value["ownershipTags"] != {
        "deployment": config["deployment_name"], "application": "wan-safety-studio", "managedBy": "terraform",
    }:
        raise ValueError("Foundation ownership tags differ from the selected studio deployment.")
    return value


def scope_fingerprint():
    config = load_config()
    foundation = load_foundation()
    # Start/Stop can change the compute switch without changing any prepared asset.
    config.pop("compute_enabled", None)
    foundation.pop("computeEnabled", None)
    payload = json.dumps({"config": config, "foundation": foundation}, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode()).hexdigest()


def package_index_url():
    value = os.environ.get("WAN_STUDIO_PYPI_INDEX", PUBLIC_PYPI_INDEX)
    parts = urlsplit(value)
    if (parts.scheme != "https" or not parts.hostname or parts.username or parts.password or
            parts.query or parts.fragment or
            not re.fullmatch(r"https://[a-zA-Z0-9.-]+(?::[0-9]+)?/[a-zA-Z0-9._~/-]*", value)):
        raise ValueError("Package index must be an explicit HTTPS URL without credentials or query parameters.")
    return value.rstrip("/")


def assert_cli_scope():
    config = load_config()
    executable = shutil.which("az")
    if executable is None:
        raise FileNotFoundError("Azure CLI is required.")
    result = subprocess.run(
        [executable, "account", "show", "--output", "json", "--only-show-errors"],
        check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    account = json.loads(result.stdout)
    if (identifier(account["id"]), identifier(account["tenantId"])) != (
            identifier(config["subscription_id"]), identifier(config["tenant_id"])):
        raise ValueError("Azure CLI tenant/subscription differ from the selected configuration.")


def build_credential():
    assert_cli_scope()
    from azure.identity import AzureCliCredential
    return AzureCliCredential(tenant_id=load_config()["tenant_id"])
