"""Use cases for vision projects. Depends only on the domain and the Store/MediaTools ports."""
from __future__ import annotations

from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import tempfile

from vision.domain import FRAME, Box, FrameLabels, Project, ValidationError, check_name
from vision.ports import MediaTools, Store

ROOT = "vision/projects/"
GALLERY = "video-library/"
MAX_FRAMES = 2000


class NotFound(LookupError):
    pass


class Conflict(RuntimeError):
    pass


def _json(value):
    return json.dumps(value, indent=1, sort_keys=True).encode()


class VisionService:
    def __init__(self, store: Store, media: MediaTools):
        self.store, self.media = store, media

    def _key(self, project, *parts):
        return ROOT + "/".join((check_name(project, "project"), *parts))

    def _doc(self, key, what):
        data = self.store.read(key)
        if data is None:
            raise NotFound(f"{what} not found.")
        return json.loads(data)

    # projects
    def create_project(self, name, classes):
        project = Project.create(name, classes)
        try:
            self.store.write(self._key(name, "project.json"), _json(project.to_json()), overwrite=False)
        except FileExistsError:
            raise Conflict("A project with this name already exists.") from None
        return project

    def get_project(self, name):
        value = self._doc(self._key(name, "project.json"), "Project")
        return Project(value["name"], tuple(value["classes"]))

    def list_projects(self):
        names = {key.split("/")[2] for key in self.store.list(ROOT.rstrip("/"))}
        return sorted(names)

    # sources
    def _video_id(self, data_or_path):
        digest = hashlib.sha256()
        with open(data_or_path, "rb") as handle:
            for chunk in iter(lambda: handle.read(1 << 20), b""):
                digest.update(chunk)
        return "v" + digest.hexdigest()[:12]

    def import_video(self, project, path: Path, origin: dict):
        """Probe, content-address and store a video. Re-importing the same bytes is idempotent."""
        self.get_project(project)
        info = self.media.probe(path)
        video = self._video_id(path)
        key = self._key(project, "sources", video, "video.mp4")
        if not self.store.exists(key):
            self.store.upload(key, path)
        self.store.write(self._key(project, "sources", video, "source.json"),
                         _json({"id": video, "origin": origin, **info}))
        return video

    def import_gallery_video(self, project, blob):
        if not blob.startswith(GALLERY) or not blob.endswith((".mp4", ".webm")):
            raise ValidationError("Only generated .mp4/.webm files from the video library can be imported.")
        with tempfile.TemporaryDirectory(prefix="vision-import-") as tmp:
            path = Path(tmp) / "video.mp4"
            if not self.store.exists(blob):
                raise NotFound("Generated video not found.")
            self.store.download(blob, path)
            return self.import_video(project, path, {"kind": "wan", "blob": blob})

    def list_videos(self, project):
        self.get_project(project)
        out = []
        for key in self.store.list(self._key(project, "sources")):
            if key.endswith("/source.json"):
                source = json.loads(self.store.read(key))
                index = self.store.read(self._key(project, "frames", source["id"], "index.json"))
                source["frames"] = len(json.loads(index)["frames"]) if index else 0
                out.append(source)
        return out

    # frames
    def extract(self, project, video, fps):
        self.get_project(project)
        if not 0 < fps <= 30:
            raise ValidationError("fps must be above 0 and at most 30.")
        source = self._doc(self._key(project, "sources", video, "source.json"), "Video")
        if self.store.exists(self._key(project, "frames", video, "index.json")):
            raise Conflict("Frames were already extracted for this video; labels depend on them.")
        with tempfile.TemporaryDirectory(prefix="vision-extract-") as tmp:
            local = Path(tmp) / "video.mp4"
            self.store.download(self._key(project, "sources", video, "video.mp4"), local)
            frames = self.media.extract_frames(local, Path(tmp) / "frames", fps, MAX_FRAMES)
            for frame in frames:
                self.store.upload(self._key(project, "frames", video, frame.name), frame, overwrite=True)
        index = {"width": source["width"], "height": source["height"], "fps": fps, "frames": [f.stem for f in frames]}
        self.store.write(self._key(project, "frames", video, "index.json"), _json(index), overwrite=False)
        return index

    def frame_index(self, project, video):
        return self._doc(self._key(project, "frames", video, "index.json"), "Frames")

    def frame_image(self, project, video, frame):
        if not FRAME.fullmatch(frame):
            raise ValidationError("Invalid frame id.")
        data = self.store.read(self._key(project, "frames", video, frame + ".jpg"))
        if data is None:
            raise NotFound("Frame not found.")
        return data

    # labels
    def get_labels(self, project, video, frame):
        if not FRAME.fullmatch(frame):
            raise ValidationError("Invalid frame id.")
        data = self.store.read(self._key(project, "labels", video, frame + ".json"))
        return FrameLabels.from_json(json.loads(data)) if data else FrameLabels()

    def put_labels(self, project, video, frame, reviewed, boxes):
        classes = self.get_project(project).classes
        index = self.frame_index(project, video)
        if frame not in index["frames"]:
            raise NotFound("Frame not found.")
        try:
            checked = [Box(**box).checked(classes, index["width"], index["height"]) for box in boxes]
        except TypeError:
            raise ValidationError("Boxes need cls, x, y, w, h.") from None
        labels = FrameLabels(bool(reviewed), checked)
        self.store.write(self._key(project, "labels", video, frame + ".json"), _json(labels.to_json()))
        return labels

    # datasets
    def freeze_dataset(self, project, val_fraction=0.2, seed=0):
        """Immutable COCO dataset version, split by source video so train and val never share a scene."""
        classes = self.get_project(project).classes
        videos = sorted(v["id"] for v in self.list_videos(project) if v["frames"])
        if not 0 <= val_fraction < 1:
            raise ValidationError("Validation fraction must be in [0, 1).")
        val_count = math.ceil(len(videos) * val_fraction) if val_fraction else 0
        if val_count and len(videos) < 2:
            raise ValidationError("Splitting needs at least two videos; import another or use a validation fraction of 0.")
        rank = sorted(videos, key=lambda v: hashlib.sha256(f"{seed}:{v}".encode()).hexdigest())
        val = set(rank[:val_count])
        splits = {"train": self._coco(project, [v for v in videos if v not in val], classes),
                  "val": self._coco(project, sorted(val), classes)}
        if not splits["train"]["images"]:
            raise ValidationError("No reviewed frames in the training split. Review labels first.")
        existing = [k for k in self.store.list(self._key(project, "datasets")) if k.endswith("/manifest.json")]
        version = f"v{len(existing) + 1:03d}"
        base = self._key(project, "datasets", version)
        for name, coco in splits.items():
            self.store.write(f"{base}/{name}.json", _json(coco), overwrite=False)
        counts = {name: {"images": len(c["images"]), "boxes": len(c["annotations"])} for name, c in splits.items()}
        used = {c["name"]: sum(a["category_id"] == c["id"] for a in splits["train"]["annotations"]) for c in splits["train"]["categories"]}
        warnings = [f"No training boxes for class {name!r}." for name, n in used.items() if n == 0]
        manifest = {"version": version, "classes": list(classes), "seed": seed, "valFraction": val_fraction,
                    "videos": {"train": [v for v in videos if v not in val], "val": sorted(val)},
                    "counts": counts, "warnings": warnings, "createdAt": datetime.now(timezone.utc).isoformat()}
        self.store.write(f"{base}/manifest.json", _json(manifest), overwrite=False)
        return manifest

    def list_datasets(self, project):
        return [json.loads(self.store.read(k)) for k in self.store.list(self._key(project, "datasets"))
                if k.endswith("/manifest.json")]

    def _coco(self, project, videos, classes):
        images, annotations = [], []
        for video in videos:
            index = self.frame_index(project, video)
            for frame in index["frames"]:
                labels = self.get_labels(project, video, frame)
                if not labels.reviewed:
                    continue
                image_id = len(images) + 1
                images.append({"id": image_id, "file_name": f"frames/{video}/{frame}.jpg",
                               "width": index["width"], "height": index["height"], "video": video})
                for box in labels.boxes:
                    annotations.append({"id": len(annotations) + 1, "image_id": image_id,
                                        "category_id": classes.index(box.cls) + 1,
                                        "bbox": [box.x, box.y, box.w, box.h], "area": box.w * box.h, "iscrowd": 0})
        return {"images": images, "annotations": annotations,
                "categories": [{"id": i + 1, "name": c} for i, c in enumerate(classes)]}
