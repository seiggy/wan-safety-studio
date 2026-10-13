"""YOLOX trainer (Apache-2.0). Runs inside the trainer image; the YOLOX repo is checked out at $YOLOX_HOME."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

from vision.trainers.sdk import Metrics, OnnxModel, TrainerInfo, TrainRequest, run_cli
from vision.trainers.yolox.deepstream import render_deepstream
from vision.trainers.yolox.exp import render_exp
from vision.trainers.yolox.layout import to_yolox_layout
from vision.trainers.yolox.parse import parse_coco_summary
from vision.trainers.yolox.presets import PRESETS

YOLOX_HOME = Path(os.environ.get("YOLOX_HOME", "/opt/YOLOX"))
WEIGHTS = Path(os.environ.get("YOLOX_WEIGHTS", "/weights"))


def _run(args, log=None):
    process = subprocess.run([sys.executable, *args], cwd=YOLOX_HOME, capture_output=True, text=True, check=False)
    output = process.stdout + process.stderr
    print(output)
    if process.returncode:
        raise RuntimeError(f"{args[0]} failed ({process.returncode}).")
    return output


class YoloxTrainer:
    info = TrainerInfo("yolox", "0.1.0", "Apache-2.0", "Apache-2.0", tuple(PRESETS), "nano")

    def __init__(self):
        self.work = Path(tempfile.mkdtemp(prefix="yolox-"))

    def _exp(self, request: TrainRequest) -> Path:
        preset = PRESETS[request.preset]
        root = to_yolox_layout(request.data, self.work / "coco")
        path = self.work / "exp.py"
        path.write_text(render_exp(preset, len(request.classes), str(root), str(self.work / "runs"),
                                   request.epochs or preset.epochs))
        return path

    def train(self, request):
        preset = PRESETS[request.preset]
        exp = self._exp(request)
        ckpt = request.resume or WEIGHTS / preset.weights
        args = ["tools/train.py", "-f", str(exp), "-d", "1", "-b", str(preset.batch), "--fp16", "-o", "-c", str(ckpt)]
        if request.resume:
            args.append("--resume")
        _run(args)
        best = self.work / "runs" / "wan-studio" / "best_ckpt.pth"
        target = request.out / "best.pth"
        shutil.copyfile(best, target)
        return target

    def evaluate(self, weights, request):
        output = _run(["tools/eval.py", "-f", str(self._exp(request)), "-c", str(weights), "-b", "16", "-d", "1", "--fp16", "--fuse"])
        ap50, ap, per_class = parse_coco_summary(output, request.classes)
        return Metrics(ap50, ap, per_class)

    def export(self, weights, out, request):
        onnx = out / "model.onnx"
        _run(["tools/export_onnx.py", "--output-name", str(onnx), "-f", str(self._exp(request)), "-c", str(weights), "--opset", "11", "--batch-size", "1"])
        size = PRESETS[request.preset].input_size
        return OnnxModel(onnx, (size, size))

    def deepstream(self, model, classes):
        return render_deepstream(model, classes)


def main():
    raise SystemExit(run_cli(YoloxTrainer()))
