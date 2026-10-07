"""Trainer contract + YOLOX pure-logic checks. No GPU, Docker or YOLOX install needed."""
import json
from pathlib import Path
import tempfile
import unittest

from vision.domain import ValidationError
from vision.adapters.storage import LocalStore
from vision.service import VisionService
from vision.trainers.registry import TrainerRegistry, load_manifest
from vision.trainers.sdk import DeepStreamConfig, Metrics, OnnxModel, TrainerInfo, run_cli
from vision.trainers.yolox.deepstream import render_deepstream
from vision.trainers.yolox.exp import render_exp
from vision.trainers.yolox.layout import to_yolox_layout
from vision.trainers.yolox.parse import parse_coco_summary
from vision.trainers.yolox.presets import PRESETS
from vision.trainers.yolox.trainer import YoloxTrainer
from vision.usecases.train import TrainingService
from test_vision import FakeMedia


class FakeTrainer:
    """A minimal conforming trainer; any real trainer class can be added to CONTRACT_TRAINERS below."""
    info = TrainerInfo("fake", "1", "MIT", "MIT", ("a", "b"), "a")

    def train(self, request):
        path = request.out / "best.w"
        path.write_text(request.preset)
        return path

    def evaluate(self, weights, request):
        return Metrics(0.5, 0.25, {c: 0.1 for c in request.classes})

    def export(self, weights, out):
        (out / "model.onnx").write_bytes(b"onnx")
        return OnnxModel(out / "model.onnx", (64, 64))

    def deepstream(self, model, classes):
        return DeepStreamConfig({"config_infer_primary.txt": f"n={len(classes)}"}, ("note",))


CONTRACT_TRAINERS = {"fake": FakeTrainer, "yolox": YoloxTrainer}


class Contract(unittest.TestCase):
    def test_trainer_info_is_well_formed(self):
        for name, cls in CONTRACT_TRAINERS.items():
            info = cls.info
            self.assertEqual(info.name, name)
            self.assertIn(info.default_preset, info.presets)
            self.assertTrue(info.license_code and info.license_weights)
            for method in ("train", "evaluate", "export", "deepstream"):
                self.assertTrue(callable(getattr(cls, method)), f"{name}.{method}")

    def test_cli_runs_all_steps_and_writes_results(self):
        with tempfile.TemporaryDirectory() as tmp:
            data, out = Path(tmp) / "d", Path(tmp) / "o"
            data.mkdir()
            (data / "classes.json").write_text('["leak", "fire"]')
            self.assertEqual(run_cli(FakeTrainer(), ["all", "--data", str(data), "--out", str(out), "--preset", "b"]), 0)
            self.assertEqual((out / "best.w").read_text(), "b")
            self.assertEqual(json.loads((out / "metrics.json").read_text())["map50"], 0.5)
            self.assertTrue((out / "model.onnx").exists() and (out / "deepstream" / "config_infer_primary.txt").exists())
            with self.assertRaises(SystemExit):
                run_cli(FakeTrainer(), ["train", "--data", str(data), "--out", str(out), "--preset", "zzz"])

    def test_registry_lists_yolox_and_skips_template(self):
        found = TrainerRegistry().all()
        self.assertEqual(set(found), {"yolox"})
        self.assertEqual(found["yolox"].presets, ("nano", "tiny", "s"))
        with tempfile.TemporaryDirectory() as tmp:
            bad = Path(tmp) / "x" / "trainer.json"
            bad.parent.mkdir()
            bad.write_text('{"name": "Bad Name"}')
            with self.assertRaises(ValidationError):
                load_manifest(bad)


