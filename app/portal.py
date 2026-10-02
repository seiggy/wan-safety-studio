"""Entra-authenticated UI around the prepared job factories: loopback locally, private HTTPS on App Service."""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timezone
import itertools
import json
import logging
import math
import os
from pathlib import Path
import re
import secrets
import shutil
import tempfile
import time

from aiohttp import web
from azure.core.exceptions import ResourceNotFoundError
import msal
from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode

from config import build_credential, identifier, load_foundation
from diagnostics import error_detail, failure, memory, step

LOCAL_ORIGIN = "http://localhost:51881"
ORIGIN = os.environ.get("WAN_STUDIO_PUBLIC_ORIGIN", LOCAL_ORIGIN)
if ORIGIN != LOCAL_ORIGIN and not re.fullmatch(r"https://[a-z0-9-]+(\.[a-z0-9-]+)+", ORIGIN):
    raise ValueError("WAN_STUDIO_PUBLIC_ORIGIN must be the loopback origin or an https://host origin.")
HOSTED = ORIGIN != LOCAL_ORIGIN
# Opt-in for a host whose network or platform auth already gates access; every request then acts as one fixed user.
AUTH_DISABLED = HOSTED and os.environ.get("WAN_STUDIO_DISABLE_AUTH", "").lower() == "true"
HOST = ORIGIN.split("://", 1)[1]
REDIRECT = ORIGIN + "/auth/callback"
WEB_ROOT = Path(__file__).with_name("web")
SESSION_SECONDS = 3600
# Pixel counts match the prepared 768x768 default, so GPU time stays comparable; all are multiples of 16.
ASPECTS = {"16:9": (1024, 576), "4:3": (896, 672), "1:1": (768, 768)}
TAKES = (1, 3, 5)
MAX_SCENES = 5
# WAN 2.2 I2V is trained on ~5 s (81-frame) clips; longer single passes grow attention cost quadratically.
DURATION_RANGE = (5.0, 10.0)
TERMINAL_JOB_STATES = {"Completed", "Failed", "Canceled", "Cancelled", "NotResponding"}
OPEN_JOB_SCAN = 200
TRACER = trace.get_tracer("wan-safety-studio.portal")
LOGGER = logging.getLogger("wan.portal")


@dataclass(frozen=True)
class FoundationSettings:
    workspace_name: str
    resource_group: str
    compute: str
    storage_account: str
    storage_container: str
    max_upload_mb: int = 64


def foundation_settings():
    foundation = load_foundation()
    return FoundationSettings(
        foundation["workspaceName"], foundation["resourceGroupName"], foundation["computeName"],
        foundation["storageAccountName"], foundation["containerName"],
    )


def load_auth_config(cache: Path):
    client_id = os.environ.get("WAN_STUDIO_PORTAL_CLIENT_ID")
    if client_id is not None:
        # Container host: the registration's client ID is an app setting; tenant comes from the foundation.
        return {"tenantId": load_foundation()["tenantId"], "clientId": identifier(client_id)}
    path = cache / "portal-auth.json"
    if not path.is_file():
        raise ValueError("Run scripts/Initialize-PortalAuth.ps1 -ApproveIdentityChanges before Portal.")
    auth = json.loads(path.read_text(encoding="utf-8-sig"))
    for field in ("tenantId", "clientId", "groupId", "applicationObjectId", "servicePrincipalId"):
        auth[field] = identifier(auth[field])
    foundation = load_foundation()
    if (auth["tenantId"] != foundation["tenantId"] or
            auth.get("redirectUri") != REDIRECT or auth.get("role") != "VideoCreator" or
            not re.fullmatch(r"[a-zA-Z0-9-]{1,127}", auth.get("secretName", ""))):
        raise ValueError("Portal authentication settings differ from the selected tenant/redirect contract.")
    return auth


