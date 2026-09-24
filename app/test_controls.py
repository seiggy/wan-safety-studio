"""Framework-free offline guard/patch/contract tests; no third-party packages required."""
from __future__ import annotations

import argparse
import ast
import base64
from contextlib import nullcontext, redirect_stdout
from datetime import timedelta
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tomllib
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import Mock, patch
from urllib.parse import quote, urlsplit
import uuid

ROOT = Path(__file__).resolve().parent


def module(name, **members):
    value = ModuleType(name)
    value.__dict__.update(members)
    sys.modules[name] = value
    return value


class Entity:
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)


module("azure")
module("azure.ai")
module("azure.ai.ml")
module("azure.ai.ml.entities", CommandJobLimits=Entity, JobResourceConfiguration=Entity, ManagedIdentityConfiguration=Entity)
import cost_guard
import config as studio_config


def portable_fixture():
    zero = "00000000-0000-0000-0000-000000000000"
    prefix = f"/subscriptions/{zero}/resourceGroups/rg-sample/providers/"
    workspace = prefix + "Microsoft.MachineLearningServices/workspaces/mlw-sample"
    network = prefix + "Microsoft.Network/"
    settings = {
        "deployment_name": "sample", "subscription_id": zero, "tenant_id": zero, "location": "eastus",
        "operator_principal_id": zero, "gpu_subnet_dedicated": True,
        "gpu_subnet_id": network + "virtualNetworks/vnet-sample/subnets/gpu",
        "gpu_subnet_cidr": "10.0.0.0/26",
        "private_endpoint_subnet_id": network + "virtualNetworks/vnet-sample/subnets/endpoints",
        "private_dns_zone_ids": {key: network + "privateDnsZones/" + zone for key, zone in {
            "blob": "privatelink.blob.core.windows.net", "file": "privatelink.file.core.windows.net",
            "vault": "privatelink.vaultcore.azure.net", "registry": "privatelink.azurecr.io",
            "api": "privatelink.api.azureml.ms", "notebooks": "privatelink.notebooks.azure.net",
        }.items()},
        "log_analytics_workspace_id": prefix + "Microsoft.OperationalInsights/workspaces/log-sample",
    }
    foundation = {
        "subscriptionId": zero, "tenantId": zero, "location": "eastus", "deploymentName": "sample",
        "resourceGroupName": "rg-sample", "workspaceName": "mlw-sample", "workspaceId": workspace,
        "computeName": "sample-gpu", "computeId": workspace + "/computes/sample-gpu",
        "storageAccountName": "stsample", "storageAccountId": prefix + "Microsoft.Storage/storageAccounts/stsample",
        "registryName": "crsample", "registryLoginServer": "crsample.azurecr.io",
        "keyVaultName": "kv-sample", "keyVaultId": prefix + "Microsoft.KeyVault/vaults/kv-sample",
        "containerName": "sample", "datastoreName": "sample",
        "workspaceIdentityId": prefix + "Microsoft.ManagedIdentity/userAssignedIdentities/workspace",
        "computeIdentityId": prefix + "Microsoft.ManagedIdentity/userAssignedIdentities/compute",
        "computeIdentityClientId": zero, "gpuSubnetId": settings["gpu_subnet_id"],
        "gpuSubnetCidr": settings["gpu_subnet_cidr"], "privateEndpointSubnetId": settings["private_endpoint_subnet_id"],
        "natGatewayId": network + "natGateways/sample", "publicIpId": network + "publicIPAddresses/sample",
        "networkSecurityGroupId": network + "networkSecurityGroups/sample",
        "ownershipTags": {"deployment": "sample", "application": "wan-safety-studio", "managedBy": "terraform"},
        "privateConnectivityHosts": ["stsample.blob.core.windows.net"], "computeEnabled": False,
        "manageComputeEgress": False, "maxPaygHourlyUsd": 4, "maxJobSeconds": 7200, "instanceCount": 1,
    }
    return settings, foundation


