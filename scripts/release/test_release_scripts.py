"""Offline checks of the standalone release scripts: the shared helper, and argument handling of the bash and PowerShell
entry points. No Azure, network, container engine or package download."""
import contextlib
import io
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import studio_release  # noqa: E402

ROOT = studio_release.ROOT
HERE = Path(__file__).resolve().parent
ARTIFACT = "c26383a8b01bfb720c7fc9774d60af808fc7de7ed7a4f2a57675f33d81736841"


def fixture():
    return json.loads((ROOT / "contracts/fixtures/studio-settings.json").read_text(encoding="utf-8"))


def run_helper(*argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = studio_release.main(list(argv))
    return code, out.getvalue(), err.getvalue()


class Helper(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="wan-release-test-"))
        self.addCleanup(shutil.rmtree, self.temp, ignore_errors=True)
        self.settings = self.temp / "settings.json"
        self.settings.write_text(json.dumps(fixture()), encoding="utf-8")

    def values(self, output):
        return dict(line.split("=", 1) for line in output.splitlines() if "=" in line)

    def test_settings_are_copied_outside_the_checkout_and_promotion_defaults_to_every_region(self):
        out = self.temp / "job.json"
        code, stdout, _ = run_helper("settings", "--settings", str(self.settings), "--out", str(out), "--stage", "promote")
        self.assertEqual(0, code)
        values = self.values(stdout)
        self.assertEqual("eastus2", values["CONTROL_REGION"])
        self.assertEqual(sorted(fixture()["Studio"]["Regions"]), values["REGIONS"].split())
        self.assertEqual(self.settings.read_text(encoding="utf-8"), out.read_text(encoding="utf-8"))

    def test_settings_refusals_explain_what_to_fix(self):
        out = str(self.temp / "job.json")
        cases = [
            (lambda s: s["Pipeline"].pop("PublisherPrincipalId"), "build", [], "PublisherPrincipalId"),
            (lambda s: s["Pipeline"].pop("SiteId"), "deploy", [], "Pipeline.SiteId"),
            (lambda s: None, "promote", ["nowhere"], "Unknown region"),
            (lambda s: s["Pipeline"]["Regions"]["westeurope"].pop("KeyVaultId"), "promote", ["westeurope"], "KeyVaultId"),
            (lambda s: None, "build", ["eastus2"], "always uses the control region"),
            (lambda s: s["Studio"].update(ControlRegion="mars"), "build", [], "ControlRegion"),
        ]
        for change, stage, regions, message in cases:
            settings = fixture()
            change(settings)
            self.settings.write_text(json.dumps(settings), encoding="utf-8")
            arguments = ["settings", "--settings", str(self.settings), "--out", out, "--stage", stage]
            for key in regions:
                arguments += ["--region", key]
            with self.subTest(message=message):
                code, _, err = run_helper(*arguments)
                self.assertEqual(1, code)
                self.assertIn(message, err)

    def test_settings_inside_the_checkout_or_with_duplicate_keys_are_refused(self):
        code, _, err = run_helper("settings", "--settings", str(ROOT / "contracts/fixtures/studio-settings.json"),
                                  "--out", str(self.temp / "job.json"), "--stage", "build")
        self.assertEqual(1, code)
        self.assertIn("outside the repository checkout", err)
        self.settings.write_text('{"Studio": {}, "Studio": {}}', encoding="utf-8")
        code, _, err = run_helper("settings", "--settings", str(self.settings), "--out", str(self.temp / "j.json"), "--stage", "build")
        self.assertEqual(1, code)
        self.assertIn("duplicate property", err)

    def test_settings_fall_back_to_the_ci_variables(self):
        with patch.dict(os.environ, {"STUDIO_SETTINGS_JSON": json.dumps(fixture())}):
            os.environ.pop("STUDIO_SETTINGS_PATH", None)
            code, stdout, _ = run_helper("settings", "--out", str(self.temp / "job.json"), "--stage", "deploy")
        self.assertEqual(0, code)
        self.assertEqual("eastus2", self.values(stdout)["REGIONS"])

    def test_feed_must_be_one_approved_feed_without_credentials(self):
        self.assertEqual(0, run_helper("feed", "--url", "https://packagefeedproxy.microsoft.io/pypi/simple")[0])
        for url in ("", "https://pypi.org/simple", "https://user:pw@pkgs.dev.azure.com/o/_packaging/f/pypi/simple"):
            with self.subTest(url=url):
                self.assertEqual(1, run_helper("feed", "--url", url)[0])

    def az_fake(self, oid, subscription=None, tenant=None):
        settings = fixture()
        tenant = tenant or settings["Studio"]["TenantId"]
        claims = json.dumps({"oid": oid, "tid": tenant}).encode()
        import base64
        token = "e30." + base64.urlsafe_b64encode(claims).decode().rstrip("=") + ".sig"

        def az(*arguments):
            if arguments[:2] == ("account", "show"):
                return {"tenantId": tenant, "id": subscription or settings["Studio"]["SubscriptionId"],
                        "environmentName": "AzureCloud", "user": {"type": "user", "name": "someone@example.com"}}
            return {"accessToken": token}
        return az

    def test_identity_requires_the_publisher_to_build_or_promote_but_not_to_deploy(self):
        publisher = fixture()["Pipeline"]["PublisherPrincipalId"]
        other = "95344f44-1fb6-4d90-bd7f-7d4a61362650"
        with patch.object(studio_release, "az", side_effect=self.az_fake(publisher)):
            code, stdout, _ = run_helper("identity", "--settings", str(self.settings), "--stage", "promote")
        self.assertEqual((0, publisher), (code, self.values(stdout)["CALLER"]))
        with patch.object(studio_release, "az", side_effect=self.az_fake(other)):
            code, _, err = run_helper("identity", "--settings", str(self.settings), "--stage", "build")
            self.assertEqual(1, code)
            self.assertIn("az ad signed-in-user show", err)
            self.assertEqual(0, run_helper("identity", "--settings", str(self.settings), "--stage", "deploy")[0])
        with patch.object(studio_release, "az", side_effect=self.az_fake(publisher, subscription="00000000-0000-0000-0000-000000000009")):
            code, _, err = run_helper("identity", "--settings", str(self.settings), "--stage", "build")
            self.assertEqual(1, code)
            self.assertIn("az account set", err)

    def test_network_requires_every_endpoint_to_resolve_privately_and_answer(self):
        def resolve(host, port, type=None):
            address = "52.1.2.3" if host.endswith(".vault.azure.net") else "10.1.2.3"
            return [(socket.AF_INET, socket.SOCK_STREAM, 6, "", (address, port))]
        with patch.object(studio_release.socket, "getaddrinfo", side_effect=resolve), \
                patch.object(studio_release.socket, "create_connection", return_value=contextlib.nullcontext()):
            code, stdout, _ = run_helper("network", "--settings", str(self.settings), "--stage", "build")
            self.assertEqual(0, code)
            self.assertEqual(2, len(self.values(stdout)["HOSTS"].split()))
            code, _, err = run_helper("network", "--settings", str(self.settings), "--stage", "promote", "--component", "gpu",
                                      "--region", "eastus2")
            self.assertEqual(1, code)
            self.assertIn("vault.azure.net resolves to public", err)
            code, _, _ = run_helper("network", "--settings", str(self.settings), "--stage", "promote", "--component", "yolox",
                                    "--region", "eastus2")
            self.assertEqual(0, code, "Trainer promotion does not use the workspace's private connectivity hosts.")

    def test_engine_preflight_names_the_missing_engine(self):
        with patch.dict(os.environ, {"STUDIO_DOCKER": ""}), patch("shutil.which", return_value=None):
            code, _, err = run_helper("engine", "--engine", "docker")
        self.assertEqual(1, code)
        self.assertIn("docker was not found", err)

    def banner(self, *arguments):
        code, stdout, _ = run_helper("banner", "--artifact", ARTIFACT, "--settings", "/s.json", *arguments)
        self.assertEqual(0, code)
        return stdout

    def test_banners_print_the_artifact_id_prominently_with_the_next_step(self):
        text = self.banner("--stage", "build", "--component", "gpu", "--shell", "bash")
        self.assertIn("RELEASE ARTIFACT ID", text)
        self.assertIn("\n  " + ARTIFACT + "\n", text)
        self.assertIn(f"scripts/release/promote-release.sh --settings /s.json --artifact {ARTIFACT}", text)
        text = self.banner("--stage", "build", "--component", "gpu", "--shell", "pwsh")
        self.assertIn(f"Publish-StudioRelease.ps1 -Settings /s.json -Artifact {ARTIFACT}", text)

        text = self.banner("--stage", "promote", "--component", "gpu", "--shell", "pwsh", "--ok", "eastus2",
                           "--failed", "westeurope", "--failed", "swedencentral")
        self.assertIn("Promoted to:      eastus2", text)
        self.assertIn("Not promoted to:  westeurope, swedencentral", text)
        for phrase in ("Administration", "'Release artifact ID'", "'Select release'", "starts no compute", "Off",
                       "'Start <region>'", "-Region westeurope,swedencentral"):
            self.assertIn(phrase, text)
        text = self.banner("--stage", "promote", "--component", "gpu", "--shell", "bash", "--failed", "westeurope")
        self.assertNotIn("'Select release'", text, "Nothing was promoted, so there is nothing to select.")
        self.assertIn("--region westeurope", text)

        for stage in ("build", "deploy"):
            text = self.banner("--stage", stage, "--component", "server", "--shell", "bash")
            self.assertIn("SERVER ARTIFACT ID", text)
            self.assertIn("never paste it into Administration", text)
        self.assertIn("--component yolox", self.banner("--stage", "build", "--component", "yolox", "--shell", "bash"))

    def test_banner_refuses_a_malformed_artifact(self):
        self.assertEqual(1, run_helper("banner", "--stage", "build", "--artifact", "abc", "--shell", "bash")[0])


