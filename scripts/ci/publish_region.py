"""Promote one sealed WAN build; the create-only regional binding is the last write."""
import argparse
import re
from datetime import datetime, timezone
from pathlib import Path
import tempfile
import time
import uuid

from pipeline import (Azure, ML_API, WORKSPACE_API, contracts, download_source, immutable_bytes, json_bytes, load,
                      private_host, promote_image, require, snapshot, snapshot_id, wiring, workspace_mismatches, datastore_mismatches)


def registration(azure, resource_id, expected):
    found = azure.arm(resource_id, missing=True)
    if found is None:
        azure.arm(resource_id, method="PUT", body={"properties": expected})
    deadline = time.monotonic() + 300
    found = azure.arm(resource_id)
    while found["properties"].get("provisioningState") in ("Creating", "Updating"):
        require(time.monotonic() < deadline, "AML asset did not finish registering.")
        time.sleep(5)
        found = azure.arm(resource_id)
    properties = found["properties"]
    if "modelUri" in expected:
        # ARM expands datastore URIs with the workspace identity; compare the same short form the binding records.
        properties["modelUri"] = re.sub(r"^azureml://subscriptions/[^/]+/resource[gG]roups/[^/]+/workspaces/[^/]+/datastores/", "azureml://datastores/",
                                        properties.get("modelUri", "")).rstrip("/") + "/"
    problems = []
    if found["id"].lower() != resource_id.lower():
        problems.append(f"id: expected {resource_id}, actual {found['id']}")
    problems += [f"{key}: expected {value}, actual {properties.get(key)}" for key, value in expected.items()
                 if key != "tags" and properties.get(key) != value]
    problems += [f"tag {key}: expected {value}, actual {properties.get('tags', {}).get(key)}" for key, value in expected["tags"].items()
                 if properties.get("tags", {}).get(key) != value]
    if properties.get("provisioningState") != "Succeeded":
        problems.append(f"provisioningState: expected Succeeded, actual {properties.get('provisioningState')}")
    for flag, wrong in (("isArchived", bool(properties.get("isArchived", False))), ("build", bool(properties.get("build"))),
                        ("condaFile", bool(properties.get("condaFile")))):
        if wrong:
            problems.append(f"{flag}: must be unset")
    require(not problems, f"AML registration {resource_id} is incomplete or differs; existing assets are never overwritten: " + "; ".join(problems))


def promote(settings, artifact_id, region_key, directory):
    checked = contracts(settings)
    receipt = checked["foundation"]
    require(region_key in receipt["regions"], "Unselected region.")
    actor = load(settings).get("Pipeline", {}).get("PublisherPrincipalId")
    require(actor and str(uuid.UUID(actor)) == actor, "Pipeline.PublisherPrincipalId is required.")
    azure = Azure(receipt["scope"])
    require(azure.actor_id == actor, "Pipeline.PublisherPrincipalId is not the authenticated CLI caller.")
    source, target = receipt["regions"][receipt["controlRegion"]], receipt["regions"][region_key]
    original, destination = azure.container(source), azure.container(target)
    manifest = directory / "manifest.json"
    manifest.write_bytes(original.get_blob_client(f"builds/{artifact_id}/manifest.json").download_blob().readall())
    checked = contracts(settings, manifest)
    build = checked["build"]
    require(build["artifactId"] == artifact_id, "Requested build identity changed.")
    workspace = azure.owned(target["workspaceId"], target["ownershipTags"], WORKSPACE_API)["properties"]
    wired = {"workspaceIdentityId": wiring(target, region_key, "workspaceIdentityId"),
             "keyVaultId": wiring(target, region_key, "keyVaultId")}
    mismatches = workspace_mismatches(workspace, target, wired)
    require(not mismatches, f"Workspace privacy, system-datastore identity or linked services changed for {region_key}: " + "; ".join(mismatches))
    for host in target["privateConnectivityHosts"]:
        private_host(host)
    datastore = azure.arm(target["workspaceId"] + "/datastores/" + target["releaseDatastore"])["properties"]
    mismatches = datastore_mismatches(datastore, target, receipt["scope"]["subscriptionId"])
    require(not mismatches, f"Release datastore is not this private managed-identity store for {region_key}: " + "; ".join(mismatches))
    prefixes = {}
    for kind in ("code", "models", "licenses", "buildInputs"):
        prefix = kind + "/" + snapshot_id(build[kind])
        require({item.name for item in original.list_blobs(name_starts_with=prefix + "/")} ==
                {prefix + "/" + item["path"] for item in build[kind]}, "Source immutable snapshot contains missing/unexpected files.")
        prefixes[kind] = snapshot(destination, kind, build[kind], download_source(original, prefix, directory))
    image = promote_image(azure, source, target, "wan/runtime", build["imageDigest"])
    model_name = "wan-models-" + snapshot_id(build["models"])
    model_uri = f"azureml://datastores/{target['releaseDatastore']}/paths/{prefixes['models']}/"
    environment_id = target["workspaceId"] + "/environments/wan-" + artifact_id + "/versions/1"
    registration(azure, environment_id, {"image": image, "autoRebuild": "Disabled", "tags": target["ownershipTags"]})
    registration(azure, target["workspaceId"] + "/models/" + model_name + "/versions/1",
                 {"modelType": "CustomModel", "modelUri": model_uri, "tags": target["ownershipTags"]})
    immutable_bytes(destination, f"builds/{artifact_id}/manifest.json", json_bytes(build))
    binding = {
        "schemaVersion": 1, "region": region_key, "workspaceId": target["workspaceId"],
        "scopeFingerprint": checked["scopeFingerprints"][region_key], "artifactId": artifact_id,
        "environmentId": environment_id, "modelsRef": f"azureml:{model_name}:1",
        "codeUri": f"azureml://datastores/{target['releaseDatastore']}/paths/{prefixes['code']}/",
        "modelsUri": model_uri, "image": image, "verifiedAt": datetime.now(timezone.utc).isoformat(), "verifiedBy": actor,
    }
    path = directory / "binding.json"
    path.write_bytes(json_bytes(binding))
    contracts(settings, manifest, path)
    # A successful retry re-verifies everything but keeps the original immutable audit timestamp.
    name = f"releases/{artifact_id}/{checked['scopeFingerprints'][region_key]}.json"
    from azure.core.exceptions import ResourceNotFoundError
    try:
        previous = destination.get_blob_client(name).download_blob().readall()
    except ResourceNotFoundError:
        previous = None
    if previous is not None:
        path.write_bytes(previous)
        existing = contracts(settings, manifest, path)["release"]
        require(all(existing[key] == value for key, value in binding.items() if key not in ("verifiedAt", "verifiedBy")),
                "Existing regional binding differs.")
        return name
    immutable_bytes(destination, name, json_bytes(binding))
    return name


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--region", required=True)
    args = parser.parse_args()
    import re
    require(re.fullmatch(r"[a-f0-9]{64}", args.artifact), "Invalid immutable build identity.")
    with tempfile.TemporaryDirectory(prefix="wan-promotion-") as temp:
        print("Verified regional binding: " + promote(args.settings, args.artifact, args.region, Path(temp)))
