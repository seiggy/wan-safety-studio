"""Offline fault injection; no tokens, Azure endpoints, Docker or package downloads."""
import copy
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from types import SimpleNamespace
import unittest
import urllib.error
from unittest.mock import patch

from azure.core.exceptions import ResourceExistsError, ResourceNotFoundError

import pipeline
import foundation
import publish_region
import publish_trainer
from pipeline import immutable_bytes, json_bytes, snapshot, snapshot_id

ROOT = pipeline.ROOT


def fixture_json():
    """The golden contract output plus the pipeline-only wiring values the tool merges in from the settings document."""
    golden = json.loads((ROOT / "contracts/fixtures/studio.json").read_text())
    regions = json.loads((ROOT / "contracts/fixtures/studio-settings.json").read_text())["Pipeline"]["Regions"]
    for key, wiring_values in regions.items():
        golden["foundation"]["regions"][key].update(
            workspaceIdentityId=wiring_values["WorkspaceIdentityId"], keyVaultId=wiring_values["KeyVaultId"])
    return golden


def workspace_properties(target):
    return {"properties": {
        "publicNetworkAccess": "Disabled", "allowPublicAccessWhenBehindVnet": False, "systemDatastoresAuthMode": "Identity",
        "primaryUserAssignedIdentity": target.get("workspaceIdentityId"), "storageAccount": target["storageAccountId"],
        "containerRegistry": target["registryId"], "keyVault": target.get("keyVaultId"),
    }}


class Blob:
    def __init__(self, container, name):
        self.container, self.name = container, name

    def upload_blob(self, content, **kwargs):
        if self.name in self.container.data:
            raise ResourceExistsError()
        self.container.data[self.name] = content.read() if hasattr(content, "read") else content

    def download_blob(self):
        if self.name not in self.container.data:
            raise ResourceNotFoundError()
        value = self.container.data[self.name]
        return SimpleNamespace(readall=lambda: value, chunks=lambda: iter([value]))


class Container:
    def __init__(self):
        self.data = {}

    def get_blob_client(self, name):
        return Blob(self, name)

    def list_blobs(self, name_starts_with):
        return [SimpleNamespace(name=name) for name in self.data if name.startswith(name_starts_with)]


