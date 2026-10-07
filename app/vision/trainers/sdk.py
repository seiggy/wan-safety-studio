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

    def export(self, weights: Path, out: Path) -> OnnxModel:
        """Write model.onnx (and anything it needs) under out."""

    def deepstream(self, model: OnnxModel, classes: tuple[str, ...]) -> DeepStreamConfig:
        """nvinfer config, labels and any parser notes for DeepStream on Jetson."""


COMMANDS = ("info", "train", "evaluate", "export", "deepstream", "all")


def _json(value):
    return json.dumps(value, indent=1, default=str)


def run_cli(trainer: Trainer, argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="trainer")
    parser.add_argument("command", choices=COMMANDS)
    parser.add_argument("--data", type=Path, default=Path("/data"))
    parser.add_argument("--out", type=Path, default=Path("/out"))
    parser.add_argument("--preset")
    parser.add_argument("--epochs", type=int)
    parser.add_argument("--resume", type=Path)
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
    weights = args.out / "best.weights"
    steps = ("train", "evaluate", "export", "deepstream") if args.command == "all" else (args.command,)
    model = None
    for step in steps:
        if step == "train":
            weights = trainer.train(request)
        elif step == "evaluate":
            (args.out / "metrics.json").write_text(_json(asdict(trainer.evaluate(weights, request))))
        elif step == "export":
            model = trainer.export(weights, args.out)
            (args.out / "model.json").write_text(_json(asdict(model)))
        elif step == "deepstream":
            model = model or OnnxModel(args.out / "model.onnx", (0, 0))
            config = trainer.deepstream(model, classes)
            target = args.out / "deepstream"
            target.mkdir(exist_ok=True)
            for name, text in config.files.items():
                (target / name).write_text(text)
            (target / "NOTES.txt").write_text("\n".join(config.notes))
    return 0


if __name__ == "__main__":
    sys.exit("Import run_cli from your trainer package; this module has no trainer.")