BASH = shutil.which("bash")
PWSH = shutil.which("pwsh")


def shell(command, *arguments):
    environment = {key: value for key, value in os.environ.items() if not key.startswith("STUDIO_SETTINGS")}
    return subprocess.run([*command, *arguments], capture_output=True, text=True, cwd=ROOT, env=environment, timeout=120)


@unittest.skipUnless(BASH and PWSH, "bash and pwsh are both needed for the parity checks")
class EntryPoints(unittest.TestCase):
    """Help and usage errors fail before any work, in both shells, for the same mistakes."""

    SCRIPTS = (("build-artifact.sh", "Build-StudioArtifact.ps1"), ("promote-release.sh", "Publish-StudioRelease.ps1"),
               ("deploy-server.sh", "Deploy-StudioServer.ps1"))

    def bash(self, script, *arguments):
        return shell([BASH, f"scripts/release/{script}"], *arguments)

    def pwsh(self, script, *arguments):
        return shell([PWSH, "-NoProfile", "-File", str(HERE / script)], *arguments)

    def test_help_describes_every_script(self):
        for bash_script, pwsh_script in self.SCRIPTS:
            with self.subTest(script=bash_script):
                result = self.bash(bash_script, "--help")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("Usage: scripts/release/" + bash_script, result.stdout)
                self.assertIn("--container-engine", result.stdout)
                result = self.pwsh(pwsh_script, "-Help")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("SYNOPSIS", result.stdout)
                self.assertIn("-ContainerEngine", result.stdout)

    def test_the_same_mistakes_are_refused_in_both_shells_before_any_work(self):
        missing = str(Path(tempfile.gettempdir()) / "wan-no-such-settings.json")
        cases = [
            (("build-artifact.sh",), (), ("Build-StudioArtifact.ps1",), (), "containerengine"),
            (("build-artifact.sh",), ("--container-engine", "nerdctl"), ("Build-StudioArtifact.ps1",), ("-ContainerEngine", "Nerdctl"), "docker"),
            (("build-artifact.sh",), ("--container-engine", "wslc", "--component", "yolox"),
             ("Build-StudioArtifact.ps1",), ("-ContainerEngine", "Wslc", "-Component", "yolox"), "rainer"),
            (("promote-release.sh",), (), ("Publish-StudioRelease.ps1",), (), "rtifact"),
            (("promote-release.sh",), ("--artifact", "abc"), ("Publish-StudioRelease.ps1",), ("-Artifact", "abc"), "64"),
            (("deploy-server.sh",), (), ("Deploy-StudioServer.ps1",), (), "exactly one"),
            (("deploy-server.sh",), ("--build",), ("Deploy-StudioServer.ps1",), ("-Build",), "ontainer"),
            (("promote-release.sh",), ("--artifact", ARTIFACT, "--settings", missing),
             ("Publish-StudioRelease.ps1",), ("-Artifact", ARTIFACT, "-Settings", missing), "Settings file not found"),
        ]
        for bash_script, bash_arguments, pwsh_script, pwsh_arguments, message in cases:
            with self.subTest(case=bash_arguments):
                result = self.bash(bash_script[0], *bash_arguments)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message.lower(), (result.stdout + result.stderr).lower().replace("-", ""))
                result = self.pwsh(pwsh_script[0], *pwsh_arguments)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message.lower(), (result.stdout + result.stderr).lower().replace("-", ""))


if __name__ == "__main__":
    unittest.main()
