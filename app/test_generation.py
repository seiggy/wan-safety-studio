"""Run: python -m unittest test_generation. ComfyUI is a fake aiohttp server; no GPU, Azure or ffmpeg."""
import asyncio
import json
from pathlib import Path
import tempfile
import unittest

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer

from dev_server import create_app
from generation.comfyui import ApiRetrieval, ComfyUiClient, ComfyUiError, ShareRetrieval, find_videos, render_workflow
from generation.jobs import DEFAULT_MODELS, LocalGeneration
from generation.storage import store_from_env
from vision.adapters.storage import LocalStore

TEMPLATE = {"1": {"inputs": {"text": "{{positive_prompt}}", "seed": "{{seed}}", "image": "{{input_image}}",
                             "label": "a-{{width}}x{{height}}"}}}


def fake_comfyui(video=b"MP4DATA"):
    seen = {}

    async def upload(request):
        field = await (await request.multipart()).next()
        seen["upload"] = field.filename
        return web.json_response({"name": field.filename, "subfolder": "", "type": "input"})

    async def prompt(request):
        seen["prompt"] = (await request.json())["prompt"]
        return web.json_response({"prompt_id": "p1"})

    async def history(request):
        return web.json_response({"p1": {"status": {"status_str": "success", "completed": True},
                                         "outputs": {"9": {"images": [{"filename": "out.mp4", "subfolder": "wan", "type": "output"}]}}}})

    async def view(request):
        seen["view"] = dict(request.query)
        return web.Response(body=video)

    async def stats(request):
        return web.json_response({})

    app = web.Application()
    app.router.add_post("/upload/image", upload)
    app.router.add_post("/prompt", prompt)
    app.router.add_get("/history/{id}", history)
    app.router.add_get("/view", view)
    app.router.add_get("/system_stats", stats)
    return app, seen


class Templating(unittest.TestCase):
    def test_placeholders_keep_types_and_reject_unknown(self):
        out = render_workflow(TEMPLATE, {"positive_prompt": "p", "seed": 7, "input_image": "i.png", "width": 8, "height": 4})
        self.assertEqual(out["1"]["inputs"], {"text": "p", "seed": 7, "image": "i.png", "label": "a-8x4"})
        with self.assertRaises(ComfyUiError):
            render_workflow({"x": "{{nope}}"}, {})

    def test_find_videos_across_output_kinds(self):
        entry = {"outputs": {"1": {"gifs": [{"filename": "a.webm"}]}, "2": {"images": [{"filename": "b.png"}]}}}
        self.assertEqual([v["filename"] for v in find_videos(entry)], ["a.webm"])

    def test_store_selection(self):
        with tempfile.TemporaryDirectory() as tmp:
            store, label = store_from_env({"WAN_STUDIO_STORAGE": "local", "WAN_STUDIO_STORAGE_PATH": tmp})
            self.assertIsInstance(store, LocalStore)
            with self.assertRaises(ValueError):
                store_from_env({"WAN_STUDIO_STORAGE": "azurite"})
            with self.assertRaises(ValueError):
                store_from_env({"WAN_STUDIO_STORAGE": "azure"})


class ShippedWorkflows(unittest.TestCase):
    def test_template_renders_to_the_importable_reference_graph(self):
        folder = Path(__file__).parent / "generation" / "workflows"
        reference = json.loads((folder / "wan22_i2v_fp8.api.json").read_text(encoding="utf-8"))
        template = json.loads((folder / "wan22_i2v.api.json").read_text(encoding="utf-8"))
        values = {"positive_prompt": reference["6"]["inputs"]["text"], "negative_prompt": reference["7"]["inputs"]["text"],
                  "input_image": reference["52"]["inputs"]["image"], "seed": reference["57"]["inputs"]["noise_seed"],
                  "width": reference["50"]["inputs"]["width"], "height": reference["50"]["inputs"]["height"],
                  "length": reference["50"]["inputs"]["length"], "fps": reference["47"]["inputs"]["fps"],
                  "filename_prefix": reference["47"]["inputs"]["filename_prefix"], **DEFAULT_MODELS}
        self.assertEqual(render_workflow(template, values), reference)