class PipelineTests(unittest.TestCase):
    def test_tool_error_tails_are_shown_but_secret_shaped_text_is_redacted(self):
        text = ("line 1\nERROR: push failed: https://acr.blob.core.windows.net/x?sig=ABCDEFG123456&se=2030 denied\n"
                "Authorization: Bearer abcdefghijklmnop\ntoken=supersecretvalue\n"
                "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.sgnaturevalue123\nregistry unreachable: dial tcp 10.0.0.4:443: i/o timeout")
        shown = pipeline.redacted_tail(text)
        for visible in ("push failed", "dial tcp 10.0.0.4:443: i/o timeout", "sig=[redacted]"):
            self.assertIn(visible, shown)
        for hidden in ("ABCDEFG123456", "abcdefghijklmnop", "supersecretvalue", "eyJhbGci", "sgnaturevalue123"):
            self.assertNotIn(hidden, shown)
        self.assertEqual("no error output", pipeline.redacted_tail(""))

    def test_build_provenance_uses_neutral_names_with_a_github_fallback(self):
        names = ("STUDIO_BUILD_RUN_ID", "GITHUB_RUN_ID", "STUDIO_BUILD_OUTPUT", "GITHUB_OUTPUT", "STUDIO_FEDERATED_IDENTITY", "GITHUB_ACTIONS")
        with patch.dict(os.environ, {}, clear=False):
            for name in names:
                os.environ.pop(name, None)
            with self.assertRaisesRegex(ValueError, "STUDIO_BUILD_RUN_ID is required"):
                pipeline.build_var("RUN_ID")
            self.assertIsNone(pipeline.build_var("OUTPUT", required=False))
            self.assertFalse(pipeline.federated_identity_required())
            os.environ["GITHUB_RUN_ID"] = "7"
            self.assertEqual("7", pipeline.build_var("RUN_ID"))
            os.environ["STUDIO_BUILD_RUN_ID"] = "9"
            self.assertEqual("9", pipeline.build_var("RUN_ID"))
            os.environ["STUDIO_FEDERATED_IDENTITY"] = "true"
            self.assertTrue(pipeline.federated_identity_required())

    def test_a_python_child_sees_the_locked_packages_of_the_running_interpreter(self):
        output = pipeline.run(sys.executable, "-c", "import opentelemetry, sys; print(opentelemetry.__path__[0])")
        self.assertIn("opentelemetry", output)
        with patch.dict(os.environ, {"PYTHONPATH": ""}):
            self.assertIn("site-packages", pipeline.run(sys.executable, "-c", "import os; print(os.environ['PYTHONPATH'])"))

    def test_gpu_build_caches_are_optional_and_a_set_folder_is_created_when_missing(self):
        import build_image
        with patch.dict(os.environ, {"STUDIO_UPSTREAM_SOURCE": ""}):
            self.assertIsNone(build_image.gpu_cache("STUDIO_UPSTREAM_SOURCE"))
        with patch.dict(os.environ, {"STUDIO_UPSTREAM_SOURCE": "relative/path"}):
            with self.assertRaisesRegex(ValueError, "absolute path"):
                build_image.gpu_cache("STUDIO_UPSTREAM_SOURCE")
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp) / "new" / "cache"
            with patch.dict(os.environ, {"STUDIO_MODEL_CACHE": str(folder)}):
                self.assertEqual(str(folder), build_image.gpu_cache("STUDIO_MODEL_CACHE"))
            self.assertTrue(folder.is_dir())
    def test_the_pinned_upstream_is_fetched_by_exact_sha_once_and_a_reused_folder_is_not_fetched_again(self):
        import build_image
        pin = json.loads((ROOT / "build/gpu/profiles.json").read_text())["upstream"]
        calls = []

        def fake_run(*args, **kwargs):
            calls.append([str(x) for x in args])
            if args[3] == "rev-parse" and not (Path(args[2]) / "has-commit").exists():
                raise RuntimeError("missing commit")
            return ""

        with tempfile.TemporaryDirectory() as temp, patch("build_image.run", side_effect=fake_run):
            destination = Path(temp) / "upstream"
            self.assertEqual(str(destination), build_image.fetch_upstream(destination))
            self.assertEqual(["init", "rev-parse", "fetch"], [call[3] for call in calls])
            self.assertEqual([pin["repository"], pin["sourceSha"]], calls[2][-2:])
            self.assertIn("--depth", calls[2])
            calls.clear()
            (destination / ".git").mkdir()
            (destination / "has-commit").write_text("x")
            build_image.fetch_upstream(destination)
            self.assertEqual(["rev-parse"], [call[3] for call in calls])
            (Path(temp) / "plain").mkdir()
            (Path(temp) / "plain" / "file.txt").write_text("x")
            with self.assertRaisesRegex(ValueError, "not a git repository"):
                build_image.fetch_upstream(Path(temp) / "plain")

    def test_an_expired_federated_login_is_refreshed_once_and_only_when_oidc_is_available(self):
        from azure.core.exceptions import ClientAuthenticationError
        calls = []

        class Inner:
            def get_token(self, *scopes, **kwargs):
                calls.append("get")
                if len(calls) == 1:
                    raise ClientAuthenticationError("AADSTS700024: Client assertion is not within its valid time range.")
                return "token"

        credential = pipeline.RefreshingCliCredential.__new__(pipeline.RefreshingCliCredential)
        credential.inner, credential.tenant_id, credential.client_id, credential.subscription_id = Inner(), "t", "c", "s"
        with patch.object(pipeline.RefreshingCliCredential, "relogin", return_value=True) as relogin:
            self.assertEqual("token", credential.get_token("scope"))
        self.assertEqual(1, relogin.call_count)
        self.assertEqual(["get", "get"], calls)

        calls.clear()
        with patch.object(pipeline.RefreshingCliCredential, "relogin", return_value=False):
            with self.assertRaises(ClientAuthenticationError):
                credential.get_token("scope")
        self.assertEqual(["get"], calls)
        with patch.dict(os.environ, {"ACTIONS_ID_TOKEN_REQUEST_URL": "", "ACTIONS_ID_TOKEN_REQUEST_TOKEN": ""}):
            self.assertFalse(credential.relogin())

    def cli(self, failures, login_fails=False):
        """A fake subprocess layer: the first `failures` az acr calls fail with the given stderr; az login/account always
        succeed unless login_fails. Records each command (assertion masked) in self.commands."""
        self.commands = []
        pending = list(failures)

        def fake(command, **kwargs):
            args = command[1:]
            self.commands.append(["az", *("<assertion>" if index and args[index - 1] == "--federated-token" else value
                                          for index, value in enumerate(args))])
            if args[0] == "login" and login_fails:
                return SimpleNamespace(returncode=1, stdout="", stderr="AADSTS700024: Client assertion is not within its valid time range.")
            if args[0] == "acr" and pending:
                return SimpleNamespace(returncode=1, stdout="", stderr=pending.pop(0))
            return SimpleNamespace(returncode=0, stdout="ok", stderr="")
        return patch("pipeline.subprocess.run", side_effect=fake)

    def oidc(self, available=True):
        values = {"ACTIONS_ID_TOKEN_REQUEST_URL": "https://oidc.example/token?api-version=2.0" if available else "",
                  "ACTIONS_ID_TOKEN_REQUEST_TOKEN": "request-token" if available else ""}
        response = io.BytesIO(json.dumps({"value": "fresh-assertion-value"}).encode())
        opener = patch("pipeline.urllib.request.urlopen", return_value=response)
        return patch.dict(os.environ, values), opener, patch.object(pipeline, "FEDERATED_LOGIN", ("tenant", "client", "subscription"))

    EXPIRED = ("WARNING: Unable to get AAD authorization tokens with message: AADSTS700024: Client assertion is not within its "
               "valid time range. Current time: 2026-11-02T14:44:41Z, assertion valid from 2026-11-02T14:02:31Z\n"
               "ERROR: Unable to authenticate using AAD or admin login credentials.")

    def test_an_az_call_failing_on_an_expired_assertion_logs_in_again_once_and_retries(self):
        environment, opener, login = self.oidc()
        with environment, opener as urlopen, login, self.cli([self.EXPIRED]):
            self.assertEqual("ok", pipeline.run("az", "acr", "login", "--name", "crwancusf9718e8c", "--expose-token"))
        self.assertEqual([["az", "acr", "login", "--name", "crwancusf9718e8c", "--expose-token"],
                          ["az", "login", "--service-principal", "-u", "client", "-t", "tenant", "--federated-token", "<assertion>",
                           "--allow-no-subscriptions", "--only-show-errors", "-o", "none"],
                          ["az", "account", "set", "--subscription", "subscription", "--only-show-errors"],
                          ["az", "acr", "login", "--name", "crwancusf9718e8c", "--expose-token"]], self.commands)
        self.assertEqual(1, urlopen.call_count)
        request = urlopen.call_args.args[0]
        self.assertTrue(request.full_url.endswith("api-version=2.0&audience=api://AzureADTokenExchange"))
        self.assertEqual("Bearer request-token", request.get_header("Authorization"))

    def test_an_az_call_is_retried_only_once_even_if_the_assertion_still_looks_expired(self):
        environment, opener, login = self.oidc()
        with environment, opener, login, self.cli([self.EXPIRED, self.EXPIRED]):
            with self.assertRaisesRegex(RuntimeError, r"az acr failed [\s\S]*AADSTS700024"):
                pipeline.run("az", "acr", "repository", "list", "--name", "r")
        self.assertEqual(["acr", "login", "account", "acr"], [command[1] for command in self.commands])

    def test_other_az_failures_and_other_tools_are_not_retried(self):
        environment, opener, login = self.oidc()
        with environment, opener as urlopen, login, self.cli(["ERROR: (AuthorizationFailed) no pull permission"]):
            with self.assertRaisesRegex(RuntimeError, "AuthorizationFailed"):
                pipeline.run("az", "acr", "repository", "show", "--name", "r")
        self.assertEqual(["acr"], [command[1] for command in self.commands])
        urlopen.assert_not_called()
        with environment, opener as urlopen, login, patch("pipeline.subprocess.run",
                return_value=SimpleNamespace(returncode=1, stdout="", stderr=self.EXPIRED)) as invoke:
            with self.assertRaises(RuntimeError):
                pipeline.run("docker", "push", "x")
        self.assertEqual(1, invoke.call_count)
        urlopen.assert_not_called()

    def test_no_relogin_without_github_oidc_or_a_verified_federated_identity(self):
        for available, identity in ((False, ("tenant", "client", "subscription")), (True, None)):
            environment, opener, _ = self.oidc(available)
            with self.subTest(oidc=available, identity=identity), environment, opener as urlopen, \
                    patch.object(pipeline, "FEDERATED_LOGIN", identity), self.cli([self.EXPIRED]):
                with self.assertRaisesRegex(RuntimeError, "AADSTS700024"):
                    pipeline.run("az", "acr", "login", "--name", "r")
                self.assertEqual(["acr"], [command[1] for command in self.commands])
                urlopen.assert_not_called()

    def test_a_failing_relogin_does_not_recurse_and_never_shows_the_assertion(self):
        environment, opener, login = self.oidc()
        with environment, opener as urlopen, login, self.cli([self.EXPIRED], login_fails=True):
            with self.assertRaisesRegex(RuntimeError, "az login failed") as failure:
                pipeline.run("az", "acr", "login", "--name", "r")
        self.assertEqual(["acr", "login"], [command[1] for command in self.commands])
        self.assertEqual(1, urlopen.call_count)
        self.assertNotIn("fresh-assertion-value", str(failure.exception))

    def test_azure_records_the_verified_federated_identity_for_relogin(self):
        account = {"tenantId": "T", "id": "S", "environmentName": "AzureCloud", "user": {"type": "servicePrincipal", "name": "C"}}
        with patch("pipeline.run", return_value=json.dumps(account)), patch("azure.identity.AzureCliCredential"), \
                patch("pipeline.actor_id", return_value="oid"), patch.object(pipeline, "FEDERATED_LOGIN", None), \
                patch.dict(os.environ, {"GITHUB_ACTIONS": "true", "AZURE_CLIENT_ID": "c"}):
            pipeline.Azure({"tenantId": "t", "subscriptionId": "s"})
            self.assertEqual(("t", "c", "s"), pipeline.FEDERATED_LOGIN)

    def test_workspace_mismatches_name_each_differing_property_with_expected_and_actual(self):
        target = {"workspaceIdentityId": "/id/w", "storageAccountId": "/s", "registryId": "/r", "keyVaultId": "/k"}
        wired = {"workspaceIdentityId": "/id/w", "keyVaultId": "/k"}
        good = {"publicNetworkAccess": "Disabled", "allowPublicAccessWhenBehindVnet": False, "systemDatastoresAuthMode": "Identity",
                "primaryUserAssignedIdentity": "/ID/W", "storageAccount": "/s", "containerRegistry": "/r", "keyVault": "/k"}
        self.assertEqual([], pipeline.workspace_mismatches(good, target, wired))
        api_shape = {key: value for key, value in good.items() if key != "allowPublicAccessWhenBehindVnet"}
        api_shape.update(publicNetworkAccess="disabled", systemDatastoresAuthMode="identity")
        self.assertEqual([], pipeline.workspace_mismatches(api_shape, target, wired))
        self.assertEqual(1, len(pipeline.workspace_mismatches(dict(good, allowPublicAccessWhenBehindVnet=True), target, wired)))
        bad = dict(good, publicNetworkAccess="Enabled", keyVault="/other", systemDatastoresAuthMode=None)
        problems = pipeline.workspace_mismatches(bad, target, wired)
        self.assertEqual(3, len(problems))
        self.assertIn("publicNetworkAccess: expected Disabled, actual Enabled", problems)
        self.assertTrue(any(p.startswith("keyVault: expected /k (setting keyVaultId), actual /other") for p in problems))

    def test_datastore_mismatches_accept_omitted_subscription_and_group_but_refuse_wrong_ones(self):
        target = {"storageAccountId": "/subscriptions/s/resourceGroups/g/providers/Microsoft.Storage/storageAccounts/stacct",
                  "releaseContainer": "wan-release", "workspaceId": "/subscriptions/s/resourceGroups/rg-one/providers/Microsoft.MachineLearningServices/workspaces/w"}
        good = {"datastoreType": "AzureBlob", "endpoint": "core.windows.net", "protocol": "https", "accountName": "stacct",
                "containerName": "wan-release", "credentials": {"credentialsType": "None"},
                "serviceDataAccessAuthIdentity": "WorkspaceUserAssignedIdentity"}
        self.assertEqual([], pipeline.datastore_mismatches(good, target, "s"))
        self.assertEqual([], pipeline.datastore_mismatches(dict(good, subscriptionId="S", resourceGroup="RG-ONE", datastoreType="azureBlob"), target, "s"))
        self.assertEqual(1, len(pipeline.datastore_mismatches(dict(good, subscriptionId="other"), target, "s")))
        self.assertEqual(1, len(pipeline.datastore_mismatches(dict(good, resourceGroup="elsewhere"), target, "s")))
        self.assertEqual(1, len(pipeline.datastore_mismatches(dict(good, accountName="other"), target, "s")))
        self.assertEqual(1, len(pipeline.datastore_mismatches(dict(good, credentials={"credentialsType": "AccountKey"}), target, "s")))

    def test_an_arm_failure_names_the_resource_the_caller_and_azures_reason_but_nothing_else(self):
        body = json.dumps({"error": {"code": "AuthorizationFailed", "message": "no read on scope"}}).encode()
        error = urllib.error.HTTPError("https://management.azure.com/x", 403, "Forbidden", {}, io.BytesIO(body))
        message = pipeline.arm_failure("GET", "/subscriptions/s/resourceGroups/g/providers/Microsoft.ContainerRegistry/registries/r", error, "oid-1")
        for expected in ("HTTP 403", "registries/r", "oid-1", "AuthorizationFailed: no read on scope"):
            self.assertIn(expected, message)
        self.assertNotIn("Bearer", message)

    def test_immutable_record_retries_do_not_overwrite(self):
        target = Container()
        immutable_bytes(target, "record", b"same")
        immutable_bytes(target, "record", b"same")
        with self.assertRaises(ValueError):
            immutable_bytes(target, "record", b"changed")
        self.assertEqual(target.data["record"], b"same")

    def test_snapshot_reuses_exact_content_and_rejects_actual_corruption(self):
        target = Container()
        records = [{"path": "weights/a", "bytes": 4, "sha256": hashlib.sha256(b"same").hexdigest()}]
        prefix = snapshot(target, "models", records, lambda _: io.BytesIO(b"same"))
        self.assertEqual(prefix, snapshot(target, "models", records, lambda _: self.fail("Must reuse existing snapshot")))
        target.data[prefix + "/weights/a"] = b"evil"
        with self.assertRaises(ValueError):
            snapshot(target, "models", records, lambda _: self.fail("Never overwrite"))

    def test_snapshot_rejects_unexpected_files(self):
        target = Container()
        records = [{"path": "a", "bytes": 1, "sha256": hashlib.sha256(b"a").hexdigest()}]
        target.data["models/" + snapshot_id(records) + "/extra"] = b"extra"
        with self.assertRaises(ValueError):
            snapshot(target, "models", records, lambda _: self.fail("No upload after unexpected content"))

    def test_snapshot_identity_excludes_code_image_region_and_timeout(self):
        records = [{"path": "model", "bytes": 1, "sha256": "a" * 64}]
        self.assertEqual(snapshot_id(records), snapshot_id(copy.deepcopy(records)))
        self.assertNotEqual(snapshot_id(records), snapshot_id([dict(records[0], sha256="b" * 64)]))

    def test_native_environment_strips_platform_and_feed_credentials(self):
        with patch.dict(os.environ, {"ARM_CLIENT_SECRET": "do-not-forward", "AZURE_CLIENT_SECRET": "do-not-forward",
                                     "GITHUB_TOKEN": "do-not-forward", "PIP_EXTRA_INDEX_URL": "do-not-forward",
                                     "AZURE_CONFIG_DIR": "isolated-cli-profile"}):
            with patch("pipeline.subprocess.run", return_value=SimpleNamespace(returncode=0, stdout="ok")) as invoke:
                self.assertEqual(pipeline.run("tool", "check"), "ok")
                self.assertFalse(set(invoke.call_args.kwargs["env"]) &
                                 {"ARM_CLIENT_SECRET", "AZURE_CLIENT_SECRET", "GITHUB_TOKEN", "PIP_EXTRA_INDEX_URL", "AZURE_CONFIG_DIR"})
                pipeline.run("az", "account", "show")
                self.assertEqual("isolated-cli-profile", invoke.call_args.kwargs["env"]["AZURE_CONFIG_DIR"])

    def test_scope_mismatch_before_any_credential_or_cloud_use(self):
        wrong = {"tenantId": "another", "id": "another", "environmentName": "AzureCloud"}
        with patch("pipeline.run", return_value=json.dumps(wrong)), patch("azure.identity.AzureCliCredential") as credential:
            with self.assertRaises(ValueError):
                pipeline.Azure({"tenantId": "expected", "subscriptionId": "expected"})
            credential.assert_not_called()

    def test_actor_audit_is_bound_to_actual_cli_claims_without_graph_fallback(self):
        claims = {"oid": "33333333-3333-3333-3333-333333333333",
                  "tid": "22222222-2222-2222-2222-222222222222", "appid": "44444444-4444-4444-4444-444444444444"}
        payload = base64.urlsafe_b64encode(json_bytes(claims)).decode().rstrip("=")
        token = "header." + payload + ".signature"
        self.assertEqual(claims["oid"], pipeline.actor_id(token, claims["tid"], claims["appid"]))
        for value, tenant, client in ((token, "wrong", claims["appid"]), (token, claims["tid"], "wrong"),
                                      ("opaque", claims["tid"], claims["appid"])):
            with self.assertRaises(ValueError):
                pipeline.actor_id(value, tenant, client)

    def test_private_dns_does_not_accept_public_or_mixed_answers(self):
        for addresses in (["8.8.8.8"], ["10.0.0.1", "8.8.8.8"], ["127.0.0.1"], ["169.254.169.254"], []):
            with self.subTest(addresses=addresses), patch("pipeline.socket.getaddrinfo",
                return_value=[(0, 0, 0, "", (value, 443)) for value in addresses]):
                with self.assertRaises(ValueError):
                    pipeline.private_host("fixture")

    def test_registration_never_overwrites_existing_asset(self):
        azure = SimpleNamespace(arm=lambda *args, **kwargs: {
            "id": "/expected", "properties": {"image": "wrong", "provisioningState": "Succeeded", "tags": {}}})
        with self.assertRaises(ValueError):
            publish_region.registration(azure, "/expected", {"image": "correct", "tags": {}})

    def test_missing_provisioning_evidence_does_not_look_successful(self):
        azure = SimpleNamespace(arm=lambda *args, **kwargs: {"id": "/expected", "properties": {"image": "correct", "tags": {}}})
        with self.assertRaises(ValueError):
            publish_region.registration(azure, "/expected", {"image": "correct", "tags": {}})

    def test_environment_cannot_hide_a_second_build(self):
        azure = SimpleNamespace(arm=lambda *args, **kwargs: {"id": "/expected", "properties": {
            "image": "correct", "tags": {}, "provisioningState": "Succeeded", "build": {"contextUri": "unexpected"}}})
        with self.assertRaises(ValueError):
            publish_region.registration(azure, "/expected", {"image": "correct", "tags": {}})

    def test_registration_accepts_arm_expanded_model_datastore_uri(self):
        azure = SimpleNamespace(arm=lambda *args, **kwargs: {"id": "/expected", "properties": {
            "modelUri": "azureml://subscriptions/s/resourcegroups/r/workspaces/w/datastores/wan_release/paths/models/x",
            "provisioningState": "Succeeded", "tags": {}}})
        publish_region.registration(azure, "/expected", {"modelUri": "azureml://datastores/wan_release/paths/models/x/", "tags": {}})

    def test_a_new_model_is_put_with_the_workspace_qualified_uri_and_custom_model_and_verified_in_short_form(self):
        workspace = "/subscriptions/sub-1/resourceGroups/rg-wan/providers/Microsoft.MachineLearningServices/workspaces/mlw-cus"
        resource_id = workspace + "/models/wan-models-abc/versions/1"
        expected = {"modelType": "custom_model", "modelUri": "azureml://datastores/wan_release/paths/models/abc/",
                    "tags": {"studio": "wan"}}
        stored, puts = {}, []

        def arm(rid, api=None, method="GET", body=None, missing=False):
            self.assertEqual(resource_id, rid)
            if method == "PUT":
                uri = body["properties"]["modelUri"]
                # What ARM 2024-10-01 enforces: only the workspace-qualified datastore form is accepted.
                if not re.fullmatch(r"azureml://subscriptions/[^/]+/resourceGroups/[^/]+/workspaces/[^/]+/datastores/[^/]+/paths/.+", uri):
                    raise RuntimeError("HTTP 400 Property: ModelUri does not match the regex")
                puts.append(copy.deepcopy(body))
                stored.update(id=rid, properties=dict(body["properties"], provisioningState="Succeeded"))
                return stored
            return copy.deepcopy(stored) if stored else (None if missing else self.fail("unexpected read"))

        azure = SimpleNamespace(arm=arm)
        publish_region.registration(azure, resource_id, expected)
        self.assertEqual(1, len(puts))
        self.assertEqual({"modelType": "custom_model", "tags": {"studio": "wan"},
                          "modelUri": "azureml://subscriptions/sub-1/resourceGroups/rg-wan/workspaces/mlw-cus/datastores/wan_release/paths/models/abc/"},
                         puts[0]["properties"])
        self.assertEqual("azureml://datastores/wan_release/paths/models/abc/", expected["modelUri"], "The caller's short form is unchanged.")
        publish_region.registration(azure, resource_id, expected)
        self.assertEqual(1, len(puts), "An existing model is verified, never re-PUT.")
        with self.assertRaisesRegex(ValueError, "modelType: expected custom_model, actual CustomModel"):
            stored["properties"]["modelType"] = "CustomModel"
            publish_region.registration(azure, resource_id, expected)

    def test_a_model_location_outside_a_workspace_datastore_is_refused_before_any_put(self):
        azure = SimpleNamespace(arm=lambda rid, method="GET", body=None, missing=False, **_:
                                None if method == "GET" else self.fail("must not PUT"))
        for resource_id, uri in (("/not/a/workspace/models/m/versions/1", "azureml://datastores/d/paths/x/"),
                                 ("/subscriptions/s/resourceGroups/g/providers/Microsoft.MachineLearningServices/workspaces/w/models/m/versions/1",
                                  "https://storage.example/x/")):
            with self.subTest(uri=uri), self.assertRaisesRegex(ValueError, "Unexpected model location"):
                publish_region.registration(azure, resource_id, {"modelType": "custom_model", "modelUri": uri, "tags": {}})

    def test_wslc_build_binds_the_pushed_digest_to_the_built_image_and_login_is_recorded_for_logout(self):
        import containers
        image_id, digest = "sha256:" + "1" * 64, "sha256:" + "2" * 64
        calls = []

        def tool(*args, input=None):
            command = [str(arg) for arg in args[1:]]
            calls.append(command)
            if command[0] == "build":
                Path(command[command.index("--iidfile") + 1]).write_text(image_id + "\n")
            if command[:3] == ["inspect", "--type", "image"]:
                return json.dumps([{"Os": "linux", "Architecture": "amd64", "Id": image_id,
                                    "RepoDigests": ["other.azurecr.io/wan/server@sha256:" + "3" * 64, "r.azurecr.io/wan/server@" + digest]}])
            return ""

        cli = containers.Containers(tool, cli="wslc")
        with tempfile.TemporaryDirectory() as temp, patch.object(containers, "host_path", side_effect=lambda path: str(path)), \
                patch.dict(os.environ, {"STUDIO_REGISTRY_LOG": str(Path(temp) / "registries")}):
            self.assertEqual((image_id, digest), cli.build_and_push(
                "r.azurecr.io/wan/server:unready-1-1", Path(temp) / "Dockerfile", Path(temp),
                ["A=1"], [("pip-index", "env", "PIP_INDEX_URL"), ("npmrc", "src", Path(temp) / "npmrc")]))
            build = calls[0]
            self.assertIn("id=pip-index,env=PIP_INDEX_URL", build)
            self.assertIn(f"id=npmrc,src={Path(temp) / 'npmrc'}", build)
            self.assertEqual(["push", "--quiet", "r.azurecr.io/wan/server:unready-1-1"], calls[2])
            cli.login("r.azurecr.io", "token-value")
            self.assertEqual("r.azurecr.io\n", (Path(temp) / "registries").read_text())
            self.assertEqual(["login", "r.azurecr.io", "--username", containers.TOKEN_USER, "--password-stdin"], calls[-1])
            self.assertNotIn("token-value", calls[-1])

        def arm64(*args, input=None):
            return json.dumps([{"Os": "linux", "Architecture": "arm64"}]) if args[1] == "inspect" else ""
        with self.assertRaisesRegex(ValueError, "not linux/amd64"):
            containers.Containers(arm64, cli="wslc").pull("r.azurecr.io/wan/server@" + digest)

    SOURCE = {"registryId": "/subscriptions/s/resourceGroups/g/providers/Microsoft.ContainerRegistry/registries/crsource"}
    TARGET = {"registryId": "/subscriptions/s/resourceGroups/h/providers/Microsoft.ContainerRegistry/registries/crtarget"}

    def promotion(self, tags, inspect, repositories=("wan/runtime",)):
        """promote_image against fake registries: returns (result or exception, az calls, registry() calls)."""
        calls, logins = [], []
        azure = SimpleNamespace(registry=lambda region, login=True: logins.append(login) or
                                region["registryId"].rsplit("/", 1)[1] + ".azurecr.io")

        def tool(*args, **kwargs):
            calls.append(args)
            if args[:4] == ("az", "acr", "repository", "list"):
                return json.dumps(list(repositories))
            if args[:4] == ("az", "acr", "repository", "show-tags"):
                return json.dumps(tags)
            return ""

        with patch("pipeline.run", side_effect=tool), patch("pipeline.image_digest", side_effect=inspect), \
                patch("pipeline.require_single_amd64_manifest") as platform, \
                patch("pipeline.containers", side_effect=AssertionError("promotion must not use the container CLI")):
            try:
                result = pipeline.promote_image(azure, self.SOURCE, self.TARGET, "wan/runtime", "sha256:" + "a" * 64)
            except (ValueError, RuntimeError) as error:
                result = error
        return result, calls, logins, platform

    def test_a_missing_destination_tag_is_imported_server_side_by_exact_digest_and_verified(self):
        digest = "sha256:" + "a" * 64
        result, calls, logins, platform = self.promotion([], lambda host, repository, reference: digest, repositories=())
        self.assertEqual("crtarget.azurecr.io/wan/runtime@" + digest, result)
        imports = [call for call in calls if call[:3] == ("az", "acr", "import")]
        self.assertEqual([("az", "acr", "import", "--name", "crtarget", "--source", "crsource.azurecr.io/wan/runtime@" + digest,
                           "--registry", self.SOURCE["registryId"], "--image", "wan/runtime:" + "a" * 64, "--only-show-errors")], imports)
        self.assertNotIn("--force", imports[0])
        self.assertEqual([False, False], logins, "Promotion verifies both registries but never logs a container CLI in.")
        platform.assert_called_once_with("crtarget.azurecr.io", "wan/runtime", digest)

    def test_existing_immutable_image_is_not_imported_again(self):
        digest = "sha256:" + "a" * 64
        result, calls, _, _ = self.promotion(["a" * 64], lambda host, repository, reference: digest)
        self.assertEqual("crtarget.azurecr.io/wan/runtime@" + digest, result)
        self.assertFalse(any(call[:3] == ("az", "acr", "import") for call in calls))

    def test_an_existing_tag_naming_different_bytes_is_refused_and_never_overwritten(self):
        digest = "sha256:" + "a" * 64

        def inspect(host, repository, reference):
            return "sha256:" + "b" * 64 if host.startswith("crtarget") and reference.startswith(":") else digest
        result, calls, _, _ = self.promotion(["a" * 64], inspect)
        self.assertIsInstance(result, ValueError)
        self.assertIn("already names different bytes", str(result))
        self.assertFalse(any(call[:3] == ("az", "acr", "import") for call in calls))

    def test_an_import_that_changes_the_digest_fails(self):
        digest = "sha256:" + "a" * 64

        def inspect(host, repository, reference):
            return "sha256:" + "b" * 64 if host.startswith("crtarget") else digest
        result, calls, _, _ = self.promotion([], inspect)
        self.assertIsInstance(result, ValueError)
        self.assertIn("Copied destination tag changed the manifest digest", str(result))
        self.assertTrue(any(call[:3] == ("az", "acr", "import") for call in calls))

    def test_a_missing_source_image_is_refused_before_any_copy(self):
        result, calls, _, _ = self.promotion([], lambda host, repository, reference: "sha256:" + "c" * 64)
        self.assertIsInstance(result, ValueError)
        self.assertIn("Source image missing or changed", str(result))
        self.assertFalse(any(call[:3] == ("az", "acr", "import") for call in calls))

    def test_build_rejects_a_registry_manifest_that_is_not_one_linux_amd64_docker_v2_image(self):
        digest = "sha256:" + "c" * 64
        good = {"digest": digest, "mediaType": pipeline.DOCKER_V2_MANIFEST, "architecture": "amd64", "os": "linux"}
        with patch("pipeline.image_manifest", return_value=good):
            pipeline.require_single_amd64_manifest("r.azurecr.io", "wan/server", digest)
        for change in ({"mediaType": "application/vnd.oci.image.index.v1+json"}, {"architecture": "arm64"},
                       {"mediaType": "application/vnd.oci.image.manifest.v1+json"}, {"digest": "sha256:" + "d" * 64}):
            with self.subTest(change=change), patch("pipeline.image_manifest", return_value=dict(good, **change)):
                with self.assertRaisesRegex(ValueError, "not one linux/amd64 Docker v2"):
                    pipeline.require_single_amd64_manifest("r.azurecr.io", "wan/server", digest)

    def engine_tool(self, calls, image_id, digest, repository="r.azurecr.io/wan/server", podman_id=None, metadata=None):
        def tool(*args, input=None):
            command = [str(arg) for arg in args[1:]]
            calls.append(command)
            if command[:2] == ["buildx", "build"] and metadata is not None:
                Path(command[command.index("--metadata-file") + 1]).write_text(json.dumps(metadata))
            if command[0] == "build":
                Path(command[command.index("--iidfile") + 1]).write_text(image_id + "\n")
            if command[0] == "push" and "--digestfile" in command:
                Path(command[command.index("--digestfile") + 1]).write_text(digest)
            if command[:3] == ["inspect", "--type", "image"]:
                return json.dumps([{"Os": "linux", "Architecture": "amd64", "Id": podman_id or image_id,
                                    "RepoDigests": [repository + "@" + digest]}])
            if command[:3] == ["inspect", "--type", "container"]:
                return json.dumps([{"SizeRw": 0}])
            return ""
        return tool

    def test_podman_builds_docker_format_pushes_v2s2_and_reads_the_digest_from_digestfile(self):
        import containers
        image_id, digest = "sha256:" + "1" * 64, "sha256:" + "2" * 64
        calls = []
        cli = containers.Containers(self.engine_tool(calls, image_id, digest, podman_id="1" * 64), cli="/usr/bin/podman")
        self.assertEqual("podman", cli.engine)
        with tempfile.TemporaryDirectory() as temp:
            self.assertEqual((image_id, digest), cli.build_and_push(
                "r.azurecr.io/wan/server:unready-1-1", Path(temp) / "Dockerfile", Path(temp),
                ["A=1"], [("pip-index", "env", "PIP_INDEX_URL"), ("npmrc", "src", Path(temp) / "npmrc")]))
            build = calls[0]
            for flag in (["--format", "docker"], ["--platform", "linux/amd64"]):
                self.assertEqual(flag, build[build.index(flag[0]):build.index(flag[0]) + 2])
            self.assertNotIn("--progress", build)
            self.assertIn("id=pip-index,env=PIP_INDEX_URL", build)
            self.assertIn(f"id=npmrc,src={(Path(temp) / 'npmrc').resolve()}", build, "Podman takes native paths, never wslpath.")
            push = next(call for call in calls if call[0] == "push")
            self.assertEqual(["push", "--quiet", "--format", "v2s2", "--digestfile"], push[:5])
            # Podman's unprefixed image ID is normalized, so the pulled image binds to the built one.
            self.assertTrue(cli.is_built_image("r.azurecr.io/wan/server@" + digest, image_id))
            self.assertIn(["pull", "--quiet", "--platform", "linux/amd64", "r.azurecr.io/wan/server@" + digest], calls)
            cli.copy_out("r.azurecr.io/wan/server@" + digest, "/app", Path(temp) / "app")
            copy = next(call for call in calls if call[0] == "cp")
            self.assertTrue(copy[1].endswith(":/app/."))
            self.assertEqual(str((Path(temp) / "app").resolve()), copy[2], "No wslc trailing separator for Podman.")

    def test_podman_without_a_pushed_digest_is_refused(self):
        import containers
        calls = []
        cli = containers.Containers(self.engine_tool(calls, "sha256:" + "1" * 64, "not-a-digest"), cli="podman")
        with tempfile.TemporaryDirectory() as temp, self.assertRaisesRegex(ValueError, "podman push reported no registry digest"):
            cli.build_and_push("r.azurecr.io/wan/server:t", Path(temp) / "Dockerfile", Path(temp))

    def test_docker_builds_with_buildx_pushing_one_docker_v2_manifest_and_binds_its_metadata(self):
        import containers
        image_id, digest = "sha256:" + "1" * 64, "sha256:" + "2" * 64
        calls = []
        cli = containers.Containers(self.engine_tool(calls, image_id, digest, metadata={
            "containerimage.digest": digest, "containerimage.config.digest": image_id}), cli="docker")
        with tempfile.TemporaryDirectory() as temp:
            self.assertEqual((image_id, digest), cli.build_and_push(
                "r.azurecr.io/wan/server:unready-1-1", Path(temp) / "Dockerfile", Path(temp), ["A=1"],
                [("pip-index", "env", "PIP_INDEX_URL")]))
        build = calls[0]
        self.assertEqual(["buildx", "build"], build[:2])
        for flag in ("--provenance=false", "--sbom=false", "type=image,push=true,oci-mediatypes=false", "id=pip-index,env=PIP_INDEX_URL"):
            self.assertIn(flag, build)
        self.assertEqual("linux/amd64", build[build.index("--platform") + 1])
        self.assertIn("--metadata-file", build)
        self.assertFalse(any(call[0] == "push" for call in calls), "buildx pushes from the build itself.")
        # The classic image store names the image by its config digest, the containerd store by its manifest digest.
        self.assertTrue(cli.is_built_image("r.azurecr.io/wan/server@" + digest, image_id))
        other = containers.Containers(self.engine_tool([], "sha256:" + "9" * 64, digest), cli="docker")
        self.assertFalse(other.is_built_image("r.azurecr.io/wan/server@" + digest, image_id))

    def test_docker_without_buildx_metadata_is_refused(self):
        import containers
        cli = containers.Containers(self.engine_tool([], "sha256:" + "1" * 64, "sha256:" + "2" * 64, metadata={}), cli="docker")
        with tempfile.TemporaryDirectory() as temp, self.assertRaisesRegex(ValueError, "no pushed manifest digest"):
            cli.build_and_push("r.azurecr.io/wan/server:t", Path(temp) / "Dockerfile", Path(temp))

    def test_native_engines_add_read_only_and_keep_the_empty_writable_layer_check(self):
        import containers
        for engine in ("docker", "podman", "wslc"):
            calls = []
            cli = containers.Containers(self.engine_tool(calls, "sha256:" + "1" * 64, "sha256:" + "2" * 64), cli=engine)
            with self.subTest(engine=engine), patch.object(containers, "host_path", side_effect=lambda path: str(path)):
                cli.run_offline("image@sha256:" + "2" * 64, "python", ["-V"], tmpfs=("/tmp:rw",))
                run = calls[0]
                self.assertEqual(["run", "--name"], run[:2])
                for flag in ("--network", "none", "--pull", "never"):
                    self.assertIn(flag, run)
                self.assertEqual(engine != "wslc", "--read-only" in run)
                self.assertIn(["inspect", "--type", "container", "--size", run[2]], calls)

    def test_engine_selection_and_preflight_fail_fast_with_the_missing_feature(self):
        import containers
        with patch.dict(os.environ, {"STUDIO_CONTAINER_ENGINE": "nerdctl"}), self.assertRaisesRegex(ValueError, "must be one of"):
            containers.engine_name()
        with patch.dict(os.environ, {"STUDIO_CONTAINER_ENGINE": "Podman"}):
            self.assertEqual("podman", containers.engine_name())
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop("STUDIO_CONTAINER_ENGINE", None)
            self.assertEqual("wslc", containers.engine_name(), "Unset keeps the private runners on wslc.")
        with patch.object(containers.shutil, "which", return_value=None), patch.dict(os.environ, {"STUDIO_DOCKER": ""}), \
                self.assertRaisesRegex(ValueError, "docker was not found"):
            containers.executable("docker")

        def no_buildx(*args, input=None):
            raise RuntimeError("'buildx' is not a docker command")
        with self.assertRaisesRegex(ValueError, "buildx plugin"):
            containers.Containers(no_buildx, cli="docker").preflight()

        def old_podman(*args, input=None):
            return "--secret --iidfile --format" if args[1] == "build" else "--quiet"
        with self.assertRaisesRegex(ValueError, "lacks --digestfile"):
            containers.Containers(old_podman, cli="podman").preflight()

    def test_each_engine_sees_only_its_own_settings_and_the_feed_url(self):
        variables = {"PIP_INDEX_URL": "https://packagefeedproxy.microsoft.io/pypi/simple", "DOCKER_CONFIG": "/job/docker",
                     "XDG_RUNTIME_DIR": "/run/user/1000", "STUDIO_CONTAINER_ENGINE": "docker", "ProgramFiles": r"C:\Program Files"}
        with patch.dict(os.environ, variables), \
                patch("pipeline.subprocess.run", return_value=SimpleNamespace(returncode=0, stdout="ok", stderr="")) as invoke:
            pipeline.run("/usr/bin/docker", "build")
            env = invoke.call_args.kwargs["env"]
            self.assertEqual("/job/docker", env["DOCKER_CONFIG"])
            self.assertIn("PROGRAMFILES", {key.upper() for key in env}, "Docker on Windows finds buildx under ProgramFiles.")
            self.assertIn("PIP_INDEX_URL", env)
            self.assertNotIn("XDG_RUNTIME_DIR", env)
            self.assertNotIn("WSLENV", env)
            pipeline.run("podman", "build")
            env = invoke.call_args.kwargs["env"]
            self.assertEqual("/run/user/1000", env["XDG_RUNTIME_DIR"])
            self.assertNotIn("DOCKER_CONFIG", env)
            pipeline.run("az", "acr")
            env = invoke.call_args.kwargs["env"]
            self.assertFalse({"PIP_INDEX_URL", "DOCKER_CONFIG", "XDG_RUNTIME_DIR", "PROGRAMFILES"} & {key.upper() for key in env})
            self.assertEqual("docker", env["STUDIO_CONTAINER_ENGINE"])
            pipeline.run(sys.executable, "verify.py")
            env = invoke.call_args.kwargs["env"]
            self.assertEqual("/job/docker", env["DOCKER_CONFIG"], "verify.py runs the selected engine itself.")

    def test_wslc_gets_the_feed_url_forwarded_and_other_tools_do_not_see_it(self):
        with patch.dict(os.environ, {"PIP_INDEX_URL": "https://packagefeedproxy.microsoft.io/pypi/simple", "WSLENV": "OTHER"}), \
                patch("pipeline.subprocess.run", return_value=SimpleNamespace(returncode=0, stdout="ok", stderr="")) as invoke:
            pipeline.run("/mnt/c/Program Files/WSL/wslc.exe", "build")
            self.assertEqual("PIP_INDEX_URL", invoke.call_args.kwargs["env"]["WSLENV"])
            self.assertIn("PIP_INDEX_URL", invoke.call_args.kwargs["env"])
            pipeline.run("az", "acr")
            self.assertFalse({"PIP_INDEX_URL", "WSLENV"} & set(invoke.call_args.kwargs["env"]))

    def test_non_destructive_plan_owner_allowlist(self):
        def plan(kind, actions, arm_type=None):
            return {"format_version": "1.2", "complete": True, "resource_changes": [
                {"mode": "managed", "type": kind, "change": {"actions": actions, "after": {"type": arm_type}}}]}
        for kind in ("azurerm_virtual_network", "azurerm_private_dns_zone", "azurerm_nat_gateway",
                     "azurerm_public_ip", "azurerm_subnet", "azurerm_log_analytics_workspace"):
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                foundation.validate_plan(plan(kind, ["create"]))
        for actions in (["delete"], ["create", "delete"], ["delete", "create"]):
            with self.subTest(actions=actions), self.assertRaises(ValueError):
                foundation.validate_plan(plan("azurerm_resource_group", actions))
        with self.assertRaises(ValueError):
            foundation.validate_plan(plan("azapi_resource", ["create"], "Microsoft.MachineLearningServices/workspaces/computes@2024-10-01"))
        self.assertTrue(foundation.validate_plan(plan("azurerm_resource_group", ["create"])))

    def test_saved_plan_provenance_binds_source_run_tfvars_backend_and_lock(self):
        with tempfile.TemporaryDirectory() as temp:
            first, second = Path(temp) / "config", Path(temp) / "backend"
            first.write_text("one")
            second.write_text("two")
            env = {"GITHUB_SHA": "a" * 40, "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"}
            with patch.dict(os.environ, env):
                before = foundation.provenance(first, second)
                second.write_text("changed")
                self.assertNotEqual(before["backendSha256"], foundation.provenance(first, second)["backendSha256"])
                self.assertEqual(before["tfvarsSha256"], foundation.provenance(first, second)["tfvarsSha256"])
                with patch.dict(os.environ, GITHUB_RUN_ATTEMPT="2"):
                    self.assertNotEqual(before["runAttempt"], foundation.provenance(first, second)["runAttempt"])

    @unittest.skipUnless(os.environ.get("STUDIO_CONTRACT_TOOL"), "Build shared .NET contract validator for cross-language checks.")
    def test_actual_deployment_validator_accepts_exported_receipts_and_rejects_scope_mutation(self):
        settings = ROOT / "contracts/fixtures/studio-settings.json"
        self.assertEqual(2, len(pipeline.contracts(settings)["foundation"]["regions"]))
        with tempfile.TemporaryDirectory() as temp:
            for name, mutate in (
                    ("subscription", lambda value: value["Studio"].update(SubscriptionId="44444444-4444-4444-4444-444444444444")),
                    ("control region", lambda value: value["Studio"].update(ControlRegion="northeurope")),
                    ("typo", lambda value: value["Studio"].update(Regoins={})),
                    ("site", lambda value: value["Pipeline"].update(SiteId=value["Studio"]["Regions"]["eastus2"]["Workspace"]["Id"])),
                    ("publisher", lambda value: value["Pipeline"].update(PublisherPrincipalId="not-a-guid"))):
                changed = json.loads(settings.read_text())
                mutate(changed)
                path = Path(temp) / "changed.json"
                path.write_text(json.dumps(changed))
                with self.subTest(name), self.assertRaises(RuntimeError):
                    pipeline.contracts(path)


