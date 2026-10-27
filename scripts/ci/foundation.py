"""Private saved-plan transport and isolated Terraform sources; never touches checkout state/config."""
import json
import os
from pathlib import Path
import re
import shutil
import sys

from pipeline import Azure, ROOT, immutable_bytes, json_bytes, load, require, run, sha

sys.path.insert(0, str(ROOT / "infra"))
from migration import validate_plan


def context(config_path, backend_path):
    config, backend = load(config_path), load(backend_path)
    require(config.get("regions") and all(value.get("compute_enabled", False) is False for value in config["regions"].values()),
            "Foundation cannot enable compute.")
    scope = {"tenantId": config["tenant_id"], "subscriptionId": config["subscription_id"]}
    azure = Azure(scope)
    allowed = {"resource_group_name", "storage_account_name", "container_name", "key", "artifact_container_name"}
    require(set(backend) == allowed and backend["artifact_container_name"] != backend["container_name"] and
            re.fullmatch(r"[a-zA-Z0-9_.-]+", backend["key"]), "Use explicit separate private backend and artifact containers; no credentials or extra backend auth fields.")
    storage_id = f"/subscriptions/{scope['subscriptionId']}/resourceGroups/{backend['resource_group_name']}/providers/Microsoft.Storage/storageAccounts/{backend['storage_account_name']}"
    properties = azure.arm(storage_id, "2023-05-01")["properties"]
    require(properties.get("publicNetworkAccess") == "Disabled" and properties.get("allowSharedKeyAccess") is False and
            properties.get("allowBlobPublicAccess") is False, "Backend/artifact storage must be private and Entra-only.")
    return azure, backend, azure.blob_container(backend["storage_account_name"], backend["artifact_container_name"])


def directory(work, backend, config_path):
    work.mkdir(mode=0o700)
    for path in (ROOT / "infra").glob("*.tf"):
        shutil.copyfile(path, work / path.name)
    shutil.copyfile(ROOT / "infra/.terraform.lock.hcl", work / ".terraform.lock.hcl")
    shutil.copytree(ROOT / "infra/modules", work / "modules")
    (work / "backend.tf").write_text('terraform {\n  backend "azurerm" {}\n}\n')
    shutil.copyfile(config_path, work / "customer.tfvars.json")
    effective = {key: backend[key] for key in ("resource_group_name", "storage_account_name", "container_name", "key")}
    effective.update(use_azuread_auth=True, use_cli=True, use_msi=False, use_oidc=False,
                     tenant_id=load(config_path)["tenant_id"], subscription_id=load(config_path)["subscription_id"])
    (work / "backend.json").write_bytes(json_bytes(effective))
    run("terraform", "-chdir=" + str(work), "init", "-input=false", "-lockfile=readonly", "-backend-config=backend.json", "-no-color")


def provenance(config, backend):
    require(re.fullmatch(r"[a-f0-9]{40}", os.environ["GITHUB_SHA"]) and
            re.fullmatch(r"[0-9]+", os.environ["GITHUB_RUN_ID"]) and re.fullmatch(r"[0-9]+", os.environ["GITHUB_RUN_ATTEMPT"]),
            "Protected plan needs exact GitHub source/run provenance.")
    return {"schemaVersion": 1, "sourceSha": os.environ["GITHUB_SHA"], "runId": os.environ["GITHUB_RUN_ID"],
            "runAttempt": os.environ["GITHUB_RUN_ATTEMPT"], "tfvarsSha256": sha(config),
            "backendSha256": sha(backend), "lockSha256": sha(ROOT / "infra/.terraform.lock.hcl")}


def plan(config, backend_path, work):
    _, backend, container = context(config, backend_path)
    directory(work, backend, config)
    run("terraform", "-chdir=" + str(work), "plan", "-input=false", "-no-color", "-var-file=customer.tfvars.json", "-out=reviewed.plan")
    plan_json = run("terraform", "-chdir=" + str(work), "show", "-json", "reviewed.plan")
    validate_plan(json.loads(plan_json))
    (work / "reviewed.json").write_text(plan_json, encoding="utf-8")
    record = provenance(config, backend_path)
    record.update(planSha256=sha(work / "reviewed.plan"), jsonSha256=sha(work / "reviewed.json"))
    key = "plans/" + record["runId"] + "-" + record["runAttempt"]
    for name in ("reviewed.plan", "reviewed.json"):
        immutable_bytes(container, key + "/" + name, (work / name).read_bytes())
    immutable_bytes(container, key + "/provenance.json", json_bytes(record))
    for change in json.loads(plan_json).get("resource_changes", []):
        if change["mode"] != "data" and change["change"]["actions"] != ["no-op"]:
            print(change["type"] + ": " + ",".join(change["change"]["actions"]))
    print("Private reviewed plan saved. Approval must inspect the exact private reviewed.json; no state/plan uploaded to GitHub.")


def apply(config, backend_path, work):
    _, backend, container = context(config, backend_path)
    expected = provenance(config, backend_path)
    key = "plans/" + expected["runId"] + "-" + expected["runAttempt"]
    record = json.loads(container.get_blob_client(key + "/provenance.json").download_blob().readall())
    require(all(record.get(name) == value for name, value in expected.items()), "Saved plan source/tfvars/backend/lock/run binding differs.")
    directory(work, backend, config)
    for name, digest in (("reviewed.plan", "planSha256"), ("reviewed.json", "jsonSha256")):
        path = work / name
        path.write_bytes(container.get_blob_client(key + "/" + name).download_blob().readall())
        require(sha(path) == record[digest], "Saved reviewed plan changed.")
    actual = json.loads(run("terraform", "-chdir=" + str(work), "show", "-json", "reviewed.plan"))
    require(actual == load(work / "reviewed.json"), "Binary plan is not its reviewed JSON.")
    validate_plan(actual)
    run("terraform", "-chdir=" + str(work), "apply", "-input=false", "-no-color", "reviewed.plan")
    print("Applied the exact reviewed plan. No compute allocated.")