class Controls(unittest.TestCase):
    def test_native_webm_is_converted_and_only_current_job_videos_are_verified(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        nodes = [n for n in tree.body if isinstance(n, ast.FunctionDef) and
                 n.name in ("media_tool", "verify_smoke_videos")]
        metadata = {"streams": [{"codec_name": "h264", "width": 512, "height": 512}], "format": {"duration": "1.0625"}}
        calls = []

        def run(*arguments, **kwargs):
            calls.append(arguments)
            if arguments[0] == "ffmpeg":
                Path(arguments[-1]).write_bytes(b"fixture-video")
            else:
                return json.dumps(metadata)

        namespace = {"Path": Path, "json": json, "shutil": SimpleNamespace(which=lambda tool: tool), "run": run}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<smoke-video>", "exec"), namespace)
        job_directory = SCRATCH / "current-job-video"
        job_directory.mkdir()
        (job_directory / "clip.webm").write_bytes(b"fixture-source")
        self.assertEqual(namespace["verify_smoke_videos"](job_directory, "image"), 1)
        self.assertTrue((job_directory / "clip.webm").exists())
        self.assertIn("libx264", calls[0])
        self.assertIn("-n", calls[0])
        metadata["streams"][0]["width"] = 256
        with self.assertRaises(ValueError):
            namespace["verify_smoke_videos"](job_directory, "image")
        empty_job = job_directory / "empty-job"
        empty_job.mkdir()
        with self.assertRaises(ValueError):
            namespace["verify_smoke_videos"](empty_job, "image")
        namespace["shutil"].which = lambda _: None
        probe = namespace["media_tool"]("ffprobe", job_directory, "image")
        self.assertIn("none", probe)
        self.assertIn("never", probe)
        self.assertIn(f"type=bind,source={job_directory},target=/videos,readonly", probe)
        submit = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "submit")
        self.assertTrue(any(isinstance(n, ast.Assign) and ast.unparse(n.value) ==
                            "args.download_directory / job.name" for n in ast.walk(submit)))

    def test_sdk_dict_subclass_exposes_controls_as_attributes(self):
        class SdkJob(dict):
            pass

        job = SdkJob()
        job.name = "sdk-fixture"
        job.limits = Entity(timeout=1800)
        job.resources = Entity(instance_count=1)
        job.compute = self.foundation["computeName"]
        self.assertEqual(dict(job), {})
        cost_guard.verify_server_controls(job, self.args(timeout_seconds=1800))
        client = SimpleNamespace(jobs=SimpleNamespace(get=Mock(return_value=job), cancel=Mock()))
        self.assertIs(cost_guard.verify_created_job(client, job, self.args(timeout_seconds=1800)), job)
        client.jobs.cancel.assert_not_called()
        job["limits"] = None
        with self.assertRaises(ValueError):
            cost_guard.verify_server_controls(job, self.args(timeout_seconds=1800))

    def test_model_registration_uses_integer_version_and_content_addressed_name(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        functions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and
                     n.name in ("prepare", "verify_cloud")]
        version = "dc0d29031b73-contenthash"
        image = "registry.example/comfyui@sha256:fixture"
        create_model = Mock()
        client = SimpleNamespace(
            environments=SimpleNamespace(create_or_update=Mock(return_value=Entity(id="environment-id")),
                                         get=Mock(return_value=Entity(image=image))),
            models=SimpleNamespace(create_or_update=create_model, get=Mock()))
        container = SimpleNamespace(list_blobs=Mock(return_value=[]))
        datastore = {"properties": {"accountName": self.foundation["storageAccountName"],
                     "containerName": self.foundation["containerName"],
                     "credentials": {"credentialsType": "None"},
                     "serviceDataAccessAuthIdentity": "WorkspaceUserAssignedIdentity"}}
        write = Mock()
        namespace = {
            "Path": Path, "sys": SimpleNamespace(path=[]), "json": json, "re": re,
            "datetime": SimpleNamespace(now=lambda _: SimpleNamespace(isoformat=lambda: "fixture-time")),
            "timezone": SimpleNamespace(utc=None), "UPSTREAM_SHA": cost_guard.UPSTREAM_SHA,
            "TRACER": SimpleNamespace(start_as_current_span=lambda *a, **kw: nullcontext()),
            "authenticate": lambda: ("credential", client),
            "read_foundation": lambda _: self.foundation, "load_foundation": lambda: self.foundation,
            "scope_fingerprint": lambda: studio_config.scope_fingerprint(),
            "version_key": lambda _: version, "checkout": lambda *a: SCRATCH,
            "prepare_models": lambda *a, **kw: (Entity(workflow="workflow"), []),
            "snapshot": lambda *a: [], "build_image": lambda *a: image, "verify_local_snapshot": Mock(),
            "run": Mock(), "register_datastore": Mock(), "upload_tree": Mock(), "write_json": write,
            "blob_service": lambda _: SimpleNamespace(get_container_client=lambda _: container),
            "datastore_url": lambda: "fixture-datastore",
            "az_json": Mock(side_effect=[datastore, {"digest": "sha256:fixture"}]),
            "same_model_uri": lambda actual, expected: actual == expected,
        }
        exec(compile(ast.Module(body=functions, type_ignores=[]), "<model-version>", "exec"), namespace)
        real_verify = namespace["verify_cloud"]
        namespace["verify_cloud"] = Mock()
        with patch.object(sys.modules["azure.ai.ml.entities"], "Environment", Entity, create=True), \
             patch.object(sys.modules["azure.ai.ml.entities"], "Model", Entity, create=True):
            namespace["prepare"](Entity(cache=SCRATCH / "model-version", profile="wan", local_models_path=SCRATCH))
        manifest = write.call_args.args[1]
        model = create_model.call_args.args[0]
        self.assertEqual(model.version, "1")
        self.assertIn(version, model.name)
        self.assertEqual(manifest["modelsRef"], f"azureml:{model.name}:1")
        self.assertEqual(manifest["modelsVersion"], "1")
        previous_cache = SCRATCH / "model-version-reuse"
        previous_cache.mkdir()
        (previous_cache / "prepared-wan.json").write_text(json.dumps({
            "version": "older", "profile": "wan", "models": [],
            "modelsPrefix": "models/prior-verified-wan",
            "scopeFingerprint": "earlier-network-configuration",
        }))
        with patch.object(sys.modules["azure.ai.ml.entities"], "Environment", Entity, create=True), \
             patch.object(sys.modules["azure.ai.ml.entities"], "Model", Entity, create=True):
            namespace["prepare"](Entity(cache=previous_cache, profile="wan", local_models_path=SCRATCH))
        reused = write.call_args.args[1]
        self.assertEqual(reused["modelsPrefix"], "models/prior-verified-wan")
        self.assertEqual(reused["modelsUri"], "azureml://datastores/sample/paths/models/prior-verified-wan/")
        self.assertIn(version, reused["codePrefix"])
        client.models.get.return_value = Entity(path=manifest["modelsUri"])
        real_verify(manifest, "credential", client)
        client.models.get.assert_called_once_with(model.name, "1")
        client.environments.get.assert_called_once_with("sample-wan", version)
        portal = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "portal_settings")
        flags = next(n.value.elts for n in portal.body if isinstance(n, ast.Assign) and
                     any(isinstance(t, ast.Name) and t.id == "flags" for t in n.targets))
        index = next(i for i, flag in enumerate(flags) if isinstance(flag, ast.Constant) and flag.value == "--models-version")
        self.assertEqual(ast.literal_eval(flags[index + 1].slice), "modelsVersion")

    def test_native_command_resolves_windows_shims(self):
        tree = ast.parse((ROOT / "runtime.py").read_text(encoding="utf-8"))
        node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "run")
        execute = Mock(return_value=SimpleNamespace(stdout="ready\n"))
        namespace = {
            "Path": Path,
            "shutil": SimpleNamespace(which=lambda _: r"C:\Program Files\Azure\az.CMD"),
            "subprocess": SimpleNamespace(run=execute, PIPE=subprocess.PIPE),
            "TRACER": SimpleNamespace(start_as_current_span=lambda *a, **kw: nullcontext()),
        }
        exec(compile(ast.Module(body=[node], type_ignores=[]), "<native-run>", "exec"), namespace)
        self.assertEqual(namespace["run"]("az", "version", capture=True), "ready")
        self.assertEqual(execute.call_args.args[0], [r"C:\Program Files\Azure\az.CMD", "version"])

    @classmethod
    def setUpClass(cls):
        cls.settings, cls.foundation = portable_fixture()
        cls.settings_path = SCRATCH / "terraform.tfvars.json"
        cls.foundation_path = SCRATCH / "foundation.json"
        cls.settings_path.write_text(json.dumps(cls.settings))
        cls.foundation_path.write_text(json.dumps(cls.foundation))
        os.environ["WAN_STUDIO_CONFIG"] = str(cls.settings_path)
        os.environ["WAN_STUDIO_FOUNDATION"] = str(cls.foundation_path)
        cls.cli_check = patch.object(cost_guard, "assert_cli_scope")
        cls.cli_check.start()
        cls.addClassCleanup(cls.cli_check.stop)
        cls.manifest = {
            "sourceSha": cost_guard.UPSTREAM_SHA, "profile": "wan", "workflow": "azureml/workflows/wan.json",
            "version": "prepared-version",
            "environmentId": "azureml:prepared:123", "modelsRef": "azureml:prepared-models:123",
            "codeUri": "azureml://datastores/sample/paths/code/123/",
            "computeIdentityClientId": cls.foundation["computeIdentityClientId"],
            "scopeFingerprint": studio_config.scope_fingerprint(),
        }
        cls.path = SCRATCH / "contract.json"
        cls.path.write_text(json.dumps(cls.manifest))
        gate = SCRATCH / "armed.json"
        gate.write_text(json.dumps({"compute": cls.foundation["computeName"], "profile": "wan", "version": "prepared-version"}))
        os.environ["WAN_STUDIO_CONTRACT"] = str(cls.path)
        os.environ["WAN_STUDIO_GATE"] = str(gate)
        os.environ["AZURE_TOKEN_CREDENTIALS"] = "AzureCliCredential"

    def args(self, **changes):
        value = SimpleNamespace(
            subscription_id=self.foundation["subscriptionId"], resource_group=self.foundation["resourceGroupName"],
            workspace_name=self.foundation["workspaceName"], compute=self.foundation["computeName"],
            environment_id=self.manifest["environmentId"], models_path=self.manifest["modelsRef"],
            code_path=self.manifest["codeUri"], workflow=self.manifest["workflow"], profile="wan",
            generated_output_path="azureml://datastores/sample/paths/video-library/current-job/",
            install_runtime_deps=False, batch_size=1, timeout_seconds=7200,
        )
        value.__dict__.update(changes)
        return value

    def test_server_limits_and_single_instance(self):
        for seconds in (1, 600, 7200):
            controls = cost_guard.job_controls(self.args(timeout_seconds=seconds))
            self.assertEqual(controls["limits"].timeout, seconds)
            self.assertEqual(controls["resources"].instance_count, 1)
            self.assertEqual(controls["identity"].client_id, self.foundation["computeIdentityClientId"])
            self.assertEqual(controls["environment_variables"], {})

    def test_server_timeout_seconds_and_iso8601_normalization(self):
        for value in (7200, 7200.0, "7200", "PT2H", "PT120M", "PT7200S", "P0DT2H", timedelta(hours=2)):
            with self.subTest(value=value):
                self.assertEqual(cost_guard.normalize_timeout(value), 7200)
        self.assertEqual(cost_guard.normalize_timeout("PT1H30M0.5S"), 5400.5)
        for value in (None, True, 0, -1, float("nan"), float("inf"), "PT", "P1M", "${{limit}}"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                cost_guard.normalize_timeout(value)
        arm_job = {"properties": {
            "limits": {"timeout": "PT2H"}, "resources": {"instanceCount": 1},
            "computeId": self.foundation["computeId"],
        }}
        cost_guard.verify_server_controls(arm_job, self.args())
        with self.assertRaises(ValueError):
            cost_guard.verify_server_controls(arm_job, self.args(timeout_seconds=600))

    def test_created_server_job_readback_and_fail_closed_cancellation(self):
        # The creation response looks safe; the following GET, not that response, is authoritative.
        created = Entity(name="fixture-job", limits=Entity(timeout=7200), resources=Entity(instance_count=1))
        for timeout, count, compute, allowed in (
            (7200, 1, "sample-gpu", True), ("PT2H", 1, "sample-gpu", True),
            (None, 1, "sample-gpu", False), ("PT4H", 1, "sample-gpu", False),
            ("PT2H", 2, "sample-gpu", False), ("PT2H", None, "sample-gpu", False),
            ("PT2H", True, "sample-gpu", False), ("PT2H", 1, "other", False),
        ):
            server_job = Entity(name="fixture-job", limits=Entity(timeout=timeout),
                                resources=Entity(instance_count=count), compute=compute)
            jobs = SimpleNamespace(get=Mock(return_value=server_job), cancel=Mock(),
                                   create_or_update=Mock(side_effect=AssertionError("No resubmission allowed.")))
            client = SimpleNamespace(jobs=jobs)
            with self.subTest(timeout=timeout, count=count, compute=compute):
                if allowed:
                    self.assertIs(cost_guard.verify_created_job(client, created, self.args()), server_job)
                    jobs.cancel.assert_not_called()
                else:
                    output = io.StringIO()
                    with redirect_stdout(output), self.assertRaises(cost_guard.ServerJobSafetyError):
                        cost_guard.verify_created_job(client, created, self.args())
                    jobs.cancel.assert_called_once_with("fixture-job")
                    self.assertEqual(set(json.loads(output.getvalue())), {"name", "status"})
                jobs.get.assert_called_once_with("fixture-job")
                jobs.create_or_update.assert_not_called()

    def test_server_readback_failure_cancels_once_without_secret_logging(self):
        for cancellation_fails in (False, True):
            sensitive = RuntimeError("https://example.invalid/blob?sig=must-not-be-logged")
            jobs = SimpleNamespace(get=Mock(side_effect=sensitive),
                cancel=Mock(side_effect=sensitive if cancellation_fails else None))
            output = io.StringIO()
            with redirect_stdout(output), self.assertRaises(cost_guard.ServerJobSafetyError):
                cost_guard.verify_created_job(SimpleNamespace(jobs=jobs), Entity(name="fixture-job"), self.args())
            jobs.cancel.assert_called_once_with("fixture-job")
            self.assertNotIn("sig=", output.getvalue())
            self.assertNotIn("must-not-be-logged", output.getvalue())
            if cancellation_fails:
                self.assertIn("RunStop", json.loads(output.getvalue())["status"])

    def test_errors_and_spans_cannot_record_sas_text(self):
        error = RuntimeError("https://example.invalid/input.png?sv=test&sig=sensitive-fixture")
        self.assertNotIn("sig=", cost_guard.public_error(error))
        self.assertNotIn("sensitive-fixture", cost_guard.public_error(error))
        for filename in ("runtime.py", "models.py"):
            tree = ast.parse((ROOT / filename).read_text())
            for call in (n for n in ast.walk(tree) if isinstance(n, ast.Call) and
                         isinstance(n.func, ast.Attribute) and n.func.attr == "start_as_current_span"):
                settings = {k.arg: k.value for k in call.keywords}
                self.assertIs(ast.literal_eval(settings["record_exception"]), False)
                self.assertIs(ast.literal_eval(settings["set_status_on_exception"]), False)
        patch_text = (ROOT / "upstream-cost.patch").read_text()
        self.assertEqual(patch_text.count('+        return web.json_response({"error": aml_submit.public_error(exc)}'), 3)

    def test_bad_runtime_scope_and_assets_fail_closed(self):
        for changes in (
            {"timeout_seconds": 7201}, {"timeout_seconds": 0}, {"timeout_seconds": True},
            {"timeout_seconds": 2.5}, {"compute": "Dedicated"}, {"compute": "ND40rs"},
            {"batch_size": 2}, {"install_runtime_deps": True}, {"profile": "minimax_h3"},
            {"workflow": "other"}, {"subscription_id": "other"}, {"resource_group": "rg-other"},
            {"environment_id": "azureml:author:latest"}, {"code_path": "."}, {"models_path": "./models"},
            {"input_image_url": "https://example.invalid/input.png?sig=not-permitted"},
            {"input_image_url": "azureml://datastores/other/paths/image.png"},
            {"input_image_url": "azureml://datastores/sample/paths/sample/web-inputs/../secret.png"},
            {"generated_output_path": None},
            {"generated_output_path": "https://example.invalid/video.mp4?sig=sample"},
            {"generated_output_path": "azureml://datastores/other/paths/video-library/current/"},
            {"generated_output_path": "azureml://datastores/sample/paths/video-library/../outside/"},
        ):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                cost_guard.job_controls(self.args(**changes))

    def test_python_syntax(self):
        for path in ROOT.glob("*.py"):
            ast.parse(path.read_text(encoding="utf-8"), filename=str(path))

    def test_portable_configuration_and_foundation_fail_closed(self):
        self.assertEqual(studio_config.load_foundation(), self.foundation)
        selected = studio_config.load_config()
        self.assertEqual(selected["max_payg_hourly_usd"], 4)
        self.assertIs(selected["compute_enabled"], False)
        self.assertIs(selected["manage_compute_egress"], False)
        for key, value in (
            ("gpu_subnet_dedicated", False), ("gpu_subnet_dedicated", "true"),
            ("compute_enabled", "false"), ("manage_compute_egress", 1),
            ("egress_public_ip_tags", None), ("egress_public_ip_tags", {"FirstPartyUsage": True}),
            ("max_payg_hourly_usd", True), ("max_payg_hourly_usd", 0),
            ("max_payg_hourly_usd", float("nan")), ("subscription_id", "unselected"),
            ("max_payg_hourly_usd", float("inf")), ("deployment_name", "trailing-"),
            ("deployment_name", "a" * 17),
            ("gpu_subnet_cidr", "10.0.0.1/26"), ("private_dns_zone_ids", {}),
            ("private_endpoint_subnet_id", self.settings["gpu_subnet_id"]),
            ("existing_gpu_nsg_id", ""),
            ("existing_gpu_nsg_id", True),
            ("existing_gpu_nsg_id", self.foundation["networkSecurityGroupId"].replace(
                self.settings["subscription_id"], "99999999-9999-9999-9999-999999999999")),
        ):
            with self.subTest(key=key, value=value):
                self.settings_path.write_text(json.dumps({**self.settings, key: value}))
                try:
                    with self.assertRaises((ValueError, KeyError)):
                        studio_config.load_foundation()
                finally:
                    self.settings_path.write_text(json.dumps(self.settings))
        for key, value in (
            ("tenantId", "10000000-0000-0000-0000-000000000000"), ("deploymentName", "other"),
            ("workspaceId", self.foundation["workspaceId"] + "-other"),
            ("computeId", self.foundation["computeId"] + "-other"),
            ("registryLoginServer", "other.azurecr.io"),
            ("workspaceIdentityId", self.foundation["workspaceIdentityId"].replace("rg-sample", "rg-other")),
            ("maxJobSeconds", 7201), ("instanceCount", True), ("ownershipTags", {}),
            ("manageComputeEgress", 0), ("computeEnabled", 0),
        ):
            with self.subTest(key=key):
                self.foundation_path.write_text(json.dumps({**self.foundation, key: value}))
                try:
                    with self.assertRaises(ValueError):
                        studio_config.load_foundation()
                finally:
                    self.foundation_path.write_text(json.dumps(self.foundation))
        with patch.object(studio_config.os, "environ", {"WAN_STUDIO_CONFIG": "relative.json"}), self.assertRaises(ValueError):
            studio_config.load_config()

    def test_config_helper_import_does_not_require_customer_files(self):
        spec = importlib.util.spec_from_file_location("unconfigured_studio_fixture", ROOT / "config.py")
        imported = importlib.util.module_from_spec(spec)
        with patch.object(studio_config.os, "environ", {}):
            spec.loader.exec_module(imported)
            self.assertEqual(imported.package_index_url(), "https://pypi.org/simple")
            with self.assertRaises(KeyError):
                imported.load_config()

    def test_customer_nsg_receipt_must_match_explicit_selection(self):
        customer_nsg = self.foundation["networkSecurityGroupId"].replace("rg-sample", "customer-network")
        try:
            self.foundation_path.write_text(json.dumps({**self.foundation, "networkSecurityGroupId": customer_nsg}))
            with self.assertRaises(ValueError):
                studio_config.load_foundation()
            self.settings_path.write_text(json.dumps({**self.settings, "existing_gpu_nsg_id": customer_nsg}))
            self.assertEqual(studio_config.load_foundation()["networkSecurityGroupId"], customer_nsg)
            self.foundation_path.write_text(json.dumps(self.foundation))
            with self.assertRaises(ValueError):
                studio_config.load_foundation()
        finally:
            self.settings_path.write_text(json.dumps(self.settings))
            self.foundation_path.write_text(json.dumps(self.foundation))

    def test_configurable_hourly_price_ceiling(self):
        try:
            for price in (0.01, 3.99, 4, 4.0, 4.01, 6.5):
                self.settings_path.write_text(json.dumps({**self.settings, "max_payg_hourly_usd": price}))
                with self.subTest(price=price):
                    self.assertEqual(studio_config.load_config()["max_payg_hourly_usd"], price)
            for price in (0, -1, True, "4", float("inf"), float("nan")):
                self.settings_path.write_text(json.dumps({**self.settings, "max_payg_hourly_usd": price}))
                with self.subTest(price=price), self.assertRaises(ValueError):
                    studio_config.load_config()
        finally:
            self.settings_path.write_text(json.dumps(self.settings))

    def test_exact_foundation_ownership_tags(self):
        tags = self.foundation["ownershipTags"]
        for key in tags:
            for changed in ({k: v for k, v in tags.items() if k != key}, {**tags, key: "other"}):
                self.foundation_path.write_text(json.dumps({**self.foundation, "ownershipTags": changed}))
                try:
                    with self.assertRaises(ValueError):
                        studio_config.load_foundation()
                finally:
                    self.foundation_path.write_text(json.dumps(self.foundation))
        long_name = "sample-studio-ab"
        self.assertEqual(len(long_name), 16)
        self.settings_path.write_text(json.dumps({**self.settings, "deployment_name": long_name}))
        try:
            self.assertEqual(studio_config.load_config()["deployment_name"], long_name)
        finally:
            self.settings_path.write_text(json.dumps(self.settings))

    def test_generated_terraform_receipts_match_runtime_admission(self):
        infra = ROOT.parent / "infra"
        settings = json.loads((infra / "terraform.tfvars.json.example").read_text(encoding="utf-8-sig"))
        selected = SCRATCH / "terraform-receipt-config.json"
        contract = SCRATCH / "terraform-receipt-contract.json"
        gate = SCRATCH / "terraform-receipt-gate.json"
        for state, enabled in (("off", False), ("on", True), ("customer-nsg", False)):
            receipt = infra / "tests" / "fixtures" / f"studio-{state}.json"
            foundation = json.loads(receipt.read_text(encoding="utf-8-sig"))
            selected.write_text(json.dumps({
                **settings, "compute_enabled": enabled, "manage_compute_egress": enabled,
                "existing_gpu_nsg_id": foundation["networkSecurityGroupId"] if state == "customer-nsg" else None,
            }))
            environment = {**os.environ, "WAN_STUDIO_CONFIG": str(selected),
                           "WAN_STUDIO_FOUNDATION": str(receipt), "WAN_STUDIO_CONTRACT": str(contract),
                           "WAN_STUDIO_GATE": str(gate)}
            with self.subTest(state=state), patch.object(studio_config.os, "environ", environment):
                self.assertEqual(studio_config.load_foundation(), foundation)
                self.assertIs(foundation["computeEnabled"], enabled)
                manifest = {
                    **self.manifest, "scopeFingerprint": studio_config.scope_fingerprint(),
                    "computeIdentityClientId": foundation["computeIdentityClientId"],
                    "codeUri": f"azureml://datastores/{foundation['datastoreName']}/paths/code/prepared/",
                }
                contract.write_text(json.dumps(manifest))
                gate.write_text(json.dumps({
                    "compute": foundation["computeName"], "profile": "wan", "version": manifest["version"],
                }))
                args = self.args(
                    subscription_id=foundation["subscriptionId"], resource_group=foundation["resourceGroupName"],
                    workspace_name=foundation["workspaceName"], compute=foundation["computeName"],
                    code_path=manifest["codeUri"],
                    generated_output_path=f"azureml://datastores/{foundation['datastoreName']}/paths/video-library/current/",
                    input_image_url=(f"azureml://datastores/{foundation['datastoreName']}/paths/"
                                     f"{foundation['deploymentName']}/web-inputs/fixture.png"),
                )
                controls = cost_guard.job_controls(args)
                self.assertEqual(controls["identity"].client_id, foundation["computeIdentityClientId"])
                cost_guard.verify_server_controls(Entity(
                    limits=controls["limits"], resources=controls["resources"], compute=foundation["computeId"],
                ), args)

    def test_credential_is_cli_only_and_requires_matching_account(self):
        execute = Mock(return_value=SimpleNamespace(stdout=json.dumps({
            "id": self.settings["subscription_id"], "tenantId": self.settings["tenant_id"],
        })))
        credential = Mock(return_value=Entity())
        identity = ModuleType("azure.identity")
        identity.AzureCliCredential = credential
        with patch.dict(sys.modules, {"azure.identity": identity}), \
             patch.object(studio_config.shutil, "which", return_value="az-fixture"), \
             patch.object(studio_config.subprocess, "run", execute), \
             patch.object(studio_config.os, "environ", {**os.environ, "AZURE_CLIENT_SECRET": "unused-fixture",
                                                        "AZURE_TOKEN_CREDENTIALS": "unapproved"}):
            studio_config.build_credential()
            credential.assert_called_once_with(tenant_id=self.settings["tenant_id"])
            self.assertEqual(execute.call_args.args[0][:3], ["az-fixture", "account", "show"])
            for wrong in ("id", "tenantId"):
                execute.return_value.stdout = json.dumps({
                    "id": self.settings["subscription_id"], "tenantId": self.settings["tenant_id"],
                    wrong: "10000000-0000-0000-0000-000000000000",
                })
                with self.assertRaises(ValueError):
                    studio_config.build_credential()
            self.assertEqual(credential.call_count, 1)

    def test_configuration_and_helper_changes_invalidate_assets_not_compute_switch(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "version_key")
        namespace = {"Path": Path, "HERE": ROOT, "hashlib": hashlib, "UPSTREAM_SHA": cost_guard.UPSTREAM_SHA,
                     "scope_fingerprint": studio_config.scope_fingerprint, "package_index_url": lambda: studio_config.PUBLIC_PYPI_INDEX}
        exec(compile(ast.Module(body=[node], type_ignores=[]), "<version-key>", "exec"), namespace)
        cache = SCRATCH / "version-cache"
        (cache / "python-project").mkdir(parents=True)
        lock = cache / "python-project" / "uv.lock"
        shutil.copy2(ROOT / "uv.lock", lock)
        version = namespace["version_key"](cache)
        try:
            self.settings_path.write_text(json.dumps({**self.settings, "compute_enabled": True}))
            self.foundation_path.write_text(json.dumps({**self.foundation, "computeEnabled": True}))
            self.assertEqual(version, namespace["version_key"](cache))
            self.settings_path.write_text(json.dumps({
                **self.settings, "operator_principal_id": "10000000-0000-0000-0000-000000000000",
            }))
            self.assertNotEqual(version, namespace["version_key"](cache))
            with self.assertRaises(ValueError):
                cost_guard.job_controls(self.args())
        finally:
            self.settings_path.write_text(json.dumps(self.settings))
            self.foundation_path.write_text(json.dumps(self.foundation))
        self.assertIn("config.py", ast.unparse(node))
        lock.write_text("changed")
        with self.assertRaises(ValueError):
            namespace["version_key"](cache)

    def test_explicit_package_index_rejects_auth_and_shell_injection(self):
        with patch.object(studio_config.os, "environ", {
            "PIP_INDEX_URL": "http://unused.example", "UV_DEFAULT_INDEX": "http://unused.example",
        }):
            self.assertEqual(studio_config.package_index_url(), "https://pypi.org/simple")
        with patch.object(studio_config.os, "environ", {"WAN_STUDIO_PYPI_INDEX": "https://mirror.example.org/simple/"}):
            self.assertEqual(studio_config.package_index_url(), "https://mirror.example.org/simple")
        for index in ("http://pypi.org/simple", "https://user:secret@mirror.example.org/simple",
                      "https://mirror.example.org/simple?token=value", "https://mirror.example.org/simple#x",
                      "https://mirror.example.org/$(command)", "https://mirror.example.org/\nRUN unsafe"):
            with patch.object(studio_config.os, "environ", {"WAN_STUDIO_PYPI_INDEX": index}), self.assertRaises(ValueError):
                studio_config.package_index_url()

    def test_stop_gate_disables_submissions(self):
        gate = Path(os.environ["WAN_STUDIO_GATE"])
        original = gate.read_text()
        try:
            gate.unlink()
            with self.assertRaises(FileNotFoundError):
                cost_guard.job_controls(self.args())
            gate.write_text(json.dumps({"compute": self.foundation["computeName"], "profile": "wan", "version": "other"}))
            with self.assertRaises(ValueError):
                cost_guard.job_controls(self.args())
        finally:
            gate.write_text(original)

    def test_snapshot_excludes_secrets_and_large_assets(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "safe_snapshot_path")
        namespace = {"Path": Path}
        exec(compile(ast.Module(body=[node], type_ignores=[]), "<snapshot>", "exec"), namespace)
        safe = namespace["safe_snapshot_path"]
        for path in (".git/config", ".env", "azureml/.env.secret", "src/.env", "src/models/a.safetensors",
                     "src/output/secret.mp4", "azureml/outputs/run.json", "privatekeys/key.pem"):
            self.assertFalse(safe(path), path)
        self.assertTrue(safe("src/main.py"))
        self.assertTrue(safe("azureml/web_submit.py"))
        self.assertTrue(safe("src/comfy/ldm/models/autoencoder.py"))
        self.assertTrue(safe("src/comfy_api/input/video_types.py"))
        self.assertFalse(safe("src/custom_nodes/plugin/models/weights.safetensors"))
        self.assertFalse(safe("src/comfy/ldm/models/weights.pth"))

    def test_cpu_validation_uses_staged_snapshot_and_required_workflow_nodes(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "verify_local_snapshot")
        run = Mock()
        namespace = {"Path": Path, "json": json, "run": run}
        exec(compile(ast.Module(body=[node], type_ignores=[]), "<snapshot-runtime>", "exec"), namespace)
        staged = SCRATCH / "staged-code"
        staged.mkdir()
        (staged / "workflow.json").write_text(json.dumps({"1": {"class_type": "WanImageToVideo"}}))
        namespace["verify_local_snapshot"]("local-image@sha256:fixture", staged, "workflow.json")
        arguments = run.call_args.args
        self.assertEqual(arguments[arguments.index("--network") + 1], "none")
        self.assertEqual(arguments[arguments.index("--pull") + 1], "never")
        self.assertIn(f"type=bind,source={staged.resolve()},target=/code,readonly", arguments)
        self.assertIn("NVIDIA_VISIBLE_DEVICES=void", arguments)
        self.assertIn("CUDA_VISIBLE_DEVICES=", arguments)
        self.assertIn("'WanImageToVideo'", arguments[-1])
        self.assertIn("'--cpu'", arguments[-1])
        self.assertIn("'--disable-all-custom-nodes'", arguments[-1])
        self.assertIn("'--temp-directory','/check/temp'", arguments[-1])
        self.assertIn("'--database-url','sqlite:////check/comfyui.db'", arguments[-1])
        self.assertIn("/check:rw,mode=1777", arguments)
        self.assertLess(arguments[-1].index("import main"), arguments[-1].index("import nodes"))
        self.assertNotIn("--gpus", arguments)
        namespace["verify_local_snapshot"]("local-image@sha256:fixture", staged, "workflow.json", "minimax_h3")
        h3_arguments = run.call_args.args
        self.assertEqual(h3_arguments[h3_arguments.index("--workdir") + 1], "/opt/comfy-h3")
        self.assertIn("sys.path.insert(0,'/opt/comfy-h3')", h3_arguments[-1])
        prepare = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "prepare")
        call = next(n for n in ast.walk(prepare) if isinstance(n, ast.Call) and
                    isinstance(n.func, ast.Name) and n.func.id == "verify_local_snapshot")
        self.assertEqual(ast.unparse(call.args[1]), "target / 'code'")

    def test_verified_local_model_reuse_and_copy_fallback(self):
        tree = ast.parse((ROOT / "models.py").read_text())
        functions = [n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ("sha256", "stage_local_model")]
        namespace = {"Path": Path, "hashlib": hashlib, "shutil": shutil}
        exec(compile(ast.Module(body=functions, type_ignores=[]), "<model-cache>", "exec"), namespace)
        source, destination = SCRATCH / "model-source.bin", SCRATCH / "model-staged.bin"
        payload = b"tiny-offline-cache-fixture"
        source.write_bytes(payload)
        checksum = hashlib.sha256(payload).hexdigest()
        stage = namespace["stage_local_model"]
        stage(source, destination, checksum, len(payload))
        self.assertEqual(destination.read_bytes(), payload)
        destination.unlink()
        with patch.object(Path, "hardlink_to", side_effect=OSError("cross-device fixture")):
            stage(source, destination, checksum, len(payload))
        self.assertEqual(destination.read_bytes(), payload)
        for test_source, digest, size in (
            (SCRATCH / "absent.bin", checksum, len(payload)),
            (source, "0" * 64, len(payload)),
            (source, checksum, len(payload) + 1),
        ):
            with self.assertRaises(ValueError):
                stage(test_source, destination, digest, size)
        self.assertEqual(destination.read_bytes(), payload)
        self.assertFalse(destination.with_suffix(".bin.partial").exists())

    def test_wan_revision_pins(self):
        tree = ast.parse((ROOT / "models.py").read_text())
        assignment = next(n for n in tree.body if isinstance(n, ast.Assign) and
            any(isinstance(t, ast.Name) and t.id == "WAN_REVISIONS" for t in n.targets))
        self.assertEqual(ast.literal_eval(assignment.value), {
            "Comfy-Org/Wan_2.2_ComfyUI_Repackaged": "c4f60d30c55a624e35427060fdd217579a6c1d77",
            "Comfy-Org/Wan_2.1_ComfyUI_repackaged": "617a7633e636506f850e043bc4605f290a466a8e",
        })

    def model_namespace(self):
        tree = ast.parse((ROOT / "models.py").read_text())
        nodes = [n for n in tree.body if
            (isinstance(n, ast.FunctionDef) and n.name in ("load_wan_manifest", "prepare_models")) or
            (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "WAN_REVISIONS" for t in n.targets))]
        namespace = {"__file__": str(ROOT / "models.py"), "json": json, "Path": Path, "re": re, "quote": quote}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<pinned-models>", "exec"), namespace)
        return namespace

    def test_wan_manifest_integrity(self):
        load = self.model_namespace()["load_wan_manifest"]
        entries = load()
        self.assertEqual(len(entries), 4)
        self.assertEqual(sum(e["size"] for e in entries), 35579207879)
        invalid = SCRATCH / "bad-manifest.json"
        for changed in (
            entries[:3],
            entries[:3] + entries[:1],
            [{**entries[0], "size": 0}] + entries[1:],
            [{**entries[0], "sha256": "unverified"}] + entries[1:],
            [{**entries[0], "url": entries[0]["url"].replace("c4f60d30c55a624e35427060fdd217579a6c1d77", "main")}] + entries[1:],
        ):
            invalid.write_text(json.dumps(changed))
            with self.assertRaises(ValueError):
                load(invalid)

    def test_wan_local_prepare_needs_no_model_metadata_or_weight_download(self):
        namespace = self.model_namespace()
        entries = namespace["load_wan_manifest"]()
        sources = {(e["folder_name"], e["filename"]): e["url"] for e in entries}
        staged, requested = [], []
        def no_model_network(*_):
            raise AssertionError("Local WAN preparation attempted model metadata/weight download.")
        def model_card(url, **_):
            requested.append(url)
            return io.BytesIO(b"---\nlicense: apache-2.0\n---\nPinned Comfy-Org model card.")
        namespace.update({
            "workflow_sources": lambda *_: (SimpleNamespace(key="wan"), sources),
            "public_json": no_model_network, "download_model": no_model_network,
            "stage_local_model": lambda *args: staged.append(args),
            "TRACER": SimpleNamespace(start_as_current_span=lambda *_, **__: nullcontext()),
            "urllib": SimpleNamespace(request=SimpleNamespace(urlopen=model_card)),
        })
        local = SCRATCH / "local-wan"
        local.mkdir()
        _, records = namespace["prepare_models"](SCRATCH, "wan", SCRATCH / "wan-stage" / "models", local_models=local)
        self.assertEqual(len(staged), 4)
        self.assertEqual(sum(r["bytes"] for r in records), 35579207879)
        self.assertEqual({r["sha256"] for r in records}, {e["sha256"] for e in entries})
        self.assertEqual(len(requested), 2)
        self.assertTrue(all(url.endswith("/README.md") and "/resolve/" in url for url in requested))

    def test_image_base_digest_and_cpu_only_validation(self):
        tree = ast.parse((ROOT / "runtime.py").read_text())
        nodes = [n for n in tree.body if
            (isinstance(n, ast.FunctionDef) and n.name in ("pin_runtime_base", "build_local_image", "build_image")) or
            (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and
                t.id in ("BASE_IMAGE_TAG", "BASE_IMAGE") for t in n.targets))]
        calls = []
        def fake_run(*arguments, **_):
            calls.append(arguments)
            return "linux" if arguments[:2] == ("docker", "info") else None
        namespace = {"Path": Path, "shutil": shutil, "re": re, "run": fake_run,
                     "load_foundation": lambda: self.foundation, "package_index_url": lambda: studio_config.PUBLIC_PYPI_INDEX,
                     "az_json": lambda *_: {"digest": "sha256:" + "0" * 64}}
        exec(compile(ast.Module(body=nodes, type_ignores=[]), "<image-build>", "exec"), namespace)
        source = f"FROM {namespace['BASE_IMAGE_TAG']}\n\nRUN true\n"
        pinned = namespace["pin_runtime_base"](source)
        self.assertEqual(pinned.splitlines()[0],
            "FROM pytorch/pytorch:2.8.0-cuda12.8-cudnn9-runtime@sha256:417bd75df6365104c283ea4c1651fb3530d9eb5a4c2fafa51943cff2a94e6385")
        for unsafe in ("FROM unknown:latest\n", source + "FROM unknown:latest\n"):
            with self.assertRaises(ValueError):
                namespace["pin_runtime_base"](unsafe)
        root = SCRATCH / "image-source"
        context = root / "azureml" / "context"
        context.mkdir(parents=True)
        (context / "Dockerfile").write_text(source)
        (context / "requirements-azureml.txt").write_text("")
        namespace["build_image"](root, SCRATCH / "image-cache", "fixture", "wan")
        self.assertFalse(any("--gpus" in command for command in calls))
        build = next(command for command in calls if command[:2] == ("docker", "build"))
        self.assertIn("PIP_INDEX_URL=https://pypi.org/simple", build)
        self.assertIn("ENV PIP_INDEX_URL=${PIP_INDEX_URL}", pinned)
        self.assertNotIn("--trusted-host", pinned)
        validation = next(command for command in calls if command[:2] == ("docker", "run"))
        self.assertIn("CUDA_VISIBLE_DEVICES=", validation)
        self.assertIn("NVIDIA_VISIBLE_DEVICES=void", validation)
        self.assertIn("none", validation)
        self.assertIn("assert not torch.cuda.is_available()", validation[-1])

    def test_public_package_index_and_lock(self):
        project = tomllib.loads((ROOT / "pyproject.toml").read_text())
        self.assertNotIn("index", project["tool"]["uv"])
        lock = tomllib.loads((ROOT / "uv.lock").read_text())
        registries = {p["source"]["registry"].rstrip("/") for p in lock["package"] if "registry" in p["source"]}
        self.assertEqual(registries, {"https://pypi.org/simple"})
        urls = [artifact["url"] for package in lock["package"]
                for artifact in ([package["sdist"]] if "sdist" in package else []) + package.get("wheels", [])]
        self.assertTrue(urls)
        parts = [urlsplit(url) for url in urls]
        self.assertTrue({part.hostname for part in parts}.issubset({
            "files.pythonhosted.org",
        }))
        self.assertTrue(all(part.scheme == "https" and not part.username and not part.password and not part.query
                            for part in parts))

    def test_patch_covers_both_factories_and_disables_paid_model_downloads(self):
        patch = (ROOT / "upstream-cost.patch").read_text()
        self.assertIn("+    controls = job_controls(args)", patch)
        self.assertIn("+        **controls,", patch)
        self.assertIn("+    controls = aml_submit.job_controls(submit_args)", patch)
        self.assertIn("+        \"models\": aml_submit.build_models_input", patch)
        self.assertIn('-HF_HOME="$H3_WORK_DIR/hf-cache" hf download', patch)
        self.assertIn('+        raise FileNotFoundError(f"Prepare is required', patch)
        self.assertNotIn("+  --run-timeout 14400", patch)
        self.assertIn("+    from .config import build_credential", patch)
        self.assertIn("+_foundation = load_foundation()", patch)
        self.assertIn("+    if args.negative_prompt is None:", patch)

    def test_patch_applies_exact_pinned_upstream_when_fixture_available(self):
        fixture = os.environ.get("WAN_STUDIO_UPSTREAM_TEST_ROOT")
        if not fixture:
            self.skipTest("Set WAN_STUDIO_UPSTREAM_TEST_ROOT for exact pinned patch and both factory execution checks.")
        source = Path(fixture)
        head = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
        self.assertEqual(head, cost_guard.UPSTREAM_SHA)
        work = SCRATCH / "upstream"
        work.mkdir()
        for name in ("register_and_submit.py", "web_submit.py", "bootstrap_ltx_and_run.py", "credential.py", "defaults.py"):
            relative = f"azureml/{name}"
            destination = work / relative
            destination.parent.mkdir(exist_ok=True)
            content = subprocess.check_output(["git", "-C", str(source), "show", f"HEAD:{relative}"])
            destination.write_bytes(content)
        subprocess.run(["git", "init", "-q", str(work)], check=True)
        subprocess.run(["git", "-C", str(work), "apply", "--ignore-space-change", "--check", str(ROOT / "upstream-cost.patch")], check=True)
        subprocess.run(["git", "-C", str(work), "apply", "--ignore-space-change", str(ROOT / "upstream-cost.patch")], check=True)
        # Execute each real modified factory against a recording AzureML command, not merely grep.
        for name in ("register_and_submit.py", "web_submit.py"):
            tree = ast.parse((work / "azureml" / name).read_text())
            submit_name = "create_job_submission" if name.startswith("register") else "submit_minimax_h3_job"
            submission = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == submit_name)
            creation = next(n for n in ast.walk(submission) if isinstance(n, ast.Call) and
                isinstance(n.func, ast.Attribute) and n.func.attr == "create_or_update")
            readback = next(n for n in ast.walk(submission) if isinstance(n, ast.Call) and
                ((isinstance(n.func, ast.Name) and n.func.id == "verify_created_job") or
                 (isinstance(n.func, ast.Attribute) and n.func.attr == "verify_created_job")))
            self.assertGreater(readback.lineno, creation.lineno)
            self.assertLess(readback.lineno, next(n for n in ast.walk(submission) if isinstance(n, ast.Return)).lineno)
            function_name = "build_job" if name.startswith("register") else "build_minimax_h3_job"
            node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == function_name)
            self.assertTrue(any(isinstance(n, ast.Call) and
                ((isinstance(n.func, ast.Name) and n.func.id == "job_controls") or
                 (isinstance(n.func, ast.Attribute) and n.func.attr == "job_controls"))
                for n in ast.walk(node)))
            returns = [n for n in ast.walk(node) if isinstance(n, ast.Return) and isinstance(n.value, ast.Call)]
            self.assertTrue(any(
                any(k.arg is None and isinstance(k.value, ast.Name) and k.value.id in ("controls", "command_kwargs")
                    for k in n.value.keywords) for n in returns))
            args = self.args()
            for key in ("positive_prompt", "negative_prompt", "seed", "steps", "cfg", "sampler_name", "scheduler",
                        "denoise", "width", "height", "duration_seconds", "length", "fps", "filename_prefix",
                        "input_image_url", "input_image_filename"):
                setattr(args, key, None)
            args.version = "test"
            args.negative_prompt = ""
            args.input_image_url = "azureml://datastores/sample/paths/sample/web-inputs/fixture.png"
            namespace = {
                "job_controls": cost_guard.job_controls, "shlex": shlex, "Path": Path,
                "Input": Entity, "Output": Entity, "command": lambda **kw: Entity(**kw),
                "build_models_input": lambda path, **kw: Entity(path=path), "models_asset_kind": lambda _: "model",
                "all_image_input_keys": lambda: ["input_image"], "__file__": str(work / "azureml" / name),
                "json": json, "base64": base64, "re": re, "uuid": uuid,
                "MINIMAX_H3_LAUNCHER": "pass", "MINIMAX_H3_MODEL_FILES": ("model",),
                "MINIMAX_H3_COMFY_REVISION": "95d755cd8107a72258d452b5d3657273d571f07d",
                "MINIMAX_H3_PROFILE_KEY": "minimax_h3",
                "workflow_utils": SimpleNamespace(load_workflow_payload=lambda _: {}, patch_prompt=lambda *_: None),
            }
            namespace["aml_submit"] = SimpleNamespace(
                job_controls=cost_guard.job_controls, Input=Entity, Output=Entity,
                command=lambda **kw: Entity(**kw), build_models_input=lambda path, **kw: Entity(path=path))
            compiled = ast.Module(body=[ast.ImportFrom(module="__future__",
                names=[ast.alias(name="annotations")], level=0), node], type_ignores=[])
            ast.fix_missing_locations(compiled)
            exec(compile(compiled, str(work / "azureml" / name), "exec"), namespace)
            if function_name == "build_job":
                profile = SimpleNamespace(uses_ltx_runner=False, comfy_low_vram=False, comfy_disable_smart_memory=False,
                    comfy_reserve_vram_gb=None, submit_prompt_input_name="positive_prompt", enable_custom_nodes=False,
                    experiment_name="test", display_name_prefix="test")
                job = namespace[function_name](args, work, environment_ref=args.environment_id,
                    models_ref=args.models_path, profile=profile)
                self.assertNotIn("negative_prompt", job.inputs)
                self.assertIn('--negative-prompt ""', job.command)
                args.negative_prompt = "avoid artifacts"
                nonempty = namespace[function_name](args, work, environment_ref=args.environment_id,
                    models_ref=args.models_path, profile=profile)
                self.assertEqual(nonempty.inputs["negative_prompt"], "avoid artifacts")
                self.assertIn('${{inputs.negative_prompt}}', nonempty.command)
            else:
                args.models_asset_kind = "model"
                job = namespace[function_name](args)
                self.assertIn("inputs.models", job.command)
                self.assertNotIn("hf download", job.command)
                self.assertNotIn("pip install", job.command)
            self.assertEqual(job.limits.timeout, 7200)
            self.assertEqual(job.resources.instance_count, 1)
            self.assertEqual(job.environment_variables, {})
            self.assertEqual(job.inputs["input_image_url"].type, "uri_file")
            self.assertIn("file://", job.command)
            self.assertNotIn("sig=", job.command)
            args.timeout_seconds = 7201
            with self.assertRaises(ValueError):
                if function_name == "build_job":
                    namespace[function_name](args, work, environment_ref=args.environment_id,
                        models_ref=args.models_path, profile=profile)
                else:
                    namespace[function_name](args)
        self.assertIn("**controls", (work / "azureml" / "register_and_submit.py").read_text())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    SCRATCH = args.scratch.resolve()
    SCRATCH.mkdir(parents=True, exist_ok=True)
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(Controls))
    raise SystemExit(0 if result.wasSuccessful() else 1)