class PromotionTests(unittest.TestCase):
    def test_partial_promotion_cannot_publish_ready_binding(self):
        fixture = fixture_json()
        build = fixture["build"]
        foundation_receipt = fixture["foundation"]
        region_key = foundation_receipt["controlRegion"]
        target = foundation_receipt["regions"][region_key]
        checked = {"foundation": foundation_receipt, "build": build, "scopeFingerprints": {region_key: "a" * 64}}
        store = Container()
        store.data[f"builds/{build['artifactId']}/manifest.json"] = json_bytes(build)
        for kind in ("code", "models", "licenses", "buildInputs"):
            prefix = kind + "/" + snapshot_id(build[kind])
            for item in build[kind]:
                store.data[prefix + "/" + item["path"]] = b"fixture"

        def arm(resource_id, *args, **kwargs):
            return {"properties": {"datastoreType": "AzureBlob", "endpoint": "core.windows.net", "protocol": "https",
                                  "accountName": target["storageAccountId"].rsplit("/", 1)[1],
                                  "containerName": target["releaseContainer"], "credentials": {"credentialsType": "None"},
                                  "subscriptionId": foundation_receipt["scope"]["subscriptionId"],
                                  "resourceGroup": target["workspaceId"].split("/")[4],
                                  "serviceDataAccessAuthIdentity": "WorkspaceUserAssignedIdentity"}}

        azure = SimpleNamespace(container=lambda _: store, owned=lambda *args: workspace_properties(target), arm=arm,
                                actor_id="33333333-3333-3333-3333-333333333333")
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "config.json"
            config.write_text('{"Pipeline":{"PublisherPrincipalId":"33333333-3333-3333-3333-333333333333"}}')
            with patch("publish_region.contracts", return_value=checked), patch("publish_region.Azure", return_value=azure), \
                 patch("publish_region.private_host"), \
                 patch("publish_region.snapshot", side_effect=lambda _, kind, records, source: kind + "/" + snapshot_id(records)), \
                 patch("publish_region.promote_image", return_value="fixture"), \
                 patch("publish_region.registration", side_effect=RuntimeError("registration failed")):
                with self.assertRaises(RuntimeError):
                    publish_region.promote(config, build["artifactId"], region_key, Path(temp))
            self.assertFalse(any(name.startswith("releases/") for name in store.data))

    @unittest.skipUnless(os.environ.get("STUDIO_CONTRACT_TOOL"), "Build shared .NET contract validator.")
    def test_successful_promotion_and_retry_produce_an_actual_valid_binding_with_integer_model_version(self):
        fixture = fixture_json()
        build = fixture["build"]
        settings_path = ROOT / "contracts/fixtures/studio-settings.json"
        receipt = fixture["foundation"]
        key, store = receipt["controlRegion"], Container()
        target = receipt["regions"][key]
        store.data[f"builds/{build['artifactId']}/manifest.json"] = json_bytes(build)
        for kind in ("code", "models", "licenses", "buildInputs"):
            for item in build[kind]:
                store.data[kind + "/" + snapshot_id(build[kind]) + "/" + item["path"]] = b"fixture"
        registrations = []

        def arm(resource_id, *args, **kwargs):
            return {"properties": {
                "datastoreType": "AzureBlob", "endpoint": "core.windows.net", "protocol": "https",
                "accountName": target["storageAccountId"].rsplit("/", 1)[1], "containerName": target["releaseContainer"],
                "subscriptionId": receipt["scope"]["subscriptionId"], "resourceGroup": target["workspaceId"].split("/")[4],
                "credentials": {"credentialsType": "None"}, "serviceDataAccessAuthIdentity": "WorkspaceUserAssignedIdentity",
            }}

        azure = SimpleNamespace(container=lambda _: store, owned=lambda *args: workspace_properties(target), arm=arm,
                                actor_id="33333333-3333-3333-3333-333333333333")
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            settings = json.loads(settings_path.read_text())
            settings["Pipeline"]["PublisherPrincipalId"] = azure.actor_id
            (path / "settings.json").write_bytes(json_bytes(settings))
            image = target["registryId"].rsplit("/", 1)[1] + ".azurecr.io/wan/runtime@" + build["imageDigest"]
            with patch("publish_region.Azure", return_value=azure), patch("publish_region.private_host"), \
                 patch("publish_region.snapshot", side_effect=lambda _, kind, records, source: kind + "/" + snapshot_id(records)), \
                 patch("publish_region.promote_image", return_value=image), \
                 patch("publish_region.registration", side_effect=lambda _, resource_id, expected: registrations.append((resource_id, expected))):
                first = publish_region.promote(path / "settings.json", build["artifactId"], key, path)
                original = store.data[first]
                second = publish_region.promote(path / "settings.json", build["artifactId"], key, path)
            self.assertEqual(first, second)
            self.assertEqual(original, store.data[second])
            record = json.loads(original)
            self.assertTrue(record["modelsRef"].endswith(":1"))
            self.assertEqual(image, record["image"])
            models = [expected for name, expected in registrations if "/models/" in name]
            self.assertEqual(1, len({name for name, _ in registrations if "/models/" in name}))
            self.assertEqual("custom_model", models[0]["modelType"])
            self.assertEqual(record["modelsUri"], models[0]["modelUri"])
            self.assertTrue(all(name.endswith("/versions/1") for name, _ in registrations))


