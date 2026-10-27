"""Promote one sealed trainer image into a region; the create-only trainer binding is the last write."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import tempfile
import uuid

from pipeline import Azure, WORKSPACE_API, contracts, immutable_bytes, json_bytes, load, promote_image, require, wiring
from publish_region import registration

TRAINER_FIELDS = ("name", "version", "module", "presets", "defaultPreset", "licenseCode", "licenseWeights")


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
    sealed = original.get_blob_client(f"images/yolox/{artifact_id}.json").download_blob().readall()
    require(hashlib.sha256(sealed).hexdigest() == artifact_id, "The sealed trainer record does not match its identity.")
    record = json.loads(sealed.decode("utf-8"))
    require(record.get("schemaVersion") == 1 and record.get("component") == "yolox" and
            re.fullmatch(r"[a-f0-9]{40}", str(record.get("sourceSha"))) and re.fullmatch(r"sha256:[a-f0-9]{64}", str(record.get("imageDigest"))),
            "Sealed trainer record is invalid.")
    trainer = record.get("trainer")
    require(isinstance(trainer, dict) and set(trainer) == set(TRAINER_FIELDS), "Sealed trainer record has no exact trainer metadata.")
    workspace = azure.owned(target["workspaceId"], target["ownershipTags"], WORKSPACE_API)["properties"]
    wired = {"workspaceIdentityId": wiring(target, region_key, "workspaceIdentityId"),
             "keyVaultId": wiring(target, region_key, "keyVaultId")}
    require(workspace.get("publicNetworkAccess") == "Disabled" and workspace.get("allowPublicAccessWhenBehindVnet") is False and
            workspace.get("systemDatastoresAuthMode") == "Identity" and
            all(isinstance(workspace.get(field), str) and workspace[field].lower() == (wired.get(key) or target[key]).lower() for field, key in
                (("primaryUserAssignedIdentity", "workspaceIdentityId"), ("storageAccount", "storageAccountId"),
                 ("containerRegistry", "registryId"), ("keyVault", "keyVaultId"))),
            "Workspace privacy, system-datastore identity or linked services changed.")
    image = promote_image(azure, source, target, "wan/yolox", record["imageDigest"])
    environment_id = f"{target['workspaceId']}/environments/wan-trainer-{trainer['name']}-{artifact_id[:12]}/versions/1"
    registration(azure, environment_id, {"image": image, "autoRebuild": "Disabled", "tags": target["ownershipTags"]})
    binding = {
        "schemaVersion": 1, "region": region_key, "workspaceId": target["workspaceId"],
        "scopeFingerprint": checked["scopeFingerprints"][region_key], "artifactId": artifact_id,
        **{key: trainer[key] for key in TRAINER_FIELDS},
        "sourceSha": record["sourceSha"], "imageDigest": record["imageDigest"], "image": image, "environmentId": environment_id,
        "verifiedAt": datetime.now(timezone.utc).isoformat(), "verifiedBy": actor,
    }
    path = directory / "trainer-binding.json"
    path.write_bytes(json_bytes(binding))
    contracts(settings, trainer=path)
    name = f"trainers/{trainer['name']}/{artifact_id}.json"
    from azure.core.exceptions import ResourceNotFoundError
    try:
        previous = destination.get_blob_client(name).download_blob().readall()
    except ResourceNotFoundError:
        previous = None
    if previous is not None:
        # A retry re-verifies everything but keeps the original immutable audit timestamp.
        path.write_bytes(previous)
        existing = contracts(settings, trainer=path)["trainer"]
        require(all(existing[key] == value for key, value in binding.items() if key not in ("verifiedAt", "verifiedBy")),
                "Existing trainer binding differs.")
        return name
    immutable_bytes(destination, name, json_bytes(binding))
    return name


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--region", required=True)
    args = parser.parse_args()
    require(re.fullmatch(r"[a-f0-9]{64}", args.artifact), "Invalid immutable trainer identity.")
    with tempfile.TemporaryDirectory(prefix="wan-trainer-promotion-") as temp:
        print("Verified trainer binding: " + promote(args.settings, args.artifact, args.region, Path(temp)))
