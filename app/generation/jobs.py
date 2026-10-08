"""Local generation jobs on ComfyUI. Same job/batch/gallery contract as the Azure ML portal, so the UI is unchanged."""
from __future__ import annotations

import asyncio
import json
import logging
import math
from pathlib import Path
import secrets
import time

from generation import metrics
from generation.timing import StageTimer
from generation.comfyui import ComfyUiClient, ComfyUiError, Retrieval, find_videos, render_workflow
from vision.ports import Store

LOGGER = logging.getLogger("wan.generation")
LIBRARY = "video-library/"
TERMINAL = {"Completed", "Failed"}
JOBS = "video-library-jobs/"  # job records, outside the library prefix so the gallery ignores them
ASPECTS = {"16:9": (1024, 576), "4:3": (896, 672), "1:1": (768, 768)}
FPS = 16


DEFAULT_MODELS = {"unet_high": "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors",
                  "unet_low": "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors",
                  "vae_name": "wan_2.1_vae.safetensors",
                  "clip_name": "umt5_xxl_fp8_e4m3fn_scaled.safetensors"}


class MissingWorkflow(RuntimeError):
    pass


class LocalGeneration:
    def __init__(self, store: Store, client: ComfyUiClient, retrieval: Retrieval, workflow_path: Path,
                 models: dict[str, str] | None = None):
        self.store, self.client, self.retrieval, self.workflow_path = store, client, retrieval, workflow_path
        self.models = {**DEFAULT_MODELS, **(models or {})}
        self.jobs: dict[str, dict] = {}
        self.batches: dict[str, dict] = {}
        self.queue: asyncio.Queue = asyncio.Queue()
        self.worker: asyncio.Task | None = None
        self._resumed: list[asyncio.Task] = []

    def _save(self, job):
        try:
            self.store.write(f"{JOBS}{job['name']}.json", json.dumps(job).encode())
        except Exception:  # noqa: BLE001 - persistence must never fail a job
            LOGGER.warning("Could not persist job %s", job["name"])

    async def recover(self):
        """After a restart: resume jobs ComfyUI already accepted; mark the rest failed so clients stop waiting."""
        for key in await asyncio.to_thread(self.store.list, JOBS.rstrip("/")):
            data = await asyncio.to_thread(self.store.read, key)
            job = json.loads(data) if data else None
            if not job or job["status"] in TERMINAL:
                if job:
                    self.jobs[job["name"]] = job
                continue
            self.jobs[job["name"]] = job
            if job.get("promptId") and job.get("context"):
                job["status"] = "Running"
                self._resumed.append(asyncio.create_task(self._resume(job)))
            else:
                job.update(status="Failed", error="Interrupted by a restart before ComfyUI accepted it.")
                await asyncio.to_thread(self._save, job)

    async def _resume(self, job):
        try:
            entry = await self.client.wait(job["promptId"])
            await self._finish(job, entry, job["context"], None)
            job["status"] = "Completed"
        except Exception as error:  # noqa: BLE001
            job.update(status="Failed", error=str(error)[:300])
        self._save(job)

    async def _finish(self, job, entry, context, timing=None, phases=None):
        videos = find_videos(entry)
        if not videos:
            raise ComfyUiError("ComfyUI finished but produced no video output.")
        ref = videos[0]
        phases = phases if phases is not None else {}
        began = time.time()
        data = await self.retrieval.fetch(self.client, ref)
        phases["retrieve"] = round(time.time() - began, 3)
        extension = Path(ref["filename"]).suffix.lower()
        base = f"{LIBRARY}{job['name']}/"
        began = time.time()
        await asyncio.to_thread(self.store.write, base + "video" + extension, data)
        phases["store_video"] = round(time.time() - began, 3)
        meta = {"job_name": job["name"], "display_name": job["display_name"], "width": context["width"], "height": context["height"],
                "duration_seconds": context["duration"], "prompt": context["prompt"], "seed": context["seed"],
                "video": "video" + extension, "created": time.time(), "timing": timing() if timing else None}
        await asyncio.to_thread(self.store.write, base + "meta.json", json.dumps(meta).encode())
        return len(data)

    def _template(self):
        if not self.workflow_path.is_file():
            raise MissingWorkflow(
                f"No ComfyUI workflow at {self.workflow_path}. Export your Wan 2.2 I2V workflow with 'Save (API Format)' and "
                "use {{positive_prompt}}, {{negative_prompt}}, {{input_image}}, {{seed}}, {{width}}, {{height}}, {{length}} "
                "and {{filename_prefix}} placeholders (see docs/local-generation.md).")
        return json.loads(self.workflow_path.read_text(encoding="utf-8"))

    def submit(self, plan: list[dict], negative_prompt: str, duration: float, aspect: str):
        """plan items: {scene, take, prompt, image_name, image_bytes}. Jobs run one at a time, like the single GPU node."""
        self._template()
        width, height = ASPECTS[aspect]
        batch = {"id": secrets.token_urlsafe(12), "total": len(plan), "jobs": [], "done": True, "error": None}
        for item in plan:
            name = f"local-{secrets.token_hex(6)}"
            job = {"name": name, "display_name": f"scene {item['scene']} take {item['take']}", "status": "Queued",
                   "studio_url": None, "submittedAt": time.time(), "scene": item["scene"], "take": item["take"]}
            self.jobs[name] = job
            batch["jobs"].append(job)
            self._save(job)
            self.queue.put_nowait((job, item, negative_prompt, duration, width, height))
        self.batches[batch["id"]] = batch
        if self.worker is None or self.worker.done():
            self.worker = asyncio.create_task(self._run())
        return batch

    async def _run(self):
        while not self.queue.empty():
            job, item, negative, duration, width, height = await self.queue.get()
            job["status"] = "Running"
            job["appQueueSeconds"] = round(time.time() - job["submittedAt"], 3)
            self._save(job)
            try:
                await self._generate(job, item, negative, duration, width, height)
                job["status"] = "Completed"
            except Exception as error:  # noqa: BLE001 - one failed job must not stop the queue
                LOGGER.error("Job %s failed: %s", job["name"], error)
                job.update(status="Failed", error=str(error)[:300])
            self._save(job)

    async def _generate(self, job, item, negative, duration, width, height):
        started, phases, seed = time.time(), {}, secrets.randbelow(10**14)
        timer, length, size, ok = None, None, None, False

        async def phase(name, awaitable):
            began = time.time()
            try:
                return await awaitable
            finally:
                phases[name] = round(time.time() - began, 3)

        def queued(prompt_id):
            job["promptId"] = prompt_id
            self._save(job)

        try:
            image = await phase("upload_image", self.client.upload_image(f"{job['name']}-{item['image_name']}", item["image_bytes"]))
            # Wan latent length: 4n+1 frames.
            length = round(duration * FPS / 4) * 4 + 1
            workflow = render_workflow(self._template(), {**self.models,
                "positive_prompt": item["prompt"], "negative_prompt": negative, "input_image": image, "seed": seed,
                "width": width, "height": height, "length": length, "fps": FPS, "filename_prefix": f"wan-studio/{job['name']}"})
            timer = StageTimer(workflow)
            job["context"] = {"prompt": item["prompt"], "width": width, "height": height, "duration": duration, "seed": seed}
            entry = await phase("comfyui", self.client.run(workflow, timer, queued))
            size = await self._finish(job, entry, job["context"],
                                     lambda: self._timing(job, timer, phases, started, length, duration, width, height), phases)
            ok = True
        finally:
            finished = time.time()
            if timer is not None and timer.started is not None:
                job["timing"] = self._timing(job, timer, phases, started, length, duration, width, height, finished, size)
                metrics.emit(job["name"], job["timing"]["params"], timer, phases, started, finished, ok)

    def _timing(self, job, timer, phases, started, length, duration, width, height, finished=None, size=None):
        params = {"width": width, "height": height, "frames": length, "seconds": duration, "provider": "comfyui"}
        return {**timer.summary(), "phases": dict(phases), "totalSeconds": round((finished or time.time()) - started, 3),
                "appQueueSeconds": job.get("appQueueSeconds"), "params": params, "videoBytes": size}
    def gallery(self):
        items = []
        for key in self.store.list(LIBRARY.rstrip("/")):
            if key.endswith("/meta.json"):
                meta = json.loads(self.store.read(key))
                items.append({**{k: meta.get(k) for k in ("job_name", "display_name", "width", "height", "duration_seconds", "prompt")},
                              "video_url": f"/api/media/{LIBRARY}{meta['job_name']}/{meta['video']}",
                              "created": meta.get("created", 0), "timing": meta.get("timing")})
        items.sort(key=lambda item: item["created"], reverse=True)
        return {"items": items, "count": len(items), "scanned_jobs": len(self.jobs)}

    def open_jobs(self):
        return [job for job in self.jobs.values() if job["status"] not in TERMINAL]
