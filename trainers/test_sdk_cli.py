"""Standalone-command checks for sdk.run_cli. Run from the repo root: python -m unittest discover -s trainers -p "test_sdk_cli.py"."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parent))
from sdk import DeepStreamConfig, Metrics, OnnxModel, TrainerInfo, run_cli  # noqa: E402


class Fake:
    info = TrainerInfo("fake", "0", "MIT", "MIT", ("a", "b"), "a")

    def __init__(self):
        self.exported = None

    def train(self, request):
        target = request.out / "best.pth"
        target.write_text("w")
        return target

    def evaluate(self, weights, request):
        return Metrics(0.5, 0.3)

    def export(self, weights, out, request):
        self.exported = (weights.name, request.preset)
        (out / "model.onnx").write_text("o")
        return OnnxModel(out / "model.onnx", (320, 320))

    def deepstream(self, model, classes):
        return DeepStreamConfig({"cfg.txt": str(model.input_size)})


class StandaloneCommands(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        root = Path(self.dir.name)
        self.data, self.out = root / "data", root / "out"
        self.data.mkdir()
        (self.data / "classes.json").write_text(json.dumps(["leak"]))
        self.base = ["--data", str(self.data), "--out", str(self.out)]

    def tearDown(self):
        self.dir.cleanup()

    def test_export_then_deepstream_work_standalone(self):
        fake = Fake()
        self.out.mkdir()
        (self.out / "best.pth").write_text("w")
        self.assertEqual(run_cli(fake, ["export", "--preset", "b", *self.base]), 0)
        self.assertEqual(fake.exported, ("best.pth", "b"))
        self.assertEqual(run_cli(fake, ["deepstream", *self.base]), 0)
        self.assertEqual((self.out / "deepstream" / "cfg.txt").read_text(), "(320, 320)")

    def test_train_output_is_found_by_evaluate(self):
        fake = Fake()
        self.assertEqual(run_cli(fake, ["train", *self.base]), 0)
        self.assertEqual(run_cli(fake, ["evaluate", *self.base]), 0)
        self.assertTrue((self.out / "metrics.json").exists())

    def test_missing_prerequisites_fail_with_a_clear_message(self):
        for command, text in (("export", "needs trained weights"), ("deepstream", "run export first")):
            err = io.StringIO()
            with contextlib.redirect_stderr(err), self.assertRaises(SystemExit):
                run_cli(Fake(), [command, *self.base])
            self.assertIn(text, err.getvalue())


if __name__ == "__main__":
    unittest.main()
