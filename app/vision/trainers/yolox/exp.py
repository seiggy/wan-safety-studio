"""Render the per-run YOLOX Exp file (the documented way to customise YOLOX: subclass the stock Exp, override settings)."""
from vision.trainers.yolox.presets import Preset


def render_exp(preset: Preset, num_classes: int, data_dir: str, output_dir: str, epochs: int) -> str:
    warmup = min(5, max(1, epochs // 10))
    no_aug = min(15, max(1, epochs // 5))
    return f'''from yolox.exp import get_exp

_Base = type(get_exp(None, {preset.exp_name!r}))


class Exp(_Base):
    def __init__(self):
        super().__init__()
        self.num_classes = {num_classes}
        self.data_dir = {data_dir!r}
        self.train_ann = "instances_train2017.json"
        self.val_ann = "instances_val2017.json"
        self.test_ann = "instances_val2017.json"
        self.output_dir = {output_dir!r}
        self.exp_name = "wan-studio"
        self.max_epoch = {epochs}
        self.warmup_epochs = {warmup}
        self.no_aug_epochs = {no_aug}
        self.eval_interval = 1
        self.print_interval = 10
        self.data_num_workers = 4
        self.save_history_ckpt = False
'''