def creator_identity(claims, auth):
    if not isinstance(claims, dict):
        LOGGER.warning("Sign-in rejected: no ID token claims were returned")
    else:
        LOGGER.info("Sign-in claims: tenant_ok=%s audience_ok=%s roles=%s", claims.get("tid") == auth["tenantId"],
                    claims.get("aud") == auth["clientId"], claims.get("roles"))
    if (not isinstance(claims, dict) or claims.get("tid") != auth["tenantId"] or
            claims.get("aud") != auth["clientId"] or not isinstance(claims.get("roles"), list) or
            "VideoCreator" not in claims["roles"]):
        raise web.HTTPForbidden(text="Your account must be assigned the Video Creator role through the approved creator group.")
    try:
        object_id = identifier(claims.get("oid"))
        expiry = int(claims["exp"])
    except (ValueError, TypeError, KeyError):
        raise web.HTTPForbidden(text="Sign-in did not provide a valid user identity.") from None
    if expiry <= time.time():
        raise web.HTTPUnauthorized(text="Sign-in has expired. Sign in again.")
    return {"id": object_id, "name": str(claims.get("name", "Video creator"))[:200],
            "expires": min(time.time() + SESSION_SECONDS, expiry)}


def submission_armed(cache: Path, manifest, settings):
    if manifest is None:
        return False
    path = cache / "armed.json"
    if not path.is_file():
        return False
    gate = json.loads(path.read_text(encoding="utf-8-sig"))
    return (gate.get("compute"), gate.get("profile"), gate.get("version")) == (
        settings.compute, manifest["profile"], manifest["version"])


