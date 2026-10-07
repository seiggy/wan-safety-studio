"""Training use cases: materialize a dataset version, run a trainer image on a compute target, record the run."""
from __future__ import annotations

from datetime import datetime, timezone
import json
from pathlib import Path
import secrets
import tempfile
import threading

from vision.domain import ValidationError, check_name
from vision.ports import ComputeTarget
from vision.service import ROOT, Conflict, NotFound, VisionService, _json
from vision.trainers.registry import TrainerRegistry

STATES = ("Queued", "Preparing", "Running", "Uploading", "Completed", "Failed")
MAX_EPOCHS = 1000


class TrainingService:
    def __init__(self, vision: VisionService, registry: TrainerRegistry, compute: ComputeTarget):
        self.vision, self.registry, self.compute = vision, registry, compute
        self.busy = threading.Lock()  # one GPU job at a time, like the shared generation node

    def _key(self, project, run, name):
        return f"{ROOT}{check_name(project, 'project')}/runs/{run}/{name}"

    def trainers(self):
        return [m.to_json() for m in self.registry.all().values()]

    def start(self, project, dataset, trainer, preset, epochs):
        manifest = self.registry.get(trainer)
        preset = preset or manifest.default_preset
        if preset not in manifest.presets:
            raise ValidationError(f"Preset must be one of {list(manifest.presets)}.")
        if epochs is not None and not 1 <= epochs <= MAX_EPOCHS:
            raise ValidationError(f"Epochs must be 1-{MAX_EPOCHS}.")
        if not any(d["version"] == dataset for d in self.vision.list_datasets(project)):
            raise NotFound("Dataset version not found.")
        if not self.busy.acquire(blocking=False):
            raise Conflict("The GPU node is busy with another job. Wait for it to finish.")
        try:
            return self._new_run(project, dataset, trainer, manifest, preset, epochs)
        except BaseException:
            self.busy.release()
            raise

    def _new_run(self, project, dataset, trainer, manifest, preset, epochs):
        run = {"id": "r" + secrets.token_hex(5), "project": project, "dataset": dataset, "trainer": trainer,
               "trainerVersion": manifest.version, "preset": preset, "epochs": epochs, "compute": self.compute.name,
               "status": "Queued", "error": None, "metrics": None, "createdAt": datetime.now(timezone.utc).isoformat()}
        self._save(run)
        return run

    def _save(self, run):
        self.vision.store.write(self._key(run["project"], run["id"], "run.json"), _json(run))

    def get(self, project, run_id):
        data = self.vision.store.read(self._key(project, run_id, "run.json"))
        if data is None:
            raise NotFound("Run not found.")
        return json.loads(data)

    def list(self, project):
        keys = [k for k in self.vision.store.list(f"{ROOT}{check_name(project, 'project')}/runs") if k.endswith("/run.json")]
        return sorted((json.loads(self.vision.store.read(k)) for k in keys), key=lambda r: r["createdAt"], reverse=True)

    def execute(self, run):
        """Blocking; run in a worker thread. Holds the busy lock taken by start()."""
        try:
            manifest = self.registry.get(run["trainer"])
            with tempfile.TemporaryDirectory(prefix="vision-train-") as tmp:
                data, out = Path(tmp) / "data", Path(tmp) / "out"
                out.mkdir(parents=True)
                self._set(run, "Preparing")
                self.materialize(run["project"], run["dataset"], data)
                args = ["all", "--preset", run["preset"]] + (["--epochs", str(run["epochs"])] if run["epochs"] else [])
                self._set(run, "Running")
                log = out / "train.log"
                code = self.compute.run(manifest.image, args, data, out, log)
                self._set(run, "Uploading")
                for path in out.rglob("*"):
                    if path.is_file():
                        self.vision.store.upload(self._key(run["project"], run["id"], "out/" + path.relative_to(out).as_posix()),
                                                 path, overwrite=True)
                if code != 0:
                    raise RuntimeError(f"Trainer exited with code {code}; see out/train.log.")
                metrics = out / "metrics.json"
                run["metrics"] = json.loads(metrics.read_text()) if metrics.is_file() else None
            self._set(run, "Completed")
        except Exception as error:  # noqa: BLE001 - recorded on the run
            run["error"] = str(error)[:500]
            self._set(run, "Failed")
        finally:
            self.busy.release()

    def _set(self, run, status):
        run["status"] = status
        self._save(run)

    def materialize(self, project, dataset, dest: Path):
        """Write the shared dataset layout (see trainers/sdk.py): classes.json, train.json, val.json, images/."""
        classes = self.vision.get_project(project).classes
        base = f"{ROOT}{project}/datasets/{dataset}"
        (dest / "images").mkdir(parents=True)
        for split in ("train", "val"):
            raw = self.vision.store.read(f"{base}/{split}.json")
            if raw is None:
                raise NotFound(f"Dataset {dataset} has no {split} split.")
            coco = json.loads(raw)
            for image in coco["images"]:
                name = f"{image['video']}_{Path(image['file_name']).name}"
                self.vision.store.download(f"{ROOT}{project}/{image['file_name']}", dest / "images" / name)
                image["file_name"] = f"images/{name}"
            (dest / f"{split}.json").write_text(json.dumps(coco))
        (dest / "classes.json").write_text(json.dumps(list(classes)))
