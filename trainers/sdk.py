"""Trainer SDK: the whole contract a model trainer implements. Standard library only, so a trainer image can copy this one file.

A trainer image runs `python -m trainer <command>`; `run_cli` dispatches commands to a `Trainer` implementation and writes
JSON results beside the outputs. The portal never imports a trainer; it only runs the image (see ports.ComputeTarget).

Dataset layout mounted at --data (read-only), produced by the portal:
  classes.json  ["leak", "fire", ...]          images/<name>.jpg
  train.json / val.json   COCO; "file_name" is relative to --data (e.g. "images/v1_000001.jpg"); category ids are 1-based
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass, field
import json
from pathlib import Path
import sys
from typing import Protocol


@dataclass(frozen=True)
class TrainerInfo:
    name: str
    version: str
    license_code: str
    license_weights: str
    presets: tuple[str, ...]
    default_preset: str


@dataclass(frozen=True)
class TrainRequest:
    data: Path
    out: Path
    preset: str
    classes: tuple[str, ...]
    epochs: int | None = None
    resume: Path | None = None


@dataclass(frozen=True)
class Metrics:
    map50: float
    map50_95: float
    per_class: dict[str, float] = field(default_factory=dict)


@dataclass(frozen=True)
class OnnxModel:
    path: Path
    input_size: tuple[int, int]  # (width, height)
    input_name: str = "images"
    output_names: tuple[str, ...] = ("output",)


@dataclass(frozen=True)
class DeepStreamConfig:
    """Files to place beside model.onnx in the edge bundle (name -> text), plus notes for the operator."""
    files: dict[str, str]
    notes: tuple[str, ...] = ()


class Trainer(Protocol):
    info: TrainerInfo

    def train(self, request: TrainRequest) -> Path:
        """Train or resume; return the best weights file under request.out."""

    def evaluate(self, weights: Path, request: TrainRequest) -> Metrics:
        """Score the weights on val.json."""

    def export(self, weights: Path, out: Path, request: TrainRequest) -> OnnxModel:
        """Write model.onnx (and anything it needs) under out. request supplies the preset and data layout, so export runs standalone."""

    def deepstream(self, model: OnnxModel, classes: tuple[str, ...]) -> DeepStreamConfig:
        """nvinfer config, labels and any parser notes for DeepStream on Jetson."""


COMMANDS = ("info", "train", "evaluate", "export", "deepstream", "all")


def _json(value):
    return json.dumps(value, indent=1, default=str)


def _load_model(path: Path) -> OnnxModel:
    raw = json.loads(path.read_text())
    return OnnxModel(Path(raw["path"]), tuple(raw["input_size"]), raw.get("input_name", "images"), tuple(raw.get("output_names", ("output",))))


def run_cli(trainer: Trainer, argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="trainer")
    parser.add_argument("command", choices=COMMANDS)
    parser.add_argument("--data", type=Path, default=Path("/data"))
    parser.add_argument("--out", type=Path, default=Path("/out"))
    parser.add_argument("--preset")
    parser.add_argument("--epochs", type=int)
    parser.add_argument("--resume", type=Path)
    parser.add_argument("--weights", type=Path, help="checkpoint for evaluate/export (default: the best.* file under --out)")
    args = parser.parse_args(argv)
    if args.command == "info":
        print(_json(asdict(trainer.info)))
        return 0
    preset = args.preset or trainer.info.default_preset
    if preset not in trainer.info.presets:
        parser.error(f"preset must be one of {trainer.info.presets}")
    classes = tuple(json.loads((args.data / "classes.json").read_text()))
    args.out.mkdir(parents=True, exist_ok=True)
    request = TrainRequest(args.data, args.out, preset, classes, args.epochs, args.resume)
    steps = ("train", "evaluate", "export", "deepstream") if args.command == "all" else (args.command,)
    weights = args.weights
    if weights is None and steps[0] in ("evaluate", "export"):
        weights = next(iter(sorted(args.out.glob("best.*"))), None)
        if weights is None:
            parser.error(f"{args.command} needs trained weights: pass --weights or put best.* under {args.out}")
    if steps[0] == "deepstream" and not (args.out / "model.json").exists():
        parser.error("deepstream needs the model.json that export writes under --out; run export first")
    model = None
    for step in steps:
        if step == "train":
            weights = trainer.train(request)
        elif step == "evaluate":
            (args.out / "metrics.json").write_text(_json(asdict(trainer.evaluate(weights, request))))
        elif step == "export":
            model = trainer.export(weights, args.out, request)
            (args.out / "model.json").write_text(_json(asdict(model)))
        elif step == "deepstream":
            model = model or _load_model(args.out / "model.json")
            config = trainer.deepstream(model, classes)
            target = args.out / "deepstream"
            target.mkdir(exist_ok=True)
            for name, text in config.files.items():
                (target / name).write_text(text)
            (target / "NOTES.txt").write_text("\n".join(config.notes))
    return 0


if __name__ == "__main__":
    sys.exit("Import run_cli from your trainer package; this module has no trainer.")
