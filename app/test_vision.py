"""Run with the locked project Python: python -m unittest test_vision. Media tooling is faked."""
import json
from pathlib import Path
import tempfile
import unittest

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer

from vision.adapters.storage import LocalStore
from vision import routes
from vision.domain import ValidationError
from vision.service import Conflict, NotFound, VisionService


class FakeMedia:
    def probe(self, video):
        return {"width": 100, "height": 50, "duration": 2.0, "fps": 10.0}

    def extract_frames(self, video, out, fps, limit):
        out.mkdir(parents=True)
        frames = [out / f"{i:06d}.jpg" for i in range(1, 4)]
        for frame in frames:
            frame.write_bytes(b"jpg")
        return frames


class VisionChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.service = VisionService(LocalStore(self.root / "store"), FakeMedia())

    def video(self, content):
        path = self.root / "in.mp4"
        path.write_bytes(content)
        return self.service.import_video("demo", path, {"kind": "upload"})

    def prepare(self, contents=(b"a", b"b")):
        self.service.create_project("demo", ["leak", "fire"])
        ids = [self.video(c) for c in contents]
        for v in ids:
            self.service.extract("demo", v, 2)
        return ids

    def test_project_rules(self):
        self.service.create_project("demo", ["leak"])
        with self.assertRaises(Conflict):
            self.service.create_project("demo", ["leak"])
        for name, classes in (("Bad Name", ["a1"]), ("ok-name", []), ("ok-name", ["dup", "dup"])):
            with self.assertRaises(ValidationError):
                self.service.create_project(name, classes)
        self.assertEqual(self.service.list_projects(), ["demo"])

    def test_import_is_idempotent_and_extract_once(self):
        a, b = self.prepare()
        self.assertEqual(self.video(b"a"), a)
        self.assertEqual({v["id"]: v["frames"] for v in self.service.list_videos("demo")}, {a: 3, b: 3})
        with self.assertRaises(Conflict):
            self.service.extract("demo", a, 2)

    def test_gallery_import_is_restricted(self):
        self.service.create_project("demo", ["leak"])
        with self.assertRaises(ValidationError):
            self.service.import_gallery_video("demo", "vision/projects/other/sources/x/video.mp4")

    def test_label_validation(self):
        a, _ = self.prepare()
        ok = {"cls": "leak", "x": 10, "y": 5, "w": 20, "h": 10}
        self.service.put_labels("demo", a, "000001", True, [ok])
        for bad in ({**ok, "cls": "nope"}, {**ok, "w": 0}, {**ok, "x": 95}, {"cls": "leak"}):
            with self.assertRaises(ValidationError):
                self.service.put_labels("demo", a, "000001", True, [bad])
        with self.assertRaises(NotFound):
            self.service.put_labels("demo", a, "000009", True, [])

    def test_freeze_splits_by_video_and_is_immutable_versioned(self):
        a, b = self.prepare()
        for v in (a, b):
            for f in ("000001", "000002"):
                self.service.put_labels("demo", v, f, True, [{"cls": "fire", "x": 1, "y": 1, "w": 5, "h": 5}])
            self.service.put_labels("demo", v, "000003", False, [])
        m1 = self.service.freeze_dataset("demo", 0.5, seed=1)
        self.assertEqual(sorted(m1["videos"]["train"] + m1["videos"]["val"]), sorted([a, b]))
        self.assertEqual(m1["counts"]["train"], {"images": 2, "boxes": 2})
        self.assertIn("No training boxes for class 'leak'.", m1["warnings"])
        self.assertEqual(self.service.freeze_dataset("demo", 0.5, seed=1)["version"], "v002")
        coco = json.loads(self.service.store.read(f"vision/projects/demo/datasets/v001/val.json"))
        self.assertEqual(coco["annotations"][0]["category_id"], 2)

    def test_freeze_needs_two_videos_for_a_split(self):
        self.prepare((b"a",))
        with self.assertRaises(ValidationError):
            self.service.freeze_dataset("demo", 0.2)


class RouteChecks(unittest.IsolatedAsyncioTestCase):
    async def test_error_mapping_and_flow(self):
        with tempfile.TemporaryDirectory() as tmp:
            app = web.Application()
            routes.register(app, VisionService(LocalStore(Path(tmp)), FakeMedia()))
            async with TestClient(TestServer(app)) as client:
                made = await client.post("/api/vision/projects", json={"name": "demo", "classes": ["leak"]})
                self.assertEqual(made.status, 201)
                self.assertEqual((await client.post("/api/vision/projects", json={"name": "demo", "classes": ["leak"]})).status, 409)
                self.assertEqual((await client.post("/api/vision/projects", json={"name": "BAD", "classes": ["leak"]})).status, 400)
                self.assertEqual((await client.get("/api/vision/projects/missing")).status, 404)
                upload = await client.post("/api/vision/projects/demo/videos", data={"file": b"abc"})
                self.assertEqual(upload.status, 201)
                video = (await upload.json())["id"]
                self.assertEqual((await client.post(f"/api/vision/projects/demo/videos/{video}/extract", json={"fps": 2})).status, 201)
                put = await client.put(f"/api/vision/projects/demo/labels/{video}/000001",
                                       json={"reviewed": True, "boxes": [{"cls": "leak", "x": 1, "y": 1, "w": 5, "h": 5}]})
                self.assertEqual(put.status, 200)
                self.assertEqual((await client.get(f"/api/vision/projects/demo/frames/{video}/000001.jpg")).status, 200)


if __name__ == "__main__":
    unittest.main()
