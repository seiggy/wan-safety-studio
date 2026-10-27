"""Read-only legacy state assessment. Emits exact commands only after fresh verified-OFF reads; never executes them."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import ipaddress
import json
from pathlib import Path
import re
import shutil
import subprocess
import urllib.error
import urllib.parse
import urllib.request
import uuid


def require(condition, message):
    if not condition:
        raise ValueError(message)


def identifier(value):
    require(isinstance(value, str), "An explicit canonical UUID is required.")
    parsed = uuid.UUID(value)
    require(str(parsed) == value and parsed.int != 0, "An explicit canonical nonempty UUID is required.")
    return value


def load(path):
    def unique(pairs):
        value = {}
        for key, child in pairs:
            require(key not in value, "Duplicate JSON property in migration input.")
            value[key] = child
        return value
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def context(config, receipt):
    subscription = identifier(config["subscription_id"])
    tenant = identifier(config["tenant_id"])
    identifier(config["operator_principal_id"])
    deployment = config["deployment_name"]
    require(re.fullmatch(r"[a-z][a-z0-9-]{1,14}[a-z0-9]", deployment) and "--" not in deployment, "Invalid deployment name.")
    region = config["legacy_region"]
    require(isinstance(region, str) and re.fullmatch(r"[a-z][a-z0-9]+", region) and
            config["control_region"] == region and region in config["regions"], "Migration needs the explicit existing legacy/control region.")
    chosen = config["regions"][region]
    require(chosen["gpu_subnet_dedicated"] is True and not chosen.get("compute_enabled", False), "Migration requires dedicated, disabled compute.")
    require((receipt["subscriptionId"], receipt["tenantId"], receipt["deploymentName"], receipt["location"]) ==
            (subscription, tenant, deployment, region), "Cached receipt differs from the explicit customer scope.")
    suffix = hashlib.sha256(f"{subscription}:{deployment}".encode()).hexdigest()[:8]
    stem = f"{deployment}-{suffix}"
    short = deployment[:12].rstrip("-")
    group = f"/subscriptions/{subscription}/resourceGroups/rg-{stem}"
    prefix = group + "/providers"
    workspace = prefix + f"/Microsoft.MachineLearningServices/workspaces/mlw-{stem}"
    ids = {
        "group": group, "workspace": workspace, "compute": workspace + "/computes/wan-gpu",
        "storage": prefix + f"/Microsoft.Storage/storageAccounts/st{short.replace('-', '')}{suffix}",
        "registry": prefix + f"/Microsoft.ContainerRegistry/registries/cr{deployment.replace('-', '')}{suffix}",
        "vault": prefix + f"/Microsoft.KeyVault/vaults/kv-{short}-{suffix}",
        "workspace_identity": prefix + f"/Microsoft.ManagedIdentity/userAssignedIdentities/id-{stem}-workspace",
        "compute_identity": prefix + f"/Microsoft.ManagedIdentity/userAssignedIdentities/id-{stem}-gpu",
        "insights": prefix + f"/Microsoft.Insights/components/appi-{stem}",
        "nsg": chosen.get("existing_gpu_nsg_id") or prefix + f"/Microsoft.Network/networkSecurityGroups/nsg-{stem}-gpu",
        "nat": prefix + f"/Microsoft.Network/natGateways/nat-{stem}",
        "pip": prefix + f"/Microsoft.Network/publicIPAddresses/pip-{stem}-egress",
    }
    for field, key in {
        "workspaceId": "workspace", "computeId": "compute", "storageAccountId": "storage",
        "keyVaultId": "vault", "workspaceIdentityId": "workspace_identity", "computeIdentityId": "compute_identity",
        "networkSecurityGroupId": "nsg", "natGatewayId": "nat", "publicIpId": "pip",
    }.items():
        require(receipt[field].lower() == ids[key].lower(), "Receipt ID differs from the deterministic legacy deployment.")
    for field, expected in {
        "resourceGroupName": f"rg-{stem}", "workspaceName": f"mlw-{stem}", "computeName": "wan-gpu",
        "registryName": f"cr{deployment.replace('-', '')}{suffix}",
        "registryLoginServer": f"cr{deployment.replace('-', '')}{suffix}.azurecr.io",
        "storageAccountName": f"st{short.replace('-', '')}{suffix}", "keyVaultName": f"kv-{short}-{suffix}",
        "containerName": "wan-studio", "datastoreName": "wan_blob",
        "releaseContainerName": "wan-release", "releaseDatastoreName": "wan_release",
    }.items():
        require(receipt[field] == expected, "Receipt names differ from the selected legacy deployment.")
    for field, key in (("gpuSubnetId", "gpu_subnet_id"), ("privateEndpointSubnetId", "private_endpoint_subnet_id"),
                       ("gpuSubnetCidr", "gpu_subnet_cidr")):
        require(receipt[field].lower() == chosen[key].lower(), "Migration must preserve the explicit legacy landing-zone selection.")
    for key in ("gpu_subnet_id", "private_endpoint_subnet_id"):
        require(re.fullmatch(rf"/subscriptions/{subscription}/resourceGroups/[^/?#%\\\r\n]+/providers/Microsoft\.Network/virtualNetworks/[^/?#%\\\r\n]+/subnets/[^/?#%\\\r\n]+",
                             chosen[key], re.IGNORECASE), "Unexpected or cross-subscription landing-zone subnet.")
    require(chosen["gpu_subnet_id"].lower() != chosen["private_endpoint_subnet_id"].lower(), "GPU/PE subnets must differ.")
    require(str(ipaddress.IPv4Network(chosen["gpu_subnet_cidr"], strict=True)) == chosen["gpu_subnet_cidr"], "Invalid dedicated subnet CIDR.")
    require(receipt["ownershipTags"] == {"application": "wan-safety-studio", "deployment": deployment, "managedBy": "terraform"},
            "Legacy receipt ownership is not the selected Terraform deployment.")
    identifier(receipt["computeIdentityClientId"])
    require((receipt.get("portal") is not None) == (config.get("portal") is not None),
            "Preserve the existing shared-host choice explicitly; migration must not remove or invent it.")
    if receipt.get("portal"):
        ids["portal"] = prefix + f"/Microsoft.Web/sites/app-{stem}"
        require(receipt["portal"]["id"].lower() == ids["portal"].lower(), "Cached host differs from the selected legacy deployment.")
        identifier(receipt["portal"]["identityClientId"])
        identifier(receipt["portal"]["identityPrincipalId"])
    return region, chosen, ids


def state_resources(state):
    require(state.get("version") == 4 and type(state.get("serial")) is int and state["serial"] >= 0 and
            isinstance(state.get("lineage"), str) and isinstance(state.get("outputs"), dict) and
            isinstance(state.get("terraform_version"), str) and state["terraform_version"].startswith("1."),
            "Supply a version-4 terraform state pull backup, not show -json or a plan.")
    identifier(state["lineage"])
    require(isinstance(state.get("resources"), list), "Malformed state resource inventory.")
    result = {}
    for resource in state["resources"]:
        require(resource["mode"] in ("managed", "data"), "Unexpected state resource mode.")
        if resource["mode"] != "managed":
            continue
        require(resource.get("provider", "").endswith(('provider["registry.terraform.io/hashicorp/azurerm"]',
                                                      'provider["registry.terraform.io/azure/azapi"]',
                                                      'provider["terraform.io/builtin/terraform"]')), "Unexpected state provider binding.")
        base = ".".join(part for part in (resource.get("module"), resource["type"], resource["name"]) if part)
        for instance in resource["instances"]:
            require(not instance.get("deposed"), "Deposed/replacement instances require manual state review.")
            key = "[" + json.dumps(instance["index_key"], separators=(",", ":")) + "]" if "index_key" in instance else ""
            address = base + key
            require(address not in result, "Duplicate state address.")
            result[address] = instance["attributes"]
    return result


def expected_resources(config, receipt, records, region, chosen, ids):
    prefix = f'module.region["{region}"].'
    def attributes(address):
        found = [records[key] for key in (address, prefix + address) if key in records]
        require(len(found) == 1, "Legacy identity state is missing or duplicated; review it before mapping grants.")
        return found[0]
    workspace_principal = identifier(attributes("azurerm_user_assigned_identity.workspace")["principal_id"])
    compute_state = attributes("azurerm_user_assigned_identity.compute")
    compute_principal = identifier(compute_state["principal_id"])
    require(compute_state["client_id"] == receipt["computeIdentityClientId"], "State compute client ID differs from the cached scope.")
    expected = {
        "azurerm_resource_group.studio": ids["group"], "azurerm_user_assigned_identity.workspace": ids["workspace_identity"],
        "azurerm_user_assigned_identity.compute": ids["compute_identity"], "azapi_resource.storage": ids["storage"],
        "azapi_resource.container": ids["storage"] + "/blobServices/default/containers/wan-studio",
        "azapi_resource.release_container": ids["storage"] + "/blobServices/default/containers/wan-release",
        "azurerm_container_registry.studio": ids["registry"], "azurerm_key_vault.studio": ids["vault"],
        "azurerm_application_insights.studio": ids["insights"], "azapi_resource.workspace": ids["workspace"],
        "azapi_resource.datastore": ids["workspace"] + "/datastores/wan_blob",
        "azapi_resource.release_datastore": ids["workspace"] + "/datastores/wan_release",
    }
    for key in ("blob", "file", "vault", "registry", "workspace"):
        address = "azurerm_private_endpoint.workspace" if key == "workspace" else f'azurerm_private_endpoint.service["{key}"]'
        expected[address] = ids["group"] + f"/providers/Microsoft.Network/privateEndpoints/pe-{receipt['deploymentName']}-{ids['group'].rsplit('-', 1)[1]}-{key}"
    if chosen.get("existing_gpu_nsg_id") is None:
        expected["azurerm_network_security_group.compute[0]"] = ids["nsg"]
        expected["azurerm_subnet_network_security_group_association.compute[0]"] = chosen["gpu_subnet_id"]
    contributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe"
    reader = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1"
    pull = "7f951dda-4ed3-4680-a7ca-43fe172d538d"
    grants = {
        "service": {
            "workspace_storage": (ids["storage"], workspace_principal, contributor),
            "workspace_vault": (ids["vault"], workspace_principal, "b86a8fe4-44ce-4948-aee5-eccb2c155cd7"),
            "workspace_registry": (ids["registry"], workspace_principal, pull),
            "compute_storage": (ids["storage"], compute_principal, contributor),
            "compute_release_reader": (expected["azapi_resource.release_container"], compute_principal, reader),
            "compute_registry": (ids["registry"], compute_principal, pull),
        },
        "operator": {
            "storage_studio": (expected["azapi_resource.container"], config["operator_principal_id"], contributor),
            "storage_release": (expected["azapi_resource.release_container"], config["operator_principal_id"], contributor),
            "registry": (ids["registry"], config["operator_principal_id"], "8311e382-0749-4cb8-b61a-304f252e45ec"),
            "workspace": (ids["workspace"], config["operator_principal_id"], "f6c7c914-8db3-469d-8ca1-694a8f32e121"),
        },
    }
    grant_values = {}
    for group, entries in grants.items():
        for name, (scope, principal, role) in entries.items():
            address = f'azurerm_role_assignment.{group}["{name}"]'
            guid = str(uuid.uuid5(uuid.NAMESPACE_URL, f"{scope}/{principal}/{role}".lower()))
            expected[address] = scope + "/providers/Microsoft.Authorization/roleAssignments/" + guid
            grant_values[address] = (scope, principal, f"/subscriptions/{config['subscription_id']}/providers/Microsoft.Authorization/roleDefinitions/{role}")
    return expected, grant_values


def verify_off(read, receipt, chosen, ids):
    def owned(resource, expected):
        require(resource and resource.get("id", "").lower() == expected.lower() and
                all(resource.get("tags", {}).get(key) == value for key, value in receipt["ownershipTags"].items()),
                "Fresh ARM resource identity/ownership differs from the selected deployment.")
    owned(read(ids["group"], "2022-09-01"), ids["group"])
    owned(read(ids["workspace"], "2024-10-01"), ids["workspace"])
    if "portal" in ids:
        site = read(ids["portal"], "2023-12-01")
        owned(site, ids["portal"])
        require(site.get("properties", {}).get("state") == "Stopped",
                "Stop the shared host in the approved maintenance window; a changed app setting alone is not a runtime submission fence.")
    for name, api in (("compute", "2024-10-01"), ("nat", "2024-05-01"), ("pip", "2024-05-01")):
        require(read(ids[name], api) is None, "Compute/NAT/PIP is still present or release cannot be proven.")
    subnet = read(chosen["gpu_subnet_id"], "2024-05-01")
    require(subnet and subnet.get("id", "").lower() == chosen["gpu_subnet_id"].lower(), "Unexpected/missing dedicated subnet.")
    properties = subnet["properties"]
    prefixes = properties.get("addressPrefixes") or [properties.get("addressPrefix")]
    require(prefixes == [chosen["gpu_subnet_cidr"]] and properties.get("defaultOutboundAccess") is False and
            not properties.get("delegations") and not properties.get("natGateway"), "GPU subnet is not verified detached/private/dedicated.")
    attached = properties.get("networkSecurityGroup", {}).get("id")
    require((attached is None and chosen.get("existing_gpu_nsg_id") is None) or
            isinstance(attached, str) and attached.lower() == ids["nsg"].lower(), "GPU subnet NSG differs from the selected owner.")
    url = ids["workspace"] + "/jobs?api-version=2024-10-01"
    visited = set()
    while url:
        require(url not in visited, "Repeated job inventory continuation.")
        visited.add(url)
        page = read(url, None)
        require(page and isinstance(page.get("value"), list), "Job inventory is missing or malformed.")
        for job in page["value"]:
            values = job.get("properties", {})
            compute = values.get("computeId")
            require(isinstance(compute, str), "Job compute scope is unknown; OFF cannot be proven.")
            if compute.lower() in (ids["compute"].lower(), "wan-gpu"):
                require(values.get("status") in ("Completed", "Failed", "Canceled", "Cancelled"), "A legacy job is nonterminal.")
        url = page.get("nextLink")
        if url:
            require(isinstance(url, str) and url.lower().startswith(("https://management.azure.com" + ids["workspace"] + "/jobs?").lower()),
                    "Job continuation escaped the selected workspace.")


def assess(config, receipt, state, read=None):
    region, chosen, ids = context(config, receipt)
    records = state_resources(state)
    expected, grants = expected_resources(config, receipt, records, region, chosen, ids)
    module = f'module.region["{region}"].'
    aliases = {"azurerm_network_security_group.compute": "azurerm_network_security_group.compute[0]",
               "azurerm_subnet_network_security_group_association.compute": "azurerm_subnet_network_security_group_association.compute[0]"}
    ephemeral = {
        "azapi_resource.compute[0]": ids["compute"], "azurerm_nat_gateway.compute[0]": ids["nat"],
        "azurerm_public_ip.compute[0]": ids["pip"],
        "azurerm_subnet_nat_gateway_association.compute[0]": chosen["gpu_subnet_id"],
        "azurerm_nat_gateway_public_ip_association.compute[0]": ids["nat"] + "|" + ids["pip"],
    }
    shared = {
        "azurerm_user_assigned_identity.portal[0]": ids["group"] + f"/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-{receipt['deploymentName']}-{ids['group'].rsplit('-',1)[1]}-portal",
        "azurerm_service_plan.portal[0]": ids["group"] + f"/providers/Microsoft.Web/serverFarms/asp-{receipt['deploymentName']}-{ids['group'].rsplit('-',1)[1]}",
        "azurerm_linux_web_app.portal[0]": ids["group"] + f"/providers/Microsoft.Web/sites/app-{receipt['deploymentName']}-{ids['group'].rsplit('-',1)[1]}",
        "azurerm_private_endpoint.portal[0]": ids["group"] + f"/providers/Microsoft.Network/privateEndpoints/pe-{receipt['deploymentName']}-{ids['group'].rsplit('-',1)[1]}-portal",
    }
    role_guid = str(uuid.uuid5(uuid.NAMESPACE_URL, (ids["workspace"] + "/wan-portal-job-submitter").lower()))
    shared["azurerm_role_definition.portal_job_submitter[0]"] = (
        f"/subscriptions/{config['subscription_id']}/providers/Microsoft.Authorization/roleDefinitions/{role_guid}|{ids['workspace']}")
    moves, absent, retained, destinations = [], [], [], set()
    for address, attributes in records.items():
        if address in ephemeral:
            require(attributes["id"].lower() == ephemeral[address].lower(), "Ephemeral state address belongs to an unexpected ID.")
            absent.append({"address": address, "id": ephemeral[address]})
            continue
        if address in shared:
            require(config.get("portal") is not None and receipt.get("portal") is not None,
                    "Partial host state lacks a complete preserved portal configuration/receipt; review it manually.")
            require(attributes["id"].lower() == shared[address].lower(), "Shared-host state identity differs; it must not move.")
            if address == "azurerm_service_plan.portal[0]":
                require(attributes["sku_name"] == config["portal"].get("sku", "B1"), "Preserve the current host SKU explicitly.")
            if address == "azurerm_linux_web_app.portal[0]":
                require(attributes["virtual_network_subnet_id"].lower() == config["portal"]["subnet_id"].lower(),
                        "Preserve the host integration subnet.")
            if address == "azurerm_user_assigned_identity.portal[0]":
                require(attributes["principal_id"] == receipt["portal"]["identityPrincipalId"] and
                        attributes["client_id"] == receipt["portal"]["identityClientId"], "Host identity state differs from its cached receipt.")
            if address == "azurerm_private_endpoint.portal[0]":
                groups = attributes.get("private_dns_zone_group")
                require(attributes.get("subnet_id", "").lower() == chosen["private_endpoint_subnet_id"].lower() and
                        isinstance(groups, list) and len(groups) == 1 and
                        [value.lower() for value in groups[0].get("private_dns_zone_ids", [])] == [config["portal"]["dns_zone_id"].lower()],
                        "Preserve the shared host's private endpoint subnet/DNS.")
            retained.append(address)
            continue
        if address.startswith('azurerm_role_assignment.portal["'):
            require("azurerm_user_assigned_identity.portal[0]" in records, "Host grant has no validated identity state.")
            key_match = re.fullmatch(r'azurerm_role_assignment\.portal\["(storage_studio|workspace|registry|agent|key_vault)"\]', address)
            require(key_match is not None, "Unknown host role state address.")
            scopes = {"storage_studio": (expected["azapi_resource.container"], "ba92f5b4-2d11-453d-a403-e96b0029c9fe"),
                      "workspace": (ids["workspace"], role_guid), "registry": (ids["registry"], "7f951dda-4ed3-4680-a7ca-43fe172d538d")}
            if key_match[1] == "agent":
                scopes["agent"] = (config["agent"]["resource_id"], "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd")
            if key_match[1] == "key_vault":
                scopes["key_vault"] = (config["key_vault_id"], "4633458b-17de-408a-b874-0445c86b69e6")
            scope, role = scopes[key_match[1]]
            principal = identifier(records["azurerm_user_assigned_identity.portal[0]"]["principal_id"])
            guid = str(uuid.uuid5(uuid.NAMESPACE_URL, f"{scope}/{principal}/{role}".lower()))
            require(attributes["id"].lower() == (scope + "/providers/Microsoft.Authorization/roleAssignments/" + guid).lower(),
                    "Shared-host role has unexpected scope/identity; do not replace it.")
            retained.append(address)
            continue
        original = address[len(module):] if address.startswith(module) else address
        original = aliases.get(original, original)
        destination = module + original
        require(destination not in destinations, "Duplicate/partially moved address would target the same resource.")
        destinations.add(destination)
        if original == "terraform_data.landing_zone":
            expected_input = {"gpu_subnet_id": chosen["gpu_subnet_id"], "private_endpoint_subnet_id": chosen["private_endpoint_subnet_id"],
                              "gpu_subnet_cidr": chosen["gpu_subnet_cidr"]}
            require(attributes.get("input") == expected_input, "Landing-zone state input differs from explicit configuration.")
        else:
            require(original in expected and attributes["id"].lower() == expected[original].lower(), "Unexpected managed state address or resource ID; manual review required.")
        if original.startswith("azurerm_private_endpoint."):
            key = "workspace" if original.endswith(".workspace") else re.fullmatch(r'azurerm_private_endpoint.service\["([^"]+)"\]', original)[1]
            zones = [chosen["private_dns_zone_ids"][name] for name in (("api", "notebooks") if key == "workspace" else (key,))]
            group = attributes.get("private_dns_zone_group")
            require(attributes.get("subnet_id", "").lower() == chosen["private_endpoint_subnet_id"].lower() and isinstance(group, list) and len(group) == 1 and
                    {value.lower() for value in group[0].get("private_dns_zone_ids", [])} == {value.lower() for value in zones},
                    "Preserve private endpoint subnet/DNS bindings during address migration.")
        if original == "azurerm_application_insights.studio":
            require(attributes.get("workspace_id", "").lower() == chosen["log_analytics_workspace_id"].lower(), "Preserve the supplied logging binding during migration.")
        if original == "azurerm_subnet_network_security_group_association.compute[0]":
            require(attributes.get("subnet_id", "").lower() == chosen["gpu_subnet_id"].lower() and attributes.get("network_security_group_id", "").lower() == ids["nsg"].lower(),
                    "NSG association state differs from the explicit dedicated subnet/owned NSG.")
        if original in grants:
            scope, principal, role = grants[original]
            require((attributes["scope"].lower(), attributes["principal_id"], attributes["role_definition_id"].lower()) ==
                    (scope.lower(), principal, role.lower()), "Legacy role state differs from its deterministic scope/principal/role.")
        if address != destination:
            moves.append({"from": address, "to": destination, "id": attributes["id"]})
    if read is not None:
        verify_off(read, receipt, chosen, ids)
    commands = []
    if read is not None:
        commands += [f"terraform -chdir=infra state rm -lock-timeout=60s '{value['address']}'" for value in absent]
        commands += [f"terraform -chdir=infra state mv -lock-timeout=60s '{value['from']}' '{value['to']}'" for value in moves]
    return {
        "schemaVersion": 1, "readOnly": True, "verifiedOff": read is not None, "stateLineage": state["lineage"], "stateSerial": state["serial"],
        "checkedAt": datetime.now(timezone.utc).isoformat() if read is not None else None,
        "moves": moves, "verifiedAbsentStateEntries": absent if read is not None else [], "sharedHostAddressesRetained": retained,
        "intentionalRoleChange": {"address": module + 'azurerm_role_assignment.service["compute_storage"]',
                                 "fromScope": ids["storage"], "toScope": expected["azapi_resource.container"],
                                 "principalId": grants['azurerm_role_assignment.service["compute_storage"]'][1],
                                 "roleDefinitionId": grants['azurerm_role_assignment.service["compute_storage"]'][2],
                                 "oldId": expected['azurerm_role_assignment.service["compute_storage"]'],
                                 "reason": "GPU jobs must not write the control container."},
        "commands": commands,
        "rollbackMoves": [{"from": value["to"], "to": value["from"]} for value in reversed(moves)],
        "nextGate": "Review exact state commands against the same backed-up state, then review a fresh saved plan. Never forget live resources or execute a stale assessment.",
    }


def live_reader(config):
    executable = shutil.which("az")
    require(executable is not None, "Azure CLI is required only for explicitly requested live read verification.")
    account = json.loads(subprocess.check_output([executable, "account", "show", "--output", "json", "--only-show-errors"], text=True))
    require(account["id"] == config["subscription_id"] and account["tenantId"] == config["tenant_id"], "Azure CLI scope differs; alternate identities are refused.")
    token = subprocess.check_output([executable, "account", "get-access-token", "--resource", "https://management.azure.com/",
                                     "--query", "accessToken", "--output", "tsv", "--only-show-errors"], text=True).strip()
    def read(path, api):
        url = path if path.startswith("https://") else "https://management.azure.com" + path
        if api:
            url += "?api-version=" + api
        require(url.startswith(f"https://management.azure.com/subscriptions/{config['subscription_id']}/"), "ARM read escaped the selected subscription.")
        request = urllib.request.Request(url, headers={"Authorization": "Bearer " + token})
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            raise RuntimeError(f"Read-only ARM verification failed (HTTP {error.code}); no state commands were authorized.") from None
    return read


def validate_plan(plan, report=None):
    """Reject accidental root/module deletes after mapping; allow only the reviewed GPU-scope replacement."""
    require(isinstance(plan.get("format_version"), str) and plan["format_version"].startswith("1.") and
            isinstance(plan.get("resource_changes"), list) and not plan.get("errored") and plan.get("complete") is not False,
            "Supply complete terraform show -json output for the exact saved plan.")
    reduction = report["intentionalRoleChange"] if report else None
    for resource in plan["resource_changes"]:
        if resource.get("mode") == "data":
            continue
        values = resource["change"]
        actions = values["actions"]
        kind = resource["type"]
        allowed = {"azurerm_resource_group", "azurerm_user_assigned_identity", "azurerm_key_vault", "azurerm_container_registry",
                   "azurerm_application_insights", "azurerm_private_endpoint", "azurerm_network_security_group",
                   "azurerm_subnet_network_security_group_association", "azurerm_role_assignment", "azurerm_role_definition",
                   "azurerm_service_plan", "azurerm_linux_web_app", "terraform_data", "azapi_resource"}
        arm_type = ((values.get("after") or values.get("before") or {}).get("type") or "").split("@")[0].lower()
        allowed_arm = {"microsoft.storage/storageaccounts", "microsoft.storage/storageaccounts/blobservices/containers",
                       "microsoft.machinelearningservices/workspaces", "microsoft.machinelearningservices/workspaces/datastores"}
        require(kind in allowed and (kind != "azapi_resource" or arm_type in allowed_arm),
                "Saved migration plan contains a forbidden compute/egress/landing-zone owner.")
        require(actions in (["no-op"], ["create"], ["update"], ["delete"], ["delete", "create"], ["create", "delete"]),
                "Unknown plan action; do not assume it is safe.")
        if "delete" not in actions:
            continue
        before, after = values.get("before") or {}, values.get("after") or {}
        require(reduction is not None and resource["address"] == reduction["address"] and kind == "azurerm_role_assignment" and
                set(actions) == {"delete", "create"} and before.get("scope", "").lower() == reduction["fromScope"].lower() and
                after.get("scope", "").lower() == reduction["toScope"].lower() and
                before.get("id", "").lower() == reduction["oldId"].lower() and
                before.get("principal_id") == reduction["principalId"] == after.get("principal_id") and
                before.get("role_definition_id", "").lower() == reduction["roleDefinitionId"].lower() == after.get("role_definition_id", "").lower() and
                after.get("name") == str(uuid.uuid5(uuid.NAMESPACE_URL,
                    f"{reduction['toScope']}/{reduction['principalId']}/{reduction['roleDefinitionId'].rsplit('/', 1)[1]}".lower())),
                "Saved plan deletes/replaces a durable or unexpected role resource; complete exact state mapping and review drift first.")
    return True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True, help="Explicit new regional configuration, not the old auto-loaded tfvars.")
    parser.add_argument("--receipt", type=Path, required=True, help="Validated cached v1 foundation receipt outside Git.")
    parser.add_argument("--state-backup", type=Path, required=True, help="Restorable terraform state pull JSON backup, outside Git.")
    parser.add_argument("--verify-live", action="store_true", help="Explicitly permit read-only ARM/CLI checks, never mutations.")
    parser.add_argument("--plan-json", type=Path, help="Optional terraform show -json of the reviewed saved plan; rejects unintended replacement/deletion.")
    args = parser.parse_args()
    config, receipt, state = load(args.config), load(args.receipt), load(args.state_backup)
    context(config, receipt)
    if args.verify_live:
        require(not (args.receipt.resolve().parent / "armed.json").exists(), "Disarm/stop the old local submission path first.")
        for candidate in Path(__file__).resolve().parent.iterdir():
            if candidate.name in ("terraform.tfvars", "terraform.tfvars.json") or re.search(r"\.auto\.tfvars(?:\.json)?$", candidate.name):
                require(candidate.resolve() == args.config.resolve(),
                        "Preserve the old automatic tfvars outside infra before live migration assessment; hidden Terraform overrides are refused.")
    report = assess(config, receipt, state, live_reader(config) if args.verify_live else None)
    if args.plan_json:
        report["planMigrationSafe"] = validate_plan(load(args.plan_json), report)
    print(json.dumps(report, indent=2))