def create_app(settings, manifest, cache: Path, auth, secret, upstream):
    # secret: a client secret string locally, or {"client_assertion": callable} for the
    # App Service managed-identity federated credential.
    if AUTH_DISABLED:
        LOGGER.warning("WAN_STUDIO_DISABLE_AUTH is true: sign-in is OFF and anyone who can reach this site can submit GPU jobs.")
        identity_client = None
    else:
        if not secret:
            raise ValueError("The MSAL client credential is empty; refusing unauthenticated startup.")
        with TRACER.start_as_current_span("msal.initialize", record_exception=False, set_status_on_exception=False):
            identity_client = msal.ConfidentialClientApplication(
                auth["clientId"], authority=f"https://login.microsoftonline.com/{auth['tenantId']}",
                client_credential=secret, enable_pii_log=False, timeout=30, exclude_scopes=["offline_access"],
            )
    open_session = {"id": "00000000-0000-0000-0000-000000000000", "name": "Open access (sign-in disabled)",
                    "expires": 32503680000.0, "csrf": secrets.token_urlsafe(32)}
    # ponytail: process-local sessions; the App Service plan is pinned to one instance.
    sessions, flows = {}, {}
    batches, tasks = {}, set()
    submit_lock = asyncio.Lock()

    def expire():
        now = time.time()
        for store in (sessions, flows):
            for key in list(store):
                if store[key]["expires"] <= now:
                    del store[key]

    def session_cookie(response, value):
        response.set_cookie("wan_session", value, max_age=SESSION_SECONDS, httponly=True,
                            samesite="Lax", secure=HOSTED, path="/")

    @web.middleware
    async def protect(request, handler):
        request_id = secrets.token_hex(8)
        request["id"] = request_id
        started = time.perf_counter()
        note = ""
        with TRACER.start_as_current_span("portal." + request.method, record_exception=False,
                                          set_status_on_exception=False) as span:
            span.set_attribute("http.request.method", request.method)
            try:
                if HOSTED:
                    # The platform health probe may not send the site host; /healthz exposes nothing.
                    if request.host != HOST and request.path != "/healthz":
                        raise web.HTTPForbidden(text="This portal accepts only its private App Service host.")
                elif request.host not in ("localhost:51881", "127.0.0.1:51881"):
                    raise web.HTTPForbidden(text="This portal accepts only its loopback host.")
                if request.host == "127.0.0.1:51881" and request.path in ("/", "/videos", "/auth/login"):
                    raise web.HTTPFound(ORIGIN + request.path)
                expire()
                public = request.path in ("/", "/videos", "/healthz", "/auth/login", "/auth/callback",
                                          "/web/app.css", "/web/app.js")
                session = sessions.get(request.cookies.get("wan_session")) or (open_session if AUTH_DISABLED else None)
                if not public and session is None:
                    raise web.HTTPUnauthorized(text="Sign in with your approved creator account.")
                if session is not None:
                    request["session"] = session
                if request.method not in ("GET", "HEAD", "OPTIONS") and not AUTH_DISABLED:
                    origin_ok = request.headers.get("Origin") == ORIGIN
                    token_ok = session is not None and secrets.compare_digest(
                        request.headers.get("X-CSRF-Token", ""), session["csrf"])
                    if not (origin_ok and token_ok):
                        # Origins are not secret; the token itself is never logged.
                        LOGGER.warning("Rejected %s %s: session=%s origin=%r expected=%r token_ok=%s", request.method,
                                       request.path, session is not None, request.headers.get("Origin"), ORIGIN, token_ok)
                        raise web.HTTPForbidden(text="Invalid request origin or CSRF token. Refresh and try again.")
                response = await handler(request)
            except web.HTTPException as error:
                note = error.text or ""
                if 300 <= error.status < 400:
                    response = error
                elif request.path == "/auth/callback":
                    response = web.HTTPFound("/?signin=" + ("forbidden" if error.status == 403 else "failed"))
                else:
                    span.set_status(Status(StatusCode.ERROR, f"HTTP {error.status}"))
                    response = web.json_response({"error": error.text, "requestId": request_id}, status=error.status)
            except Exception as error:
                span.set_status(Status(StatusCode.ERROR, type(error).__name__))
                note = failure(error)
                LOGGER.error("req=%s %s %s unhandled: %s", request_id, request.method, request.path, note)
                response = web.json_response({
                    "error": "The operation failed. Check the operator login, VPN/private DNS and server log. No automatic resubmission.",
                    "requestId": request_id,
                }, status=502)
            if isinstance(response, web.HTTPException):
                redirect = web.Response(status=response.status, headers=response.headers, body=response.body)
                redirect.cookies.update(response.cookies)
                response = redirect
            span.set_attribute("http.response.status_code", response.status)
            quiet = request.path == "/healthz" or request.path.startswith("/web/")
            LOGGER.log(logging.DEBUG if quiet and response.status < 400 else logging.WARNING if response.status >= 400 else logging.INFO,
                       "req=%s %s %s -> %s in %dms%s", request_id, request.method, request.path, response.status,
                       (time.perf_counter() - started) * 1000, f" | {note}" if note else "")
            if response.status >= 400:
                span.set_status(Status(StatusCode.ERROR, f"HTTP {response.status}"))
            response.headers.update({
                "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
                "Referrer-Policy": "no-referrer", "X-Frame-Options": "DENY",
                "Content-Security-Policy": (
                    "default-src 'self'; script-src 'self'; style-src 'self'; "
                    f"img-src 'self' blob: data: https://{settings.storage_account}.blob.core.windows.net; "
                    f"media-src 'self' https://{settings.storage_account}.blob.core.windows.net; "
                    "connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
                ),
            })
            return response

    async def index(request):
        return web.FileResponse(WEB_ROOT / "index.html")

    async def login(request):
        if AUTH_DISABLED:
            raise web.HTTPFound("/")
        if len(flows) >= 64:
            raise web.HTTPTooManyRequests(text="Too many pending sign-ins. Wait a few minutes and try again.")
        with TRACER.start_as_current_span("msal.authorize", record_exception=False, set_status_on_exception=False):
            flow = await asyncio.to_thread(identity_client.initiate_auth_code_flow, [],
                                           redirect_uri=REDIRECT, prompt="select_account")
        if "auth_uri" not in flow or "state" not in flow:
            raise RuntimeError("MSAL could not start authorization.")
        ticket = secrets.token_urlsafe(32)
        flows[ticket] = {"flow": flow, "expires": time.time() + 600}
        response = web.HTTPFound(flow["auth_uri"])
        response.set_cookie("wan_login", ticket, max_age=600, httponly=True, samesite="Lax", path="/auth")
        return response

    async def callback(request):
        if AUTH_DISABLED:
            raise web.HTTPFound("/")
        pending = flows.pop(request.cookies.get("wan_login"), None)
        if pending is None:
            raise web.HTTPBadRequest(text="This sign-in attempt expired or was already used. Return to the studio and sign in again.")
        try:
            with TRACER.start_as_current_span("msal.exchange", record_exception=False, set_status_on_exception=False):
                result = await asyncio.to_thread(identity_client.acquire_token_by_auth_code_flow,
                                                 pending["flow"], dict(request.query))
        except ValueError:
            raise web.HTTPBadRequest(text="Sign-in state/nonce verification failed. Start a new sign-in.") from None
        if result.get("error"):
            LOGGER.warning("MSAL sign-in failed: %s", result.get("error"))
            raise web.HTTPUnauthorized(text="Microsoft sign-in failed. Check app assignment and tenant, then sign in again.")
        identity = creator_identity(result.get("id_token_claims"), auth)
        if len(sessions) >= 512:
            raise web.HTTPServiceUnavailable(text="The local studio has reached its session limit. Restart it or wait for sessions to expire.")
        sessions.pop(request.cookies.get("wan_session"), None)
        session_id = secrets.token_urlsafe(32)
        sessions[session_id] = {**identity, "csrf": secrets.token_urlsafe(32)}
        response = web.HTTPFound("/")
        response.del_cookie("wan_login", path="/auth")
        session_cookie(response, session_id)
        return response

    async def logout(request):
        sessions.pop(request.cookies.get("wan_session"), None)
        response = web.json_response({"signedOut": True})
        response.del_cookie("wan_session", path="/")
        return response

    async def whoami(request):
        user = request["session"]
        return web.json_response({"name": user["name"], "csrfToken": user["csrf"], "expires": user["expires"]})

    def read_status():
        if upstream is None:
            from azure.ai.ml import MLClient
            from azure.storage.blob import BlobServiceClient
            foundation = load_foundation()
            credential = build_credential()
            client = MLClient(credential, foundation["subscriptionId"], settings.resource_group, settings.workspace_name)
            blob_service = BlobServiceClient(f"https://{settings.storage_account}.blob.core.windows.net", credential=credential)
        else:
            client = upstream.workspace_ml_client(settings)
            blob_service = upstream.make_blob_service_client(settings)
        with step("azureml.workspace.read", workspace=settings.workspace_name):
            client.workspaces.get(settings.workspace_name)
        with step("azureml.compute.read", compute=settings.compute):
            try:
                compute = client.compute.get(settings.compute)
            except ResourceNotFoundError:
                LOGGER.warning("Compute %s not found in workspace %s; reporting 'Not configured'", settings.compute,
                               settings.workspace_name)
                compute = None
        with step("storage.container.read", account=settings.storage_account, container=settings.storage_container):
            with blob_service as blob:
                blob.get_container_client(settings.storage_container).get_container_properties()
        state = str(compute.provisioning_state) if compute is not None else "Not configured"
        armed = submission_armed(cache, manifest, settings)
        return {
            "workspace": settings.workspace_name, "resourceGroup": settings.resource_group,
            "storage": settings.storage_account, "compute": settings.compute, "computeState": state,
            "prepared": manifest is not None, "version": manifest["version"] if manifest else None, "armed": armed,
            "generationEnabled": armed and state == "Succeeded",
            "checkedAt": datetime.now(timezone.utc).isoformat(),
            "maxJobMinutes": 120, "maxUploadMb": settings.max_upload_mb,
            "profile": {"label": settings.profiles["wan"].label, "fps": settings.profiles["wan"].fps,
                        "width": settings.profiles["wan"].width, "height": settings.profiles["wan"].height,
                        "durationSeconds": settings.profiles["wan"].duration_seconds,
                        "durationStep": settings.profiles["wan"].duration_step_seconds} if manifest else None,
        }

    async def status(request):
        return web.json_response(await asyncio.to_thread(read_status))

    async def upload_reference(upload):
        safe_name = upstream.sanitize_filename(upload.filename or "input_image.png")
        with tempfile.TemporaryDirectory(prefix="wan-upload-") as temp_dir:
            local_path = Path(temp_dir) / safe_name
            with local_path.open("wb") as output:
                shutil.copyfileobj(upload.file, output)
            try:
                info = await asyncio.to_thread(upstream.validate_image, local_path)
            except OSError:
                raise web.HTTPBadRequest(text=f"{safe_name} is not a readable PNG, JPEG or WebP image.") from None
            blob_name = upstream.make_blob_name(settings, safe_name)
            with step("storage.input.upload", blob=blob_name, bytes=local_path.stat().st_size):
                url = await asyncio.to_thread(upstream.upload_blob, local_path, blob_name, settings)
        return {**info, "filename": safe_name, "url": url, "blob_name": blob_name}

    async def run_batch(batch, plan, profile, negative_prompt, duration, size):
        # Owns submit_lock (acquired by submit) until every job is created or one fails. Never retries.
        seeds = set()
        try:
            for item in plan:
                if not submission_armed(cache, manifest, settings):
                    raise web.HTTPConflict(text="The operator disarmed generation.")
                args = upstream.build_submission_args(
                    settings, profile, prompt=item["prompt"], negative_prompt=negative_prompt,
                    duration_seconds=duration, uploaded_images={"input_image": item["image"]})
                while args.seed in seeds:
                    args.seed = 10**14 + secrets.randbelow(9 * 10**14)
                seeds.add(args.seed)
                args.width, args.height = size
                # A unique version keeps each job's display name and video-library folder distinct.
                args.version = f"{args.version}-s{item['scene']}t{item['take']}"
                if args.generated_output_path:
                    args.generated_output_path = args.generated_output_path.rstrip("/").rsplit("/", 1)[0] + f"/{args.version}/"
                with step("azureml.job.submit", batch=batch["id"], scene=item["scene"], take=item["take"],
                          compute=args.compute if hasattr(args, "compute") else settings.compute, output=args.generated_output_path):
                    result = await asyncio.to_thread(upstream.submit_job, args)
                batch["jobs"].append({**result["job"], "scene": item["scene"], "take": item["take"], "seed": args.seed})
                LOGGER.info("Batch %s job %d of %d created: name=%s status=%s", batch["id"], len(batch["jobs"]), batch["total"],
                            result["job"].get("name"), result["job"].get("status"))
        except web.HTTPException as error:
            LOGGER.warning("Batch %s halted: %s", batch["id"], error.text)
            batch["error"] = f"{error.text} {len(batch['jobs'])} of {batch['total']} jobs were submitted; the rest were not."
        except Exception as error:
            LOGGER.error("Batch %s stopped after %d of %d jobs: %s", batch["id"], len(batch["jobs"]), batch["total"], failure(error))
            batch["error"] = (f"Submission stopped after {len(batch['jobs'])} of {batch['total']} jobs ({type(error).__name__}"
                              f"{error_detail(error)[:300]}). Submitted jobs continue; the rest were not retried.")
        except BaseException as error:
            LOGGER.error("Batch %s cancelled after %d of %d jobs: %s", batch["id"], len(batch["jobs"]), batch["total"], failure(error))
            raise
        finally:
            batch["done"] = True
            LOGGER.info("Batch %s finished: %d of %d jobs created, error=%s", batch["id"], len(batch["jobs"]), batch["total"],
                        bool(batch["error"]))
            submit_lock.release()

    def batch_view(batch):
        return {key: batch[key] for key in ("id", "total", "jobs", "done", "error")}

    async def submit(request):
        if not submission_armed(cache, manifest, settings):
            gate = cache / "armed.json"
            LOGGER.warning("req=%s submit refused: not armed (gate file %s, manifest %s, compute %s)", request["id"],
                           "present" if gate.is_file() else "missing", "loaded" if manifest else "missing", settings.compute)
            raise web.HTTPConflict(text="GPU generation is not armed. Complete network approvals and run Start -NoPortal with the required spend approvals.")
        if submit_lock.locked():
            LOGGER.warning("req=%s submit refused: another batch is still being submitted", request["id"])
            raise web.HTTPConflict(text="A batch is still being submitted. Wait for its job IDs; do not resubmit.")
        await submit_lock.acquire()
        try:
            form = await request.post()
            LOGGER.info("req=%s submit form parsed: %d prompt(s), takes=%s, aspect=%s, duration=%s %s", request["id"],
                        len(form.getall("prompt", [])), form.get("takes"), form.get("aspect_ratio"),
                        form.get("duration_seconds"), memory())
            if form.get("profile", "wan") != "wan":
                raise web.HTTPBadRequest(text="Only the prepared WAN profile is supported.")
            profile = settings.profiles["wan"]
            prompts = [str(value).strip() for value in form.getall("prompt", [])]
            negative_prompt = str(form.get("negative_prompt", profile.negative_prompt)).strip()
            if not 1 <= len(prompts) <= MAX_SCENES:
                raise web.HTTPBadRequest(text=f"Add between 1 and {MAX_SCENES} scenes.")
            if any(not 1 <= len(prompt) <= 4000 for prompt in prompts) or len(negative_prompt) > 4000:
                raise web.HTTPBadRequest(text="Describe every scene in 1-4000 characters; the negative prompt also has a 4000-character limit.")
            low, high = DURATION_RANGE
            try:
                duration = float(form.get("duration_seconds", ""))
            except (ValueError, TypeError):
                duration = math.nan
            if not math.isfinite(duration) or not low <= duration <= high:
                raise web.HTTPBadRequest(text=f"Duration must be between {low:g} and {high:g} seconds.")
            size = ASPECTS.get(str(form.get("aspect_ratio", "")))
            if size is None:
                raise web.HTTPBadRequest(text="Choose a 16:9, 4:3 or 1:1 aspect ratio.")
            takes = next((count for count in TAKES if str(count) == str(form.get("takes", ""))), None)
            if takes is None:
                raise web.HTTPBadRequest(text="Choose 1, 3 or 5 videos per scene.")
            uploads = [form.get(f"input_image_{index}") for index in range(len(prompts))]
            provided = [isinstance(upload, web.FileField) and bool(upload.filename) for upload in uploads]
            if not provided[0]:
                raise web.HTTPBadRequest(text="Scene 1 needs a reference image.")
            images = []
            for upload, present in zip(uploads, provided):
                images.append(await upload_reference(upload) if present else images[0])
        except BaseException as error:
            if not isinstance(error, web.HTTPException):
                LOGGER.error("req=%s submit failed before the batch was created: %s", request["id"], failure(error))
            submit_lock.release()
            raise
        plan = [{"scene": scene, "take": take, "prompt": prompt, "image": image}
                for scene, (prompt, image) in enumerate(zip(prompts, images), 1) for take in range(1, takes + 1)]
        batch = {"id": secrets.token_urlsafe(12), "owner": request["session"]["id"], "total": len(plan),
                 "jobs": [], "done": False, "error": None, "created": time.time()}
        for key in [key for key, old in batches.items() if old["done"] and old["created"] < time.time() - 86400]:
            del batches[key]
        batches[batch["id"]] = batch
        LOGGER.info("Batch %s created: %d jobs planned for session %s (pid %s, instance %s) %s", batch["id"], batch["total"],
                    batch["owner"], os.getpid(), os.environ.get("WEBSITE_INSTANCE_ID", "?")[:12], memory())
        task = asyncio.create_task(run_batch(batch, plan, profile, negative_prompt, duration, size))
        tasks.add(task)
        task.add_done_callback(tasks.discard)
        return web.json_response({"batch": batch_view(batch)})

    async def batch_status(request):
        batch = batches.get(request.match_info["batch_id"])
        if batch is None or batch["owner"] != request["session"]["id"]:
            LOGGER.warning("Batch %s lookup failed: %s (known batches %d, pid %s, instance %s)", request.match_info["batch_id"],
                           "unknown id" if batch is None else "different session", len(batches), os.getpid(),
                           os.environ.get("WEBSITE_INSTANCE_ID", "?")[:12])
            raise web.HTTPNotFound(text="This batch is not available. Its submitted jobs remain in the video library.")
        return web.json_response(batch_view(batch))

    async def health(request):
        return web.json_response({"status": "ok", "authentication": "disabled" if AUTH_DISABLED else "required"})

    async def gallery(request):
        if upstream is None:
            raise web.HTTPConflict(text="WAN preparation is not complete. Complete Prepare and restart Portal to load the video library.")
        return await upstream.handle_gallery(request)

    async def job_status(request):
        if upstream is None:
            raise web.HTTPConflict(text="Complete Prepare and restart Portal before inspecting generation jobs.")
        return await upstream.handle_job_status(request)

    def read_open_jobs():
        # Azure ML is the queue: list every unfinished studio job, whoever or whichever session submitted it.
        client = upstream.workspace_ml_client(settings)
        experiment = settings.profiles["wan"].experiment_name
        with TRACER.start_as_current_span("azureml.jobs.list", record_exception=False, set_status_on_exception=False):
            found = []
            for job in itertools.islice(client.jobs.list(max_results=OPEN_JOB_SCAN), OPEN_JOB_SCAN):
                if getattr(job, "experiment_name", None) != experiment or str(job.status) in TERMINAL_JOB_STATES:
                    continue
                created = getattr(getattr(job, "creation_context", None), "created_at", None)
                found.append({"name": job.name, "display_name": job.display_name, "status": str(job.status),
                              "studio_url": job.studio_url, "created_at": created.isoformat() if created else None})
        return {"jobs": found}

    async def open_jobs(request):
        if upstream is None:
            raise web.HTTPConflict(text="Complete Prepare and restart Portal before inspecting generation jobs.")
        return web.json_response(await asyncio.to_thread(read_open_jobs))

    app = web.Application(client_max_size=settings.max_upload_mb * 1024 * 1024, middlewares=[protect])
    app["settings"] = settings
    app["gallery_cache"] = {"source": None, "source_fetched_at": 0.0, "payload": None, "payload_fetched_at": 0.0}
    app.router.add_get("/", index)
    app.router.add_get("/videos", index)
    app.router.add_get("/healthz", health)
    app.router.add_get("/auth/login", login)
    app.router.add_get("/auth/callback", callback)
    app.router.add_post("/auth/logout", logout)
    app.router.add_get("/api/session", whoami)
    app.router.add_get("/api/status", status)
    app.router.add_post("/api/submit", submit)
    app.router.add_get("/api/batches/{batch_id}", batch_status)
    app.router.add_get("/api/jobs", open_jobs)
    app.router.add_get("/api/jobs/{job_name}", job_status)
    app.router.add_get("/api/gallery", gallery)
    app.router.add_static("/web", WEB_ROOT, show_index=False)

    async def stopping(_):
        LOGGER.warning("Portal shutting down on a stop signal (pid %s); %d batch(es) in memory are lost", os.getpid(), len(batches))

    app.on_shutdown.append(stopping)
    return app
