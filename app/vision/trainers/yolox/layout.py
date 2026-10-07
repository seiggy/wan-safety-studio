"""Shared dataset layout -> the layout YOLOX's COCODataset reads (<root>/annotations/instances_<split>2017.json, <root>/<split>2017/)."""
import json
from pathlib import Path
import shutil


def to_yolox_layout(data: Path, root: Path):
    (root / "annotations").mkdir(parents=True, exist_ok=True)
    for split in ("train", "val"):
        coco = json.loads((data / f"{split}.json").read_text())
        images = root / f"{split}2017"
        images.mkdir(exist_ok=True)
        for image in coco["images"]:
            name = Path(image["file_name"]).name
            shutil.copyfile(data / image["file_name"], images / name)
            image["file_name"] = name
        (root / "annotations" / f"instances_{split}2017.json").write_text(json.dumps(coco))
    return root
