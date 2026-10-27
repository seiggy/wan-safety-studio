"""Offline build-boundary checks. Optional pinned source fixture contains code only, never weights."""
from __future__ import annotations

import ast
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import unittest
from collections import namedtuple
from types import SimpleNamespace
from unittest.mock import patch

import artifacts
import models
import prepare
import verify

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def source_archive():
    content = {name: b"# reviewed helper\n" for name in prepare.HELPERS}
    content.update({
        "src/main.py": b"# pinned ComfyUI\n", "src/folder_paths.py": b"# model paths\n",
        "src/comfy/__init__.py": b"", "src/comfy/ldm/models/autoencoder.py": b"# code\n",
        "src/comfy_api/input/video_types.py": b"# code\n",
        "src/models/private.safetensors": b"not a weight",
        "src/.env.secret": b"not a secret", "src/.git/config": b"not git",
        "azureml/config.py": b"raise AssertionError('operator was packaged')",
        "azureml/web_submit.py": b"raise AssertionError('UI was packaged')",
        "LICENSE": b"MIT attribution fixture\n", "NOTICE.txt": b"Provenance fixture\n",
    })
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w") as archive:
        for name, value in content.items():
            item = tarfile.TarInfo(name)
            item.size = len(value)
            archive.addfile(item, io.BytesIO(value))
    return stream.getvalue()


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.root = Path(self.scratch.name)
        payload = b"tiny model fixture"
        self.pinned = [entry | {"size": len(payload), "sha256": hashlib.sha256(payload).hexdigest()} for entry in models.load_wan_manifest()]
        self.model_patch = patch.object(models, "load_wan_manifest", return_value=self.pinned)
        self.model_patch.start()
        self.verify_patch = patch.object(verify, "load_wan_manifest", return_value=self.pinned)
        self.verify_patch.start()

    def tearDown(self):
        self.verify_patch.stop()
        self.model_patch.stop()
        self.scratch.cleanup()

    def stage(self, name="stage"):
        output = self.root / name
        def git(_, command, *args):
            if command == "rev-parse":
                return prepare.SOURCE_SHA.encode()
            self.assertEqual(command, "archive")
            return source_archive()
        with patch.object(prepare, "run_git", side_effect=git):
            prepare.prepare(self.root / "source", output, ROOT / "contracts" / "fixtures" / "wan-workflows")
        return output

    def assets(self):
        cache, cards, output = self.root / "cache", self.root / "cards", self.root / "assets"
        payload = b"tiny model fixture"
        cards.mkdir()
        for entry in self.pinned:
            path = cache / entry["folder_name"] / entry["filename"]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
            repository, revision = re.fullmatch(r"https://huggingface.co/([^/]+/[^/]+)/resolve/([a-f0-9]{40})/.+", entry["url"]).groups()
            (cards / (repository.replace("/", "--") + "--README.md")).write_text("Pinned model card fixture\n")
        with patch.object(models.urllib.request, "urlopen", side_effect=AssertionError("No network")):
            records = models.stage_wan_assets(output, cache, cards)
        artifacts.write_json(output / "models.json", {"models": records, "licenses": artifacts.files(output / "licenses")})
        return output, records, cache, cards, self.pinned[0]

    def image_metadata(self, stage):
        selected = json.loads((stage / "stage.json").read_text())
        return json.dumps([{"Os": "linux", "Architecture": "amd64",
                            "Config": {"Labels": {"studio.runtime-lock-sha256": selected["dependencyLockSha256"]}}}])

    def test_stage_selects_only_runtime_closure_and_keeps_zero_byte_package_files(self):
        stage = self.stage()
        names = {file["path"] for file in artifacts.files(stage / "code")}
        self.assertTrue(prepare.HELPERS.issubset(names))
        self.assertIn("src/comfy/__init__.py", names)
        self.assertIn("src/comfy/ldm/models/autoencoder.py", names)
        self.assertIn("src/comfy_api/input/video_types.py", names)
        self.assertFalse(any(name.startswith("azureml/") and name not in prepare.HELPERS for name in names))
        self.assertFalse(any(".env" in name or ".safetensors" in name or ".git" in name for name in names))

    def test_code_archive_is_deterministic_and_independent_of_output_path(self):
        a, b = self.stage("a"), self.stage("b")
        self.assertEqual(artifacts.sha256(a / "code.tar"), artifacts.sha256(b / "code.tar"))
        self.assertEqual((a / "stage.json").read_bytes(), (b / "stage.json").read_bytes())
        with tarfile.open(a / "code.tar") as archive:
            self.assertTrue(all(item.mtime == 0 and item.uid == 0 and item.gid == 0 and item.mode == 0o644 for item in archive))

    def test_never_merge_into_an_existing_build(self):
        self.stage()
        with self.assertRaisesRegex(ValueError, "new output"):
            self.stage()

    def test_wrong_commit_and_unsafe_paths_fail_closed(self):
        with patch.object(prepare, "run_git", return_value=b"another-commit"):
            with self.assertRaises(ValueError):
                prepare.stage_source(self.root, self.root / "code")
        for name in ("src/../../secret.py", "/src/main.py", "src\\main.py", "src/.cache/token", "src/output/clip.webm", "src/x.gguf"):
            with self.subTest(name=name):
                self.assertFalse(prepare.code_path(name))

    def test_source_staging_cannot_lazy_fetch_or_smudge_lfs(self):
        with patch.object(prepare.subprocess, "check_output", return_value=b"pinned") as command:
            self.assertEqual(b"pinned", prepare.run_git(self.root, "rev-parse", prepare.SOURCE_SHA))
        self.assertEqual("1", command.call_args.kwargs["env"]["GIT_NO_LAZY_FETCH"])
        self.assertEqual("1", command.call_args.kwargs["env"]["GIT_LFS_SKIP_SMUDGE"])
        self.assertIn("filter.lfs.process=", command.call_args.args[0])

    def test_helper_processes_receive_no_azure_or_application_secrets(self):
        with patch.dict(os.environ, {"AZURE_CLIENT_SECRET": "fixture-secret", "ARM_CLIENT_SECRET": "fixture-secret",
                                    "Auth__ClientSecret": "fixture-secret", "PATH": "fixture-path"}):
            environment = artifacts.process_environment()
        self.assertEqual("fixture-path", environment["PATH"])
        self.assertNotIn("AZURE_CLIENT_SECRET", environment)
        self.assertNotIn("ARM_CLIENT_SECRET", environment)
        self.assertNotIn("Auth__ClientSecret", environment)

    def test_runtime_imports_reject_an_operator_or_sdk_dependency(self):
        stage = self.stage()
        for text in ("from . import credential", "from azure.ai.ml import MLClient", "import cost_guard"):
            (stage / "code" / "azureml" / "run_workflow_job.py").write_text(text)
            with self.assertRaises(ValueError):
                prepare.validate_import_closure(stage / "code")

    def test_all_served_mode_models_are_in_the_pinned_snapshot(self):
        stage = self.stage()
        index = json.loads((stage / "workflows" / "index.json").read_text())
        self.assertEqual(set(index), {"draft", "fast", "turbo", "prime"})
        selected = json.loads((stage / "stage.json").read_text())
        self.assertIn("UNETLoader", selected["requiredNodes"])
        self.assertIn("LoraLoaderModelOnly", selected["requiredNodes"])
        self.assertTrue(selected["buildInputs"])

    def test_unrendered_workflow_is_rejected(self):
        source = self.root / "workflows"
        source.mkdir()
        (source / "index.json").write_text('{"prime":"prime.json"}')
        (source / "prime.json").write_text('{"1":{"class_type":"LoadImage","inputs":{"image":"{{input}}"}}}')
        with self.assertRaises(ValueError):
            prepare.stage_workflows(source, self.root / "out")

    def test_model_snapshot_reuses_local_hashes_without_model_metadata_or_downloads(self):
        assets, records, cache, cards, entry = self.assets()
        with patch.object(models.urllib.request, "urlopen", side_effect=AssertionError("No network")):
            again = models.stage_wan_assets(self.root / "assets-two", cache, cards)
        self.assertEqual(records, again)
        self.assertEqual(artifacts.files(assets / "licenses"), artifacts.files(self.root / "assets-two" / "licenses"))

    def test_corrupt_local_model_has_no_download_fallback(self):
        _, _, cache, cards, entry = self.assets()
        (cache / entry["folder_name"] / entry["filename"]).write_bytes(b"corrupt")
        with patch.object(models, "load_wan_manifest", return_value=[entry]), patch.object(models.urllib.request, "urlopen", side_effect=AssertionError("No fallback")):
            with self.assertRaisesRegex(ValueError, "SHA256"):
                models.stage_wan_assets(self.root / "bad", cache, cards, download=True)

    def test_cross_volume_model_copy_fallback(self):
        source, target = self.root / "source.bin", self.root / "target.bin"
        source.write_bytes(b"tiny")
        with patch.object(Path, "hardlink_to", side_effect=OSError("different volume")):
            models.stage_local_model(source, target, artifacts.sha256(source), 4)
        self.assertEqual(source.read_bytes(), target.read_bytes())

    def test_missing_licenses_fail_instead_of_silently_dropping_attribution(self):
        _, _, cache, cards, entry = self.assets()
        for path in cards.iterdir():
            path.unlink()
        with patch.object(models, "load_wan_manifest", return_value=[entry]):
            with self.assertRaisesRegex(ValueError, "model card"):
                models.stage_wan_assets(self.root / "missing-licence", cache, cards)

    def test_final_manifest_is_portable_and_sealed_only_after_cpu_probe(self):
        stage = self.stage()
        assets, _, _, _, _ = self.assets()
        image = "example.invalid/wan@sha256:" + "a" * 64
        with patch.object(verify.subprocess, "check_output", return_value=self.image_metadata(stage)), patch.object(verify.subprocess, "run") as run:
            manifest = verify.verify(stage, assets, image)
        self.assertEqual(manifest["artifactId"], artifacts.artifact_id(manifest))
        self.assertEqual(manifest["dependencyLockSha256"], artifacts.sha256(stage / "image" / "requirements-runtime.lock"))
        self.assertNotIn(str(self.root), json.dumps(manifest))
        self.assertNotIn("example.invalid", json.dumps(manifest))
        self.assertIn("--pull", run.call_args.args[0])
        self.assertIn("never", run.call_args.args[0])
        self.assertNotIn("--gpus", run.call_args.args[0])
        with self.assertRaises(ValueError):
            verify.verify(stage, assets, image)

    def test_cpu_failure_writes_no_manifest(self):
        stage = self.stage()
        assets, _, _, _, _ = self.assets()
        with patch.object(verify.subprocess, "check_output", return_value=self.image_metadata(stage)), patch.object(verify.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "docker")):
            with self.assertRaises(subprocess.CalledProcessError):
                verify.verify(stage, assets, "example/wan@sha256:" + "a" * 64)
        self.assertFalse((stage / "manifest.json").exists())

    def test_incomplete_model_snapshot_cannot_be_sealed_even_if_remaining_files_are_valid(self):
        stage = self.stage()
        assets, _, _, _, _ = self.assets()
        snapshot = json.loads((assets / "models.json").read_text())
        removed = snapshot["models"].pop()
        (assets / "models" / removed["path"]).unlink()
        (assets / "models.json").write_text(json.dumps(snapshot))
        with self.assertRaisesRegex(ValueError, "reviewed WAN manifest"):
            verify.verify(stage, assets, "example/wan@sha256:" + "a" * 64)
        self.assertFalse((stage / "manifest.json").exists())
    def test_probe_success_cannot_seal_files_changed_during_validation(self):
        stage = self.stage()
        assets, _, _, _, _ = self.assets()
        def mutate(*_, **__):
            (stage / "code" / "src" / "main.py").write_text("# altered\n")
        with patch.object(verify.subprocess, "check_output", return_value=self.image_metadata(stage)), patch.object(verify.subprocess, "run", side_effect=mutate):
            with self.assertRaisesRegex(ValueError, "changed"):
                verify.verify(stage, assets, "example/wan@sha256:" + "a" * 64)
        self.assertFalse((stage / "manifest.json").exists())

    def test_tampered_code_models_context_and_extra_outputs_fail_before_probe(self):
        for section in ("code", "image", "models", "licenses", "build-inputs"):
            with self.subTest(section=section), tempfile.TemporaryDirectory() as extra:
                self.root = Path(extra)
                stage = self.stage()
                assets, _, _, _, _ = self.assets()
                root = stage if section in {"code", "image", "build-inputs"} else assets
                (root / section / "unexpected.txt").write_text("unexpected")
                with patch.object(verify.subprocess, "run", side_effect=AssertionError("must fail before Docker")):
                    with self.assertRaises(ValueError):
                        verify.verify(stage, assets, "example/wan@sha256:" + "a" * 64)

    def test_image_tags_config_digests_and_command_injection_are_not_accepted(self):
        for image in ("wan:latest", "sha256:" + "a" * 64, "wan@sha256:" + "a" * 64 + ";echo bad"):
            with self.assertRaises(ValueError):
                verify.probe_arguments(image, self.root)

    def test_probe_imports_main_before_nodes_and_only_reads_actual_staged_source(self):
        tree = ast.parse((HERE / "probe.py").read_text())
        imports = [node.names[0].name for node in ast.walk(tree) if isinstance(node, ast.Import)]
        self.assertLess(imports.index("main"), imports.index("nodes"))
        args = verify.probe_arguments("example/wan@sha256:" + "a" * 64, self.root)
        self.assertIn("--read-only", args)
        self.assertIn("none", args)
        self.assertIn("/check:rw,mode=1777", args)
        self.assertIn("NVIDIA_VISIBLE_DEVICES=void", args)
        self.assertIn("HOME=/check", args)
        self.assertIn("XDG_CACHE_HOME=/check/cache", args)
        self.assertIn(f"type=bind,source={(self.root / 'code').resolve()},target=/code,readonly", args)
        text = (HERE / "probe.py").read_text()
        self.assertIn("init_custom_nodes=False", text)
        self.assertIn("--cpu", text)
        self.assertIn("for mode, filename in index.items()", text)

    def test_actual_probe_code_executes_staged_imports_and_rejects_missing_nodes(self):
        stage = self.stage()
        code = stage / "code" / "src"
        (code / "main.py").write_text("import os\nos.environ['STAGED_MAIN_IMPORTED']='yes'\n")
        (code / "torch.py").write_text("from types import SimpleNamespace\ncuda=SimpleNamespace(is_available=lambda:False)\n")
        selected = json.loads((stage / "stage.json").read_text())
        nodes = ("import os\nassert os.environ['STAGED_MAIN_IMPORTED']=='yes'\n"
                 "async def init_extra_nodes(**kwargs):\n    assert kwargs=={'init_custom_nodes':False,'init_api_nodes':False}\n")
        check = self.root / "check"
        check.mkdir()
        command = [sys.executable, "-B", "-c",
                   f"import sys;sys.path.insert(0,{str(HERE)!r});from probe import probe;from pathlib import Path;"
                   f"probe(Path({str(stage / 'code')!r}),Path({str(stage / 'workflows')!r}),Path({str(check)!r}))"]
        (code / "nodes.py").write_text(nodes + f"NODE_CLASS_MAPPINGS={dict.fromkeys(selected['requiredNodes'])!r}\n")
        result = subprocess.run(command, check=True, capture_output=True, text=True)
        self.assertIn("draft, fast, prime, turbo", result.stdout)
        for child in (check / "user", check / "temp"):
            child.rmdir()
        (code / "nodes.py").write_text(nodes + "NODE_CLASS_MAPPINGS={}\n")
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Missing nodes", result.stderr)

    def test_build_identity_matches_both_dotnet_golden_vectors(self):
        values = json.loads((ROOT / "contracts" / "fixtures" / "studio.json").read_text())
        for name in ("build", "unicodeBuild"):
            self.assertEqual(values[name]["artifactId"], artifacts.artifact_id(values[name]))

    def test_image_and_build_locks_are_exact_hash_pinned_without_feed_urls_or_azure_sdk(self):
        for name in ("requirements-runtime.lock", "requirements-build.lock"):
            text = (HERE / name).read_text()
            self.assertNotIn("https://", text)
            self.assertNotIn("--hash=md5:", text)
            chunks = text.replace("\\\n", "").splitlines()
            self.assertTrue(len(chunks) > 3)
            self.assertTrue(all(re.fullmatch(r"[A-Za-z0-9_.-]+==[A-Za-z0-9.+_-]+(?: ; .+?)?(?:\s+--hash=sha256:[a-f0-9]{64})+", line) for line in chunks))
            self.assertNotIn("azure-ai-ml", text)
        runtime = (HERE / "requirements-runtime.lock").read_text()
        self.assertIn("torch==2.8.0 ", runtime)
        self.assertIn("torchvision==0.23.0 ", runtime)
        docker = (HERE / "Dockerfile").read_text()
        self.assertIn("--require-hashes", docker)
        self.assertIn("--only-binary=:all:", docker)
        self.assertNotIn("pip install --upgrade", docker)
        self.assertNotIn("apt-get", docker)

    def test_experimental_metadata_is_not_imported_from_the_old_portal(self):
        profile = json.loads((HERE / "profiles.json").read_text())
        self.assertEqual(profile["minimax_h3"]["sourceSha"], "95d755cd8107a72258d452b5d3657273d571f07d")
        patch_text = (HERE / "runtime-ltx.patch").read_text()
        self.assertIn('+        raise FileNotFoundError', patch_text)
        self.assertNotIn("+        download_file", patch_text)
        self.assertFalse((ROOT / "azureml").exists(), "The retired Python operator/portal must not return.")

    @unittest.skipUnless(os.environ.get("WAN_STUDIO_P2_UPSTREAM"), "Set WAN_STUDIO_P2_UPSTREAM to a Git source fixture containing the pinned commit.")
    def test_exact_pinned_source_and_ltx_runtime_patch(self):
        source = Path(os.environ["WAN_STUDIO_P2_UPSTREAM"])
        stage = self.root / "real-stage"
        prepare.prepare(source, stage, ROOT / "contracts" / "fixtures" / "wan-workflows")
        records = artifacts.files(stage / "code")
        self.assertGreater(len(records), len(prepare.HELPERS))
        self.assertTrue((stage / "code" / "src" / "comfy" / "ldm" / "models" / "autoencoder.py").is_file())
        code = subprocess.check_output(["git", "-C", str(source), "show", prepare.SOURCE_SHA + ":azureml/bootstrap_ltx_and_run.py"])
        root = self.root / "ltx"
        (root / "azureml").mkdir(parents=True)
        (root / "azureml" / "bootstrap_ltx_and_run.py").write_bytes(code)
        subprocess.run(["git", "init", "--quiet", str(root)], check=True)
        subprocess.run(["git", "-C", str(root), "apply", "--ignore-space-change", "--check", str(HERE / "runtime-ltx.patch")], check=True)
        subprocess.run(["git", "-C", str(root), "apply", "--ignore-space-change", str(HERE / "runtime-ltx.patch")], check=True)
        tree = ast.parse((root / "azureml" / "bootstrap_ltx_and_run.py").read_text())
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "stage_required_models")
        ref = namedtuple("ModelReference", "folder_name filename")("checkpoints", "tiny.safetensors")
        namespace = {"Path": Path, "LTX_MODEL_URLS": {ref: "must-not-download"},
                     "workflow_utils": SimpleNamespace(load_workflow_payload=lambda _: {},
                                                       collect_required_model_references=lambda _: {ref}),
                     "download_file": lambda *_: self.fail("Paid worker attempted a model download")}
        exec(compile(ast.Module(body=[function], type_ignores=[]), "<patched-ltx-runtime>", "exec"), namespace)
        with self.assertRaisesRegex(FileNotFoundError, "Prepare is required"):
            namespace["stage_required_models"](root / "workflow.json", root / "missing-models")
        print(f"Exact pinned WAN payload: {len(records)} files, {sum(file['bytes'] for file in records)} bytes; no operator modules or weights.")


if __name__ == "__main__":
    unittest.main(verbosity=2)
