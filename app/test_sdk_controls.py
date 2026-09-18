"""Offline integration check using the installed SDK and a public pinned upstream checkout."""
from __future__ import annotations

import argparse
import ast
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
from types import SimpleNamespace
from unittest.mock import Mock, patch


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    here = Path(__file__).resolve().parent
    scratch = args.scratch.resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    from cost_guard import UPSTREAM_SHA
    head = subprocess.check_output(["git", "-C", str(args.upstream), "rev-parse", "HEAD"], text=True).strip()
    assert head == UPSTREAM_SHA, "Integration fixture must use the exact upstream commit."
    source = scratch / "source"
    source.mkdir()
    archive = subprocess.check_output(["git", "-C", str(args.upstream), "archive", "HEAD"])
    with tarfile.open(fileobj=io.BytesIO(archive)) as bundle:
        tracked = [member.name for member in bundle.getmembers() if member.isfile()]
        bundle.extractall(source, filter="data")
    subprocess.run(["git", "init", "-q", str(source)], check=True)
    for check in (["--check"], []):
        subprocess.run(["git", "-C", str(source), "apply", "--ignore-space-change",
                        *check, str(here / "upstream-cost.patch")], check=True)
    for helper in ("config.py", "cost_guard.py"):
        shutil.copy2(here / helper, source / "azureml" / helper)
    tree = ast.parse((here / "test_controls.py").read_text(encoding="utf-8"))
    fixture = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "portable_fixture")
    namespace = {}
    exec(compile(ast.Module(body=[fixture], type_ignores=[]), "<portable-fixture>", "exec"), namespace)
    config, foundation = namespace["portable_fixture"]()
    for variable, name, payload in (
        ("WAN_STUDIO_CONFIG", "terraform.tfvars.json", config),
        ("WAN_STUDIO_FOUNDATION", "foundation.json", foundation),
    ):
        path = scratch / name
        path.write_text(json.dumps(payload), encoding="utf-8")
        os.environ[variable] = str(path)
    os.environ["WAN_STUDIO_CONTRACT"] = str(scratch / "prepared.json")
    os.environ["WAN_STUDIO_GATE"] = str(scratch / "armed.json")
    import runtime
    staged = scratch / "staged"
    with patch.object(runtime, "run", return_value="\0".join(tracked)):
        records = runtime.snapshot(source, staged)
    assert {item["path"] for item in records}.issuperset({"azureml/config.py", "azureml/cost_guard.py"})
    assert (staged / "src" / "comfy" / "ldm" / "models" / "autoencoder.py").is_file()
    assert (staged / "src" / "comfy_api" / "input" / "video_types.py").is_file()
    for helper in ("config.py", "cost_guard.py"):
        assert (staged / "azureml" / helper).read_bytes() == (here / helper).read_bytes()
    print("PASS: actual staged snapshot retains hashed helpers and required Comfy package directories.")
    sys.path.insert(0, str(staged))
    from azure.identity import AzureCliCredential
    from azureml import config as copied_config, cost_guard, register_and_submit as aml, web_submit
    from azureml.workflow_profiles import get_profile

    with patch.object(copied_config, "assert_cli_scope"):
        assert isinstance(copied_config.build_credential(), AzureCliCredential)
    with patch.object(cost_guard, "assert_cli_scope"):
        for profile in ("wan", "minimax_h3"):
            manifest = {
                "sourceSha": UPSTREAM_SHA, "scopeFingerprint": copied_config.scope_fingerprint(),
                "profile": profile, "workflow": get_profile(profile).workflow, "version": "prepared",
                "environmentId": "azureml:sample-environment:1", "modelsRef": "azureml:sample-models-hash:1",
                "modelsVersion": "1", "codeUri": "azureml://datastores/sample/paths/code/prepared/",
                "computeIdentityClientId": foundation["computeIdentityClientId"],
            }
            Path(os.environ["WAN_STUDIO_CONTRACT"]).write_text(json.dumps(manifest), encoding="utf-8")
            Path(os.environ["WAN_STUDIO_GATE"]).write_text(json.dumps({
                "compute": foundation["computeName"], "profile": profile, "version": "prepared",
            }), encoding="utf-8")
            settings = runtime.portal_settings(manifest)
            assert (settings.host, settings.port) == ("127.0.0.1", 51881)
            assert set(settings.profiles) == {profile}
            submit_args = web_submit.build_submission_args(
                settings, settings.profiles[profile], prompt="A small geometric toy moves.",
                negative_prompt="", duration_seconds=1.0,
                uploaded_images={"input_image": {
                    "url": "azureml://datastores/sample/paths/sample/web-inputs/fixture.png",
                    "filename": "fixture.png",
                }},
            )
            submit_args.timeout_seconds = 1800
            factory = (lambda: aml.build_job(submit_args, staged, environment_ref=manifest["environmentId"],
                                             models_ref=manifest["modelsRef"], profile=get_profile(profile))
                       ) if profile == "wan" else lambda: web_submit.build_minimax_h3_job(submit_args)
            job = factory()
            assert isinstance(job, dict) and not dict(job), "Exercise the SDK's actual empty dictionary subclass."
            cost_guard.verify_server_controls(job, submit_args)
            assert job.identity.client_id == foundation["computeIdentityClientId"]
            assert job.inputs["input_image_url"].type == "uri_file"
            assert job.inputs["models"].mode == "ro_mount"
            assert job.outputs["generated"].path.startswith("azureml://datastores/sample/paths/video-library/")
            assert job.environment_variables == {}
            if profile == "wan":
                assert '--negative-prompt ""' in job.command and "negative_prompt" not in job.inputs
            else:
                assert "hf download" not in job.command and "pip install" not in job.command
            job.name = "sample-job"
            client = SimpleNamespace(jobs=SimpleNamespace(
                create_or_update=Mock(return_value=job), get=Mock(return_value=job), cancel=Mock(),
            ))
            with patch.object(aml, "ml_client", return_value=client), patch.object(aml, "repo_root", return_value=staged):
                if profile == "wan":
                    aml.create_job_submission(submit_args)
                else:
                    web_submit.submit_minimax_h3_job(submit_args)
            client.jobs.create_or_update.assert_called_once()
            client.jobs.get.assert_called_once_with(job.name)
            client.jobs.cancel.assert_not_called()
            job["limits"] = None
            try:
                cost_guard.verify_created_job(client, job, submit_args)
            except cost_guard.ServerJobSafetyError:
                pass
            else:
                raise AssertionError("Explicit missing server limits must fail closed.")
            client.jobs.cancel.assert_called_once_with(job.name)
            client.jobs.create_or_update.assert_called_once()
            print(f"PASS: {profile} actual SDK factory, private MI inputs, persisted readback and no resubmission.")


if __name__ == "__main__":
    main()
