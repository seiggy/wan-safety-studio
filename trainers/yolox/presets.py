"""Model presets. Sizes and widths follow the YOLOX model zoo: https://yolox.readthedocs.io/en/latest/model_zoo.html"""
from dataclasses import dataclass

RELEASE = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0"


@dataclass(frozen=True)
class Preset:
    exp_name: str  # yolox.exp.get_exp name
    input_size: int
    weights: str   # file name baked into the image under /weights
    batch: int
    epochs: int


PRESETS = {
    "nano": Preset("yolox-nano", 416, "yolox_nano.pth", 32, 100),
    "tiny": Preset("yolox-tiny", 416, "yolox_tiny.pth", 32, 100),
    "s": Preset("yolox-s", 640, "yolox_s.pth", 16, 100),
}