class ImagePackagingTests(unittest.TestCase):
    def test_server_image_installs_and_release_verifies_the_media_executables(self):
        import build_image
        dockerfile = (ROOT / "src/VideoSafetyStudio.Server/Dockerfile").read_text()
        final = dockerfile[dockerfile.index("AS final"):]
        self.assertIn("apt-get install -y --no-install-recommends ffmpeg", final)
        self.assertLess(final.index("ffmpeg"), final.index("USER app"))
        names = {command[0] for command in build_image.VERIFY["server"]}
        self.assertTrue({"dotnet", "ffmpeg", "ffprobe"} <= names)

    def test_server_image_carries_no_trainer_implementation_or_dependencies(self):
        dockerfile = (ROOT / "src/VideoSafetyStudio.Server/Dockerfile").read_text()
        for forbidden in ("trainers", "pip install", "torch", "yolox", "requirements"):
            self.assertNotIn(forbidden, dockerfile.lower())


class ServerContentTests(unittest.TestCase):
    @staticmethod
    def publish(root, extra=()):
        import check_server_content as content
        for name in (*content.REQUIRED, *(f"Workflows/{name}" for name in content.WORKFLOWS), *extra):
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"x")
        return content

    def test_a_clean_publish_passes_and_third_party_runtime_files_are_not_ours_to_judge(self):
        with tempfile.TemporaryDirectory() as temp:
            content = self.publish(Path(temp), ("runtimes/win-x64/native/builtin/SKILL.md", "runtimes/linux-x64/tool.py"))
            self.assertEqual([], content.problems(temp))

    def test_operator_ci_infrastructure_and_customer_files_are_rejected(self):
        for leak in ("scripts/Stop-Studio.ps1", "deploy.sh", "infra/main.tf", "terraform.tfvars.json", "azureml/runtime.py", "x/terraform.tfstate",
                     "nuget.config", "key.pem", "docs/readme.txt", "settings.json", "tfvars.json", "backend.json"):
            with self.subTest(leak=leak), tempfile.TemporaryDirectory() as temp:
                content = self.publish(Path(temp), (leak,))
                self.assertTrue(any(leak in line for line in content.problems(temp)), leak)

    def test_reference_workflows_and_missing_required_files_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            content = self.publish(Path(temp), ("Workflows/wan22_i2v_gguf_q5km.api.json",))
            self.assertTrue(any("Workflow templates differ" in line for line in content.problems(temp)))
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            content = self.publish(root)
            (root / "wwwroot/index.html").unlink()
            self.assertEqual(["Required file missing: wwwroot/index.html"], content.problems(temp))