class Yolox(unittest.TestCase):
    def test_exp_is_valid_python_with_the_run_settings(self):
        source = render_exp(PRESETS["nano"], 3, "/x/coco", "/x/runs", 40)
        compile(source, "exp.py", "exec")
        for expected in ("'yolox-nano'", "num_classes = 3", "max_epoch = 40", "'/x/coco'", "no_aug_epochs = 8"):
            self.assertIn(expected, source)

    def test_summary_parser(self):
        text = (" Average Precision  (AP) @[ IoU=0.50:0.95 | area=   all | maxDets=100 ] = 0.321\n"
                " Average Precision  (AP) @[ IoU=0.50      | area=   all | maxDets=100 ] = 0.654\n"
                "per class AP:\n| class   | AP     |\n|:--------|:-------|\n| leak    | 70.0   |\n| other   | 1.0    |\n")
        ap50, ap, per = parse_coco_summary(text, ["leak", "fire"])
        self.assertEqual((ap50, ap, per), (0.654, 0.321, {"leak": 0.7}))
        with self.assertRaises(ValueError):
            parse_coco_summary("nothing", [])

    def test_layout_and_deepstream(self):
        with tempfile.TemporaryDirectory() as tmp:
            data = Path(tmp) / "d"
            (data / "images").mkdir(parents=True)
            (data / "images" / "v_000001.jpg").write_bytes(b"x")
            coco = {"images": [{"id": 1, "file_name": "images/v_000001.jpg"}], "annotations": [], "categories": []}
            for split in ("train", "val"):
                (data / f"{split}.json").write_text(json.dumps(coco))
            root = to_yolox_layout(data, Path(tmp) / "coco")
            self.assertTrue((root / "train2017" / "v_000001.jpg").exists())
            self.assertEqual(json.loads((root / "annotations" / "instances_val2017.json").read_text())["images"][0]["file_name"], "v_000001.jpg")
        config = render_deepstream(OnnxModel(Path("model.onnx"), (416, 416)), ("leak", "fire"))
        self.assertIn("num-detected-classes=2", config.files["config_infer_primary.txt"])
        self.assertEqual(config.files["labels.txt"], "leak\nfire\n")


class FakeCompute:
    name = "fake"

    def __init__(self, code=0):
        self.code, self.calls = code, []

    def run(self, image, args, data, out, log):
        self.calls.append((image, args, sorted(p.name for p in data.iterdir())))
        (out / "metrics.json").write_text('{"map50": 0.5, "map50_95": 0.3, "per_class": {}}')
        (out / "model.onnx").write_bytes(b"onnx")
        log.write_text("log")
        return self.code


class TrainingRuns(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.vision = VisionService(LocalStore(Path(self.tmp.name) / "s"), FakeMedia())
        self.vision.create_project("demo", ["leak"])
        path = Path(self.tmp.name) / "in.mp4"
        ids = []
        for blob in (b"a", b"b"):
            path.write_bytes(blob)
            ids.append(self.vision.import_video("demo", path, {"kind": "upload"}))
        for video in ids:
            self.vision.extract("demo", video, 2)
            self.vision.put_labels("demo", video, "000001", True, [{"cls": "leak", "x": 1, "y": 1, "w": 5, "h": 5}])
        self.vision.freeze_dataset("demo", 0.5, seed=1)

    def test_run_materializes_dataset_runs_image_and_records_outputs(self):
        compute = FakeCompute()
        service = TrainingService(self.vision, TrainerRegistry(), compute)
        run = service.start("demo", "v001", "yolox", "tiny", 5)
        service.execute(run)
        done = service.get("demo", run["id"])
        self.assertEqual((done["status"], done["metrics"]["map50"]), ("Completed", 0.5))
        image, args, files = compute.calls[0]
        self.assertEqual((image, args), ("wan-trainer-yolox:dev", ["all", "--preset", "tiny", "--epochs", "5"]))
        self.assertEqual(files, ["classes.json", "images", "train.json", "val.json"])
        self.assertTrue(self.vision.store.exists(f"vision/projects/demo/runs/{run['id']}/out/model.onnx"))
        self.assertEqual(len(service.list("demo")), 1)

    def test_failure_is_recorded_and_lock_released(self):
        service = TrainingService(self.vision, TrainerRegistry(), FakeCompute(code=3))
        run = service.start("demo", "v001", "yolox", None, None)
        service.execute(run)
        self.assertEqual(service.get("demo", run["id"])["status"], "Failed")
        service.start("demo", "v001", "yolox", None, None)  # lock was released
        with self.assertRaises(Exception):
            service.start("demo", "v001", "yolox", None, None)  # busy now


if __name__ == "__main__":
    unittest.main()
