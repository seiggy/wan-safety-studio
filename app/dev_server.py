"""Local development API (run by the Aspire AppHost): vision API plus ComfyUI generation, no Azure and no sign-in.

Never deploy this: it has no authentication. It binds to loopback and is reached through the front end's dev proxy.
Storage: local folder (or LAN share mount), Azurite, or Azure Blob. Videos come from ComfyUI over its API or a shared folder.
"""
from __future__ import annotations

import asyncio
import logging
import mimetypes
import os
from pathlib import Path
import re
import secrets

from aiohttp import web
from opentelemetry import trace

from generation.comfyui import ApiRetrieval, ComfyUiClient, ShareRetrieval
from generation.jobs import ASPECTS, LocalGeneration, MissingWorkflow
from generation.storage import store_from_env
from telemetry import configure_telemetry
from vision import routes as vision_routes
from vision.adapters.media import FfmpegTools
from vision.adapters.compute_docker import LocalDockerCompute
from vision.service import VisionService
from vision.trainers.registry import TrainerRegistry
from vision.usecases.train import TrainingService

LOGGER = logging.getLogger("wan.dev")
TRACER = trace.get_tracer("wan-safety-studio.dev-api")
CSRF = secrets.token_urlsafe(16)
HERE = Path(__file__).parent
MAX_SCENES, TAKES, DURATION = 5, (1, 3, 5), (5.0, 10.0)
RANGE = re.compile(r"bytes=(\d*)-(\d*)")


def build_generation(store, env=os.environ):
    url = env.get("COMFYUI_URL", "http://localhost:8188")
    if env.get("WAN_STUDIO_VIDEO_RETRIEVAL", "api") == "share":
        output = env.get("COMFYUI_OUTPUT_DIR")
        if not output:
            raise ValueError("WAN_STUDIO_VIDEO_RETRIEVAL=share needs COMFYUI_OUTPUT_DIR (ComfyUI's output folder as mounted here).")
        retrieval = ShareRetrieval(Path(output))
    else:
        retrieval = ApiRetrieval()
    workflow = Path(env.get("COMFYUI_WORKFLOW") or HERE / "generation" / "workflows" / "wan22_i2v.api.json")
    return LocalGeneration(store, ComfyUiClient(url), retrieval, workflow)


