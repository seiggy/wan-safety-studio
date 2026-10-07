"""aiohttp routes for vision projects. Authentication, CSRF and logging come from the portal middleware."""
from __future__ import annotations

import asyncio
import functools
from pathlib import Path
import tempfile

from aiohttp import web

from vision.domain import ValidationError
from vision.service import Conflict, NotFound, VisionService

PREFIX = "/api/vision"


def mapped(handler):
    @functools.wraps(handler)
    async def wrapper(request):
        try:
            return await handler(request)
        except ValidationError as error:
            raise web.HTTPBadRequest(text=str(error)) from None
        except NotFound as error:
            raise web.HTTPNotFound(text=str(error)) from None
        except Conflict as error:
            raise web.HTTPConflict(text=str(error)) from None
        except FileExistsError:
            raise web.HTTPConflict(text="This item already exists.") from None
    return wrapper


async def body(request):
    try:
        value = await request.json()
    except ValueError:
        raise web.HTTPBadRequest(text="Expected a JSON body.") from None
    if not isinstance(value, dict):
        raise web.HTTPBadRequest(text="Expected a JSON object.")
    return value


def register(app: web.Application, service: VisionService, training=None):
    run = asyncio.to_thread
    p = "/projects/{project}"

    @mapped
    async def list_projects(request):
        return web.json_response({"projects": await run(service.list_projects)})

    @mapped
    async def create_project(request):
        value = await body(request)
        project = await run(service.create_project, value.get("name"), value.get("classes"))
        return web.json_response(project.to_json(), status=201)

    @mapped
    async def get_project(request):
        return web.json_response((await run(service.get_project, request.match_info["project"])).to_json())

    @mapped
    async def list_videos(request):
        return web.json_response({"videos": await run(service.list_videos, request.match_info["project"])})

    @mapped
    async def upload_video(request):
        project = request.match_info["project"]
        await run(service.get_project, project)
        part = await (await request.multipart()).next()
        if part is None or part.name != "file":
            raise web.HTTPBadRequest(text="Upload one video as the 'file' field.")
        with tempfile.TemporaryDirectory(prefix="vision-upload-") as tmp:
            path = Path(tmp) / "upload.bin"
            with path.open("wb") as out:
                while chunk := await part.read_chunk(1 << 20):
                    await run(out.write, chunk)
            video = await run(service.import_video, project, path, {"kind": "upload", "filename": (part.filename or "")[:200]})
        return web.json_response({"id": video}, status=201)

    @mapped
    async def import_gallery(request):
        value = await body(request)
        video = await run(service.import_gallery_video, request.match_info["project"], str(value.get("blob", "")))
        return web.json_response({"id": video}, status=201)

    @mapped
    async def extract(request):
        value = await body(request)
        try:
            fps = float(value.get("fps", 2))
        except (TypeError, ValueError):
            raise web.HTTPBadRequest(text="fps must be a number.") from None
        index = await run(service.extract, request.match_info["project"], request.match_info["video"], fps)
        return web.json_response({"frames": len(index["frames"])}, status=201)

    @mapped
    async def frames(request):
        return web.json_response(await run(service.frame_index, request.match_info["project"], request.match_info["video"]))

    @mapped
    async def frame_image(request):
        m = request.match_info
        data = await run(service.frame_image, m["project"], m["video"], m["frame"])
        return web.Response(body=data, content_type="image/jpeg")

    @mapped
    async def get_labels(request):
        m = request.match_info
        return web.json_response((await run(service.get_labels, m["project"], m["video"], m["frame"])).to_json())

    @mapped
    async def put_labels(request):
        m, value = request.match_info, await body(request)
        labels = await run(service.put_labels, m["project"], m["video"], m["frame"],
                           value.get("reviewed", False), value.get("boxes", []))
        return web.json_response(labels.to_json())

    @mapped
    async def freeze(request):
        value = await body(request)
        try:
            fraction, seed = float(value.get("valFraction", 0.2)), int(value.get("seed", 0))
        except (TypeError, ValueError):
            raise web.HTTPBadRequest(text="valFraction and seed must be numbers.") from None
        return web.json_response(await run(service.freeze_dataset, request.match_info["project"], fraction, seed), status=201)

    @mapped
    async def list_datasets(request):
        return web.json_response({"datasets": await run(service.list_datasets, request.match_info["project"])})

    background: set[asyncio.Task] = set()

    @mapped
    async def trainers(request):
        return web.json_response({"trainers": training.trainers()})

    @mapped
    async def start_run(request):
        value = await body(request)
        epochs = value.get("epochs")
        if epochs is not None and not isinstance(epochs, int):
            raise web.HTTPBadRequest(text="epochs must be an integer.")
        record = await run(training.start, request.match_info["project"], str(value.get("dataset", "")),
                           str(value.get("trainer", "")), value.get("preset"), epochs)
        task = asyncio.create_task(run(training.execute, record))
        background.add(task)
        task.add_done_callback(background.discard)
        return web.json_response(record, status=202)

    @mapped
    async def list_runs(request):
        return web.json_response({"runs": await run(training.list, request.match_info["project"])})

    @mapped
    async def get_run(request):
        return web.json_response(await run(training.get, request.match_info["project"], request.match_info["run"]))

    if training is not None:
        app.router.add_get(f"{PREFIX}/trainers", trainers)
        app.router.add_post(f"{PREFIX}{p}/runs", start_run)
        app.router.add_get(f"{PREFIX}{p}/runs", list_runs)
        app.router.add_get(f"{PREFIX}{p}/runs/{{run}}", get_run)

    app.router.add_get(f"{PREFIX}/projects", list_projects)
    app.router.add_post(f"{PREFIX}/projects", create_project)
    app.router.add_get(f"{PREFIX}{p}", get_project)
    app.router.add_get(f"{PREFIX}{p}/videos", list_videos)
    app.router.add_post(f"{PREFIX}{p}/videos", upload_video)
    app.router.add_post(f"{PREFIX}{p}/videos/from-gallery", import_gallery)
    app.router.add_post(f"{PREFIX}{p}/videos/{{video}}/extract", extract)
    app.router.add_get(f"{PREFIX}{p}/frames/{{video}}", frames)
    app.router.add_get(f"{PREFIX}{p}/frames/{{video}}/{{frame}}.jpg", frame_image)
    app.router.add_get(f"{PREFIX}{p}/labels/{{video}}/{{frame}}", get_labels)
    app.router.add_put(f"{PREFIX}{p}/labels/{{video}}/{{frame}}", put_labels)
    app.router.add_post(f"{PREFIX}{p}/datasets", freeze)
    app.router.add_get(f"{PREFIX}{p}/datasets", list_datasets)
