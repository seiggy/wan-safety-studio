"""Trainer manifests: trainer.json files found beside this package (and in WAN_STUDIO_TRAINERS_DIR, for customer trainers)."""
from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
import re

from vision.domain import ValidationError

NAME = re.compile(r"[a-z0-9][a-z0-9-]{1,40}")
BUILTIN = Path(__file__).parent


@dataclass(frozen=True)
class TrainerManifest:
    name: str
    version: str
    image: str
    license_code: str
    license_weights: str
    presets: tuple[str, ...]
    default_preset: str

    def to_json(self):
        return {"name": self.name, "version": self.version, "image": self.image, "licenseCode": self.license_code,
                "licenseWeights": self.license_weights, "presets": list(self.presets), "defaultPreset": self.default_preset}


def load_manifest(path: Path) -> TrainerManifest:
    raw = json.loads(path.read_text(encoding="utf-8"))
    try:
        manifest = TrainerManifest(raw["name"], raw["version"], raw["image"], raw["licenseCode"], raw["licenseWeights"],
                                   tuple(raw["presets"]), raw["defaultPreset"])
    except KeyError as missing:
        raise ValidationError(f"{path}: missing {missing}") from None
    if not NAME.fullmatch(manifest.name) or manifest.default_preset not in manifest.presets or not manifest.presets:
        raise ValidationError(f"{path}: invalid name or presets.")
    return manifest


class TrainerRegistry:
    def __init__(self, roots: list[Path] | None = None):
        extra = os.environ.get("WAN_STUDIO_TRAINERS_DIR")
        self.roots = roots if roots is not None else [BUILTIN] + ([Path(extra)] if extra else [])

    def all(self) -> dict[str, TrainerManifest]:
        found = {}
        for root in self.roots:
            for path in sorted(root.glob("*/trainer.json")):
                if path.parent.name.startswith("_"):
                    continue  # templates
                manifest = load_manifest(path)
                found[manifest.name] = manifest
        return found

    def get(self, name: str) -> TrainerManifest:
        try:
            return self.all()[name]
        except KeyError:
            raise ValidationError(f"Unknown trainer {name!r}.") from None