class TrainerPromotionTests(unittest.TestCase):
    @staticmethod
    def sealed(store):
        metadata = json.loads((ROOT / "trainers/yolox/trainer.json").read_text())
        record = {"schemaVersion": 1, "component": "yolox", "sourceSha": "c" * 40, "imageDigest": "sha256:" + "b" * 64,
                  "trainer": {key: metadata[key] for key in publish_trainer.TRAINER_FIELDS}}
        content = json_bytes(record)
        artifact = hashlib.sha256(content).hexdigest()
        store.data[f"images/yolox/{artifact}.json"] = content
        return artifact, record

    @staticmethod
    def environment(receipt, key):
        target = receipt["regions"][key]

        def arm(resource_id, *args, **kwargs):
            return {"properties": {}}

        return target, SimpleNamespace(owned=lambda *args: workspace_properties(target), arm=arm, actor_id="33333333-3333-3333-3333-333333333333")

    def test_the_sealed_record_must_match_its_identity_and_registration_failure_publishes_nothing(self):
        receipt = fixture_json()["foundation"]
        key, store = receipt["controlRegion"], Container()
        artifact, _ = self.sealed(store)
        target, azure = self.environment(receipt, key)
        azure.container = lambda _: store
        checked = {"foundation": receipt, "scopeFingerprints": {key: "a" * 64}}
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "config.json"
            config.write_text('{"Pipeline":{"PublisherPrincipalId":"33333333-3333-3333-3333-333333333333"}}')
            with patch("publish_trainer.contracts", return_value=checked), patch("publish_trainer.Azure", return_value=azure), \
                 patch("publish_trainer.promote_image", return_value="fixture"), \
                 patch("publish_trainer.registration", side_effect=RuntimeError("registration failed")):
                with self.assertRaises(RuntimeError):
                    publish_trainer.promote(config, artifact, key, Path(temp))
                store.data[f"images/yolox/{artifact}.json"] += b" "
                with self.assertRaisesRegex(ValueError, "identity"):
                    publish_trainer.promote(config, artifact, key, Path(temp))
            self.assertFalse(any(name.startswith("trainers/") for name in store.data))

    def test_a_record_without_exact_trainer_metadata_is_refused(self):
        receipt = fixture_json()["foundation"]
        key, store = receipt["controlRegion"], Container()
        record = {"schemaVersion": 1, "component": "yolox", "sourceSha": "c" * 40, "imageDigest": "sha256:" + "b" * 64}
        content = json_bytes(record)
        artifact = hashlib.sha256(content).hexdigest()
        store.data[f"images/yolox/{artifact}.json"] = content
        _, azure = self.environment(receipt, key)
        azure.container = lambda _: store
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "config.json"
            config.write_text('{"Pipeline":{"PublisherPrincipalId":"33333333-3333-3333-3333-333333333333"}}')
            with patch("publish_trainer.contracts", return_value={"foundation": receipt, "scopeFingerprints": {key: "a" * 64}}), \
                 patch("publish_trainer.Azure", return_value=azure):
                with self.assertRaisesRegex(ValueError, "trainer metadata"):
                    publish_trainer.promote(config, artifact, key, Path(temp))

    def test_a_missing_pipeline_workspace_wiring_setting_stops_publication(self):
        receipt = copy.deepcopy(fixture_json()["foundation"])
        key, store = receipt["controlRegion"], Container()
        artifact, _ = self.sealed(store)
        _, azure = self.environment(receipt, key)
        azure.container = lambda _: store
        del receipt["regions"][key]["keyVaultId"]
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "config.json"
            config.write_text('{"Pipeline":{"PublisherPrincipalId":"33333333-3333-3333-3333-333333333333"}}')
            with patch("publish_trainer.contracts", return_value={"foundation": receipt, "scopeFingerprints": {key: "a" * 64}}), \
                 patch("publish_trainer.Azure", return_value=azure):
                with self.assertRaisesRegex(ValueError, "Pipeline:Regions:" + key + ":KeyVaultId is required to publish"):
                    publish_trainer.promote(config, artifact, key, Path(temp))

    @unittest.skipUnless(os.environ.get("STUDIO_CONTRACT_TOOL"), "Build shared .NET contract validator.")
    def test_successful_promotion_and_retry_produce_an_actual_valid_trainer_binding(self):
        settings_path = ROOT / "contracts/fixtures/studio-settings.json"
        receipt = fixture_json()["foundation"]
        key, store = receipt["controlRegion"], Container()
        artifact, record = self.sealed(store)
        target, azure = self.environment(receipt, key)
        azure.container = lambda _: store
        image = target["registryId"].rsplit("/", 1)[1] + ".azurecr.io/wan/yolox@" + record["imageDigest"]
        registrations = []
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            settings = json.loads(settings_path.read_text())
            settings["Pipeline"]["PublisherPrincipalId"] = azure.actor_id
            (path / "settings.json").write_bytes(json_bytes(settings))
            with patch("publish_trainer.Azure", return_value=azure), patch("publish_trainer.promote_image", return_value=image), \
                 patch("publish_trainer.registration", side_effect=lambda _, resource_id, expected: registrations.append(resource_id)):
                first = publish_trainer.promote(path / "settings.json", artifact, key, path)
                original = store.data[first]
                second = publish_trainer.promote(path / "settings.json", artifact, key, path)
        self.assertEqual(f"trainers/yolox/{artifact}.json", first)
        self.assertEqual(first, second)
        self.assertEqual(original, store.data[second])
        binding = json.loads(original)
        self.assertEqual(image, binding["image"])
        self.assertEqual("vision.trainers.yolox", binding["module"])
        self.assertEqual(1, len(set(registrations)))
        self.assertTrue(registrations[0].endswith("/versions/1"))


