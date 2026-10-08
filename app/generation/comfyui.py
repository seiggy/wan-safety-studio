"""ComfyUI HTTP client and workflow templating. Retrieval of finished videos is pluggable: API download or a shared folder."""
from __future__ import annotations

import asyncio
import copy
import json
from pathlib import Path
import re
import secrets
import time
from typing import Protocol

import aiohttp
from opentelemetry import trace

TRACER = trace.get_tracer("wan-safety-studio.comfyui")
VIDEO_EXTENSIONS = (".mp4", ".webm", ".mov", ".mkv")
PLACEHOLDER = re.compile(r"\{\{([a-z_]+)\}\}")
PLACEHOLDERS = {"positive_prompt", "negative_prompt", "input_image", "seed", "width", "height", "length", "fps", "filename_prefix"}


class ComfyUiError(RuntimeError):
    pass


def render_workflow(template: dict, values: dict):
    """Replace "{{name}}" string values. A value that is exactly one placeholder keeps its type (ints stay ints)."""
    unknown = set()

    def walk(node):
        if isinstance(node, dict):
            return {key: walk(item) for key, item in node.items()}
        if isinstance(node, list):
            return [walk(item) for item in node]
        if isinstance(node, str):
            whole = PLACEHOLDER.fullmatch(node)
            if whole:
                if whole.group(1) not in values:
                    unknown.add(whole.group(1))
                    return node
                return values[whole.group(1)]
            return PLACEHOLDER.sub(lambda m: str(values.get(m.group(1), m.group(0))), node)
        return node

    rendered = walk(copy.deepcopy(template))
    if unknown:
        raise ComfyUiError(f"Workflow uses unknown placeholders: {sorted(unknown)}. Supported: {sorted(PLACEHOLDERS)}.")
    return rendered


def find_videos(history_entry: dict):
    """Every output file reference that looks like a video, whichever node type produced it."""
    found = []
    for node in (history_entry.get("outputs") or {}).values():
        for items in node.values():
            if isinstance(items, list):
                found += [item for item in items if isinstance(item, dict)
                          and str(item.get("filename", "")).lower().endswith(VIDEO_EXTENSIONS)]
    return found


class Retrieval(Protocol):
    async def fetch(self, client: "ComfyUiClient", ref: dict) -> bytes: ...


class ApiRetrieval:
    """Download through ComfyUI's /view endpoint: no shared storage needed."""

    async def fetch(self, client, ref):
        return await client.view(ref)


class ShareRetrieval:
    """Read the file straight from ComfyUI's output folder mounted over NFS/SMB."""

    def __init__(self, output_dir: Path):
        self.root = output_dir.resolve()

    async def fetch(self, client, ref):
        path = (self.root / ref.get("subfolder", "") / ref["filename"]).resolve()
        if self.root not in path.parents:
            raise ComfyUiError("Output path escapes the shared output folder.")
        return await asyncio.to_thread(path.read_bytes)


class ComfyUiClient:
    def __init__(self, base_url: str, poll_seconds: float = 3.0, timeout_seconds: float = 3600.0):
        self.base = base_url.rstrip("/")
        self.poll, self.timeout = poll_seconds, timeout_seconds
        self.client_id = secrets.token_hex(8)

    async def _json(self, session, method, path, **kwargs):
        async with session.request(method, self.base + path, **kwargs) as response:
            body = await response.text()
            if response.status >= 400:
                raise ComfyUiError(f"ComfyUI {method} {path} -> {response.status}: {body[:500]}")
            return json.loads(body) if body else {}

    async def upload_image(self, name: str, data: bytes):
        form = aiohttp.FormData()
        form.add_field("image", data, filename=name, content_type="application/octet-stream")
        form.add_field("overwrite", "true")
        with TRACER.start_as_current_span("comfyui.upload_image"):
            async with aiohttp.ClientSession() as session:
                result = await self._json(session, "POST", "/upload/image", data=form)
        return (result.get("subfolder") + "/" if result.get("subfolder") else "") + result["name"]

    async def queue(self, workflow: dict):
        with TRACER.start_as_current_span("comfyui.queue"):
            async with aiohttp.ClientSession() as session:
                result = await self._json(session, "POST", "/prompt", json={"prompt": workflow, "client_id": self.client_id})
        return result["prompt_id"]

    async def wait(self, prompt_id: str):
        """Poll history until the prompt finishes; returns the history entry or raises."""
        deadline = time.monotonic() + self.timeout
        async with aiohttp.ClientSession() as session:
            while time.monotonic() < deadline:
                entry = (await self._json(session, "GET", f"/history/{prompt_id}")).get(prompt_id)
                status = (entry or {}).get("status", {})
                if entry and status.get("status_str") == "error":
                    raise ComfyUiError("ComfyUI reported an execution error for this prompt.")
                if entry and (status.get("completed") or entry.get("outputs")):
                    return entry
                await asyncio.sleep(self.poll)
        raise ComfyUiError("Timed out waiting for ComfyUI.")

    async def run(self, workflow: dict, timer=None):
        """Queue the prompt and wait for it. With a timer, a websocket opened BEFORE queueing feeds it every event (no gaps)."""
        if timer is None:
            return await self.wait(await self.queue(workflow))
        deadline = time.monotonic() + self.timeout
        async with aiohttp.ClientSession() as session:
            try:
                socket = await session.ws_connect(f"{self.base}/ws", params={"clientId": self.client_id}, heartbeat=30)
            except aiohttp.ClientError:
                return await self.wait(await self.queue(workflow))  # no websocket: no per-stage timing
            async with socket:
                prompt_id = await self.queue(workflow)
                while not timer.done:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise ComfyUiError("Timed out waiting for ComfyUI.")
                    message = await socket.receive(timeout=remaining)
                    if message.type != aiohttp.WSMsgType.TEXT:
                        if message.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.ERROR, aiohttp.WSMsgType.CLOSING):
                            break
                        continue
                    event = json.loads(message.data)
                    if (event.get("data") or {}).get("prompt_id", prompt_id) == prompt_id:
                        timer.feed(event, time.time())
        return await self.wait(prompt_id)

    async def view(self, ref: dict):
        params = {"filename": ref["filename"], "subfolder": ref.get("subfolder", ""), "type": ref.get("type", "output")}
        with TRACER.start_as_current_span("comfyui.view"):
            async with aiohttp.ClientSession() as session, session.get(self.base + "/view", params=params) as response:
                if response.status >= 400:
                    raise ComfyUiError(f"ComfyUI /view -> {response.status}")
                return await response.read()

    async def reachable(self):
        try:
            async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=3)) as session:
                await self._json(session, "GET", "/system_stats")
            return True
        except (aiohttp.ClientError, ComfyUiError, asyncio.TimeoutError):
            return False
