"""Parsers for YOLOX's pycocotools text output."""
import re

AP = re.compile(r"Average Precision\s+\(AP\) @\[ IoU=(0\.50:0\.95|0\.50)\s*\| area=\s*all \| maxDets=100 \] = ([0-9.]+)")
ROW = re.compile(r"^\|\s*([^|]+?)\s*\|\s*([0-9.]+)\s*\|", re.M)


def parse_coco_summary(text: str, classes) -> tuple[float, float, dict[str, float]]:
    """Returns (AP50, AP50:95, per-class AP) as fractions 0-1. Per-class comes from YOLOX's 'per class AP' table (percent)."""
    found = {iou: float(value) for iou, value in AP.findall(text)}
    if "0.50:0.95" not in found or "0.50" not in found:
        raise ValueError("No COCO AP summary found in the evaluation output.")
    per_class = {}
    if "per class AP" in text:
        table = text.split("per class AP", 1)[1]
        per_class = {name: float(ap) / 100 for name, ap in ROW.findall(table) if name in set(classes)}
    return found["0.50"], found["0.50:0.95"], per_class
