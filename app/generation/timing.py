"""Per-stage timing of a ComfyUI prompt from its websocket events. Pure logic: feed messages, read a summary."""
from __future__ import annotations

from dataclasses import dataclass, field

LOADERS = {"UNETLoader", "UnetLoaderGGUF", "CLIPLoader", "VAELoader", "CheckpointLoaderSimple", "LoraLoader"}
SAVERS = {"SaveWEBM", "SaveVideo", "SaveAnimatedWEBP", "VHS_VideoCombine", "SaveImage"}


def stage_of(workflow: dict, node_id: str) -> str:
    node = workflow.get(node_id, {})
    kind = node.get("class_type", "")
    if kind in LOADERS:
        return "load_models"
    if kind == "CLIPTextEncode":
        return "text_encode"
    if kind in ("LoadImage", "WanImageToVideo"):
        return "image_to_latent"
    if kind == "ModelSamplingSD3":
        return "model_setup"
    if kind.startswith("KSampler"):
        # Wan 2.2 A14B: the first sampler adds noise (high-noise expert), the second finishes with the low-noise expert.
        return "sample_low_noise" if node.get("inputs", {}).get("add_noise") == "disable" else "sample_high_noise"
    if kind == "VAEDecode":
        return "vae_decode"
    if kind in SAVERS:
        return "save_video"
    return "other"


@dataclass
class NodeRecord:
    start: float
    end: float | None = None
    steps: int = 0
    max_steps: int = 0


@dataclass
class StageTimer:
    workflow: dict
    records: dict[str, NodeRecord] = field(default_factory=dict)
    cached: set[str] = field(default_factory=set)
    started: float | None = None
    finished: float | None = None
    current: str | None = None
    outcome: str | None = None

    def feed(self, message: dict, now: float) -> None:
        kind, data = message.get("type"), message.get("data") or {}
        if kind == "execution_start":
            self.started = now
        elif kind == "execution_cached":
            self.cached.update(str(n) for n in data.get("nodes", []))
        elif kind == "executing":
            self._close(now)
            node = data.get("node")
            if node is not None:
                self.current = str(node)
                self.records[self.current] = NodeRecord(now)
            elif self.started is not None:  # older servers: node=None means the prompt finished
                self.finish(now, "success")
        elif kind == "progress":
            record = self.records.get(str(data.get("node") or self.current))
            if record:
                record.steps, record.max_steps = int(data.get("value", 0)), int(data.get("max", 0))
        elif kind == "execution_success":
            self.finish(now, "success")
        elif kind in ("execution_error", "execution_interrupted"):
            self.finish(now, "error")

    def finish(self, now: float, outcome: str) -> None:
        self._close(now)
        if self.finished is None:
            self.finished, self.outcome = now, outcome

    def _close(self, now: float) -> None:
        if self.current is not None and self.current in self.records and self.records[self.current].end is None:
            self.records[self.current].end = now
        self.current = None

    @property
    def done(self) -> bool:
        return self.finished is not None

    def intervals(self):
        """(stage, node_id, start, end) for every executed node, in order."""
        return [(stage_of(self.workflow, node), node, r.start, r.end if r.end is not None else r.start)
                for node, r in self.records.items() for stage in [stage_of(self.workflow, node)]]

    def summary(self) -> dict:
        stages: dict[str, dict] = {}
        for stage, node, start, end in self.intervals():
            entry = stages.setdefault(stage, {"seconds": 0.0, "nodes": [], "steps": 0, "cached": False})
            entry["seconds"] += end - start
            entry["nodes"].append(node)
            entry["steps"] += self.records[node].steps
        for node in self.cached:
            if node not in self.records:
                stages.setdefault(stage_of(self.workflow, node), {"seconds": 0.0, "nodes": [], "steps": 0, "cached": True})["nodes"].append(node)
        for entry in stages.values():
            entry["seconds"] = round(entry["seconds"], 3)
            entry["secondsPerStep"] = round(entry["seconds"] / entry["steps"], 3) if entry["steps"] else None
        total = round(self.finished - self.started, 3) if self.started is not None and self.finished is not None else None
        return {"outcome": self.outcome, "executionSeconds": total, "stages": stages}