class Generation(unittest.IsolatedAsyncioTestCase):
    async def run_flow(self, retrieval_for, tmp, after):
        comfy, seen = fake_comfyui()
        template = Path(tmp) / "wf.json"
        template.write_text(json.dumps(TEMPLATE))
        async with TestClient(TestServer(comfy)) as fake:
            client = ComfyUiClient(str(fake.make_url("")), poll_seconds=0.01)
            store = LocalStore(Path(tmp) / "blob")
            generation = LocalGeneration(store, client, retrieval_for(tmp), template)
            async with TestClient(TestServer(create_app(store, "test", generation))) as api:
                form = {"prompt": "leaking valve", "aspect_ratio": "1:1", "duration_seconds": "5", "takes": "1",
                        "input_image_0": ("img.png", b"png")}
                import aiohttp
                data = aiohttp.FormData()
                for key, value in form.items():
                    data.add_field(key, value[1] if isinstance(value, tuple) else value,
                                   **({"filename": value[0]} if isinstance(value, tuple) else {}))
                response = await api.post("/api/submit", data=data)
                self.assertEqual(response.status, 200)
                job = (await response.json())["batch"]["jobs"][0]["name"]
                for _ in range(200):
                    state = await (await api.get(f"/api/jobs/{job}")).json()
                    if state["status"] in ("Completed", "Failed"):
                        break
                    await asyncio.sleep(0.02)
                await after(api, state, seen)

    async def test_api_retrieval_stores_video_and_serves_it_with_range(self):
        with tempfile.TemporaryDirectory() as tmp:
            async def after(api, state, seen):
              self.assertEqual(state["status"], "Completed", state)
              self.assertEqual(seen["prompt"]["1"]["inputs"]["text"], "leaking valve")
              self.assertEqual(seen["view"]["subfolder"], "wan")
              url = (await (await api.get("/api/gallery")).json())["items"][0]["video_url"]
              self.assertEqual(await (await api.get(url)).read(), b"MP4DATA")
              part = await api.get(url, headers={"Range": "bytes=0-2"})
              self.assertEqual((part.status, await part.read()), (206, b"MP4"))
              self.assertEqual((await api.get("/api/media/vision/projects/x")).status, 404)
            await self.run_flow(lambda t: ApiRetrieval(), tmp, after)

    async def test_share_retrieval_reads_mounted_output_and_blocks_escape(self):
        with tempfile.TemporaryDirectory() as tmp:
            shared = Path(tmp) / "comfy-out"
            (shared / "wan").mkdir(parents=True)
            (shared / "wan" / "out.mp4").write_bytes(b"SHARED")
            async def after(api, state, seen):
              self.assertEqual(state["status"], "Completed", state)
              url = (await (await api.get("/api/gallery")).json())["items"][0]["video_url"]
              self.assertEqual(await (await api.get(url)).read(), b"SHARED")
            await self.run_flow(lambda t: ShareRetrieval(shared), tmp, after)
            with self.assertRaises(ComfyUiError):
                await ShareRetrieval(shared).fetch(None, {"filename": "../../x.mp4"})

    async def test_missing_workflow_is_a_clear_conflict(self):
        with tempfile.TemporaryDirectory() as tmp:
            store = LocalStore(Path(tmp))
            generation = LocalGeneration(store, ComfyUiClient("http://127.0.0.1:1"), ApiRetrieval(), Path(tmp) / "none.json")
            async with TestClient(TestServer(create_app(store, "t", generation))) as api:
                import aiohttp
                data = aiohttp.FormData()
                for key, value in {"prompt": "p", "aspect_ratio": "1:1", "duration_seconds": "5", "takes": "1"}.items():
                    data.add_field(key, value)
                data.add_field("input_image_0", b"x", filename="a.png")
                self.assertEqual((await api.post("/api/submit", data=data)).status, 409)


class SmbChecks(unittest.TestCase):
    def test_share_root_and_connect(self):
        from generation.smb import connect_share, share_root
        self.assertEqual(share_root(r"\\UNAS-Pro-8\ComfyUI\output"), r"\\UNAS-Pro-8\ComfyUI")
        with self.assertRaises(ValueError):
            share_root(r"C:\x")
        calls = []
        self.assertEqual(connect_share(r"\\nas\c", "u", "p", lambda *a: calls.append(a) or 0), r"\\nas\c")
        self.assertEqual(calls, [(r"\\nas\c", "u", "p")])
        connect_share(r"\\nas\c", "u", "p", lambda *a: 1219)  # already connected is fine
        with self.assertRaises(OSError):
            connect_share(r"\\nas\c", "u", "bad", lambda *a: 1326)


if __name__ == "__main__":
    unittest.main()