class SavedPlanTests(unittest.TestCase):
    def test_changed_binary_cannot_reach_apply(self):
        with tempfile.TemporaryDirectory() as temp:
            root, store = Path(temp), Container()
            config, backend, work = root / "config.json", root / "backend.json", root / "work"
            config.write_text("{}")
            backend.write_text("{}")
            env = {"GITHUB_SHA": "a" * 40, "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"}
            with patch.dict(os.environ, env):
                record = foundation.provenance(config, backend)
                record.update(planSha256=hashlib.sha256(b"reviewed").hexdigest(), jsonSha256="a" * 64)
                store.data["plans/123-1/provenance.json"] = json_bytes(record)
                store.data["plans/123-1/reviewed.plan"] = b"changed"
                with patch("foundation.context", return_value=(None, {}, store)), \
                     patch("foundation.directory", side_effect=lambda *args: work.mkdir()), patch("foundation.run") as invoke:
                    with self.assertRaises(ValueError):
                        foundation.apply(config, backend, work)
                    invoke.assert_not_called()

    def test_wrong_run_or_backend_binding_cannot_even_initialize_apply_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            root, store = Path(temp), Container()
            config, backend = root / "config", root / "backend"
            config.write_text("{}")
            backend.write_text("{}")
            env = {"GITHUB_SHA": "a" * 40, "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1"}
            with patch.dict(os.environ, env):
                record = foundation.provenance(config, backend)
                record["backendSha256"] = "unexpected"
                store.data["plans/123-1/provenance.json"] = json_bytes(record)
                with patch("foundation.context", return_value=(None, {}, store)), patch("foundation.directory") as initialize:
                    with self.assertRaises(ValueError):
                        foundation.apply(config, backend, root / "work")
                    initialize.assert_not_called()


if __name__ == "__main__":
    unittest.main()