def create_app(store, storage_label: str, generation: LocalGeneration):
    @web.middleware
    async def errors(request, handler):
        with TRACER.start_as_current_span(f"{request.method} {request.path}") as span:
            try:
                response = await handler(request)
            except web.HTTPException as error:
                response = web.json_response({"error": error.text or error.reason}, status=error.status)
            except MissingWorkflow as error:
                response = web.json_response({"error": str(error)}, status=409)
            span.set_attribute("http.response.status_code", response.status)
            return response

    async def session(request):
        return web.json_response({"name": "Local developer", "csrfToken": CSRF, "expires": 32503680000.0})

    async def status(request):
        up = await generation.client.reachable()
        ready = up and generation.workflow_path.is_file()
        return web.json_response({
            "mode": "local", "workspace": "local-dev", "resourceGroup": "n/a", "storage": storage_label,
            "compute": f"ComfyUI {generation.client.base} ({'reachable' if up else 'unreachable'})",
            "computeState": "Succeeded" if up else "Unreachable", "prepared": generation.workflow_path.is_file(),
            "version": None, "armed": ready, "generationEnabled": ready, "checkedAt": None,
            "maxJobMinutes": 120, "maxUploadMb": 512, "profile": None,
        })

    async def submit(request):
        form = await request.post()
        prompts = [str(v).strip() for v in form.getall("prompt", [])]
        if not 1 <= len(prompts) <= MAX_SCENES or any(not 1 <= len(p) <= 4000 for p in prompts):
            raise web.HTTPBadRequest(text=f"Add 1-{MAX_SCENES} scenes, each 1-4000 characters.")
        aspect = str(form.get("aspect_ratio", ""))
        if aspect not in ASPECTS:
            raise web.HTTPBadRequest(text="Choose a 16:9, 4:3 or 1:1 aspect ratio.")
        try:
            duration = float(form.get("duration_seconds", ""))
            takes = int(form.get("takes", ""))
        except ValueError:
            raise web.HTTPBadRequest(text="Duration and takes must be numbers.") from None
        if not DURATION[0] <= duration <= DURATION[1] or takes not in TAKES:
            raise web.HTTPBadRequest(text="Duration or takes out of range.")
        images = []
        for index in range(len(prompts)):
            upload = form.get(f"input_image_{index}")
            if isinstance(upload, web.FileField) and upload.filename:
                images.append({"image_name": Path(upload.filename).name[:100], "image_bytes": upload.file.read()})
            elif images:
                images.append(images[0])
            else:
                raise web.HTTPBadRequest(text="Scene 1 needs a reference image.")
        plan = [{"scene": s, "take": t, "prompt": p, **images[s - 1]}
                for s, p in enumerate(prompts, 1) for t in range(1, takes + 1)]
        batch = generation.submit(plan, str(form.get("negative_prompt", "")).strip(), duration, aspect)
        return web.json_response({"batch": batch})

    async def batch_status(request):
        batch = generation.batches.get(request.match_info["batch_id"])
        if batch is None:
            raise web.HTTPNotFound(text="This batch is not available.")
        return web.json_response(batch)

    async def open_jobs(request):
        return web.json_response({"jobs": generation.open_jobs()})

    async def job_status(request):
        job = generation.jobs.get(request.match_info["job_name"])
        if job is None:
            raise web.HTTPNotFound(text="Unknown job.")
        return web.json_response(job)

    async def gallery(request):
        return web.json_response(await asyncio.to_thread(generation.gallery))

    async def media(request):
        key = request.match_info["key"]
        if not key.startswith("video-library/"):
            raise web.HTTPNotFound()
        data = await asyncio.to_thread(store.read, key)
        if data is None:
            raise web.HTTPNotFound()
        kind = mimetypes.guess_type(key)[0] or "application/octet-stream"
        match = RANGE.fullmatch(request.headers.get("Range", ""))
        if match and (match.group(1) or match.group(2)):
            size = len(data)
            start = int(match.group(1)) if match.group(1) else max(0, size - int(match.group(2)))
            end = min(int(match.group(2)), size - 1) if match.group(1) and match.group(2) else size - 1
            if start > end:
                raise web.HTTPRequestRangeNotSatisfiable()
            return web.Response(body=data[start:end + 1], status=206, content_type=kind,
                                headers={"Content-Range": f"bytes {start}-{end}/{size}", "Accept-Ranges": "bytes"})
        return web.Response(body=data, content_type=kind, headers={"Accept-Ranges": "bytes"})

    async def health(request):
        return web.json_response({"status": "ok", "mode": "local-dev"})

    app = web.Application(middlewares=[errors], client_max_size=512 * 1024 * 1024)
    app.router.add_get("/healthz", health)
    app.router.add_get("/api/session", session)
    app.router.add_get("/api/status", status)
    app.router.add_post("/api/submit", submit)
    app.router.add_get("/api/batches/{batch_id}", batch_status)
    app.router.add_get("/api/jobs", open_jobs)
    app.router.add_get("/api/jobs/{job_name}", job_status)
    app.router.add_get("/api/gallery", gallery)
    app.router.add_get("/api/media/{key:.+}", media)
    vision = VisionService(store, FfmpegTools())
    vision_routes.register(app, vision, TrainingService(vision, TrainerRegistry(), LocalDockerCompute()))
    return app


def main():
    configure_telemetry("wan-studio-dev-api")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)-7s %(name)s: %(message)s")
    store, label = store_from_env(default_dir=Path(os.environ.get("WAN_STUDIO_DEV_DATA", ".dev-data")) / "blob")
    LOGGER.warning("LOCAL DEV MODE: no authentication. Storage: %s", label)
    port = int(os.environ.get("PORT", "51882"))
    web.run_app(create_app(store, label, build_generation(store)), host="127.0.0.1", port=port, access_log=None)


if __name__ == "__main__":
    main()
