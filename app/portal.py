"""Entra-authenticated UI around the prepared job factories: loopback locally, private HTTPS on App Service."""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import logging
import math
import os
from pathlib import Path
import re
import secrets
import time

from aiohttp import web
from azure.core.exceptions import ResourceNotFoundError
import msal
from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode

from config import build_credential, identifier, load_foundation

LOCAL_ORIGIN = "http://localhost:51881"
ORIGIN = os.environ.get("WAN_STUDIO_PUBLIC_ORIGIN", LOCAL_ORIGIN)
if ORIGIN != LOCAL_ORIGIN and not re.fullmatch(r"https://[a-z0-9-]+(\.[a-z0-9-]+)+", ORIGIN):
    raise ValueError("WAN_STUDIO_PUBLIC_ORIGIN must be the loopback origin or an https://host origin.")
HOSTED = ORIGIN != LOCAL_ORIGIN
HOST = ORIGIN.split("://", 1)[1]
REDIRECT = ORIGIN + "/auth/callback"
WEB_ROOT = Path(__file__).with_name("web")
SESSION_SECONDS = 3600
TRACER = trace.get_tracer("wan-safety-studio.portal")
LOGGER = logging.getLogger(__name__)


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
    if not secret:
        raise ValueError("The MSAL client credential is empty; refusing unauthenticated startup.")
    with TRACER.start_as_current_span("msal.initialize", record_exception=False, set_status_on_exception=False):
        identity_client = msal.ConfidentialClientApplication(
            auth["clientId"], authority=f"https://login.microsoftonline.com/{auth['tenantId']}",
            client_credential=secret, enable_pii_log=False, timeout=30, exclude_scopes=["offline_access"],
        )
    # ponytail: process-local sessions; the App Service plan is pinned to one instance.
    sessions, flows = {}, {}
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
                session = sessions.get(request.cookies.get("wan_session"))
                if not public and session is None:
                    raise web.HTTPUnauthorized(text="Sign in with your approved creator account.")
                if session is not None:
                    request["session"] = session
                if request.method not in ("GET", "HEAD", "OPTIONS"):
                    if (session is None or request.headers.get("Origin") != ORIGIN or
                            not secrets.compare_digest(request.headers.get("X-CSRF-Token", ""), session["csrf"])):
                        raise web.HTTPForbidden(text="Invalid request origin or CSRF token. Refresh and try again.")
                response = await handler(request)
            except web.HTTPException as error:
                if 300 <= error.status < 400:
                    response = error
                elif request.path == "/auth/callback":
                    response = web.HTTPFound("/?signin=" + ("forbidden" if error.status == 403 else "failed"))
                else:
                    span.set_status(Status(StatusCode.ERROR, f"HTTP {error.status}"))
                    response = web.json_response({"error": error.text, "requestId": request_id}, status=error.status)
            except Exception as error:
                span.set_status(Status(StatusCode.ERROR, type(error).__name__))
                LOGGER.error("Portal request %s failed: %s", request_id, type(error).__name__)
                response = web.json_response({
                    "error": "The operation failed. Check the operator login, VPN/private DNS and server log. No automatic resubmission.",
                    "requestId": request_id,
                }, status=502)
            if isinstance(response, web.HTTPException):
                redirect = web.Response(status=response.status, headers=response.headers, body=response.body)
                redirect.cookies.update(response.cookies)
                response = redirect
            span.set_attribute("http.response.status_code", response.status)
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
        with TRACER.start_as_current_span("azureml.workspace.read", record_exception=False, set_status_on_exception=False):
            client.workspaces.get(settings.workspace_name)
        with TRACER.start_as_current_span("azureml.compute.read", record_exception=False, set_status_on_exception=False):
            try:
                compute = client.compute.get(settings.compute)
            except ResourceNotFoundError:
                compute = None
        with TRACER.start_as_current_span("storage.container.read", record_exception=False, set_status_on_exception=False):
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

    async def submit(request):
        if not submission_armed(cache, manifest, settings):
            raise web.HTTPConflict(text="GPU generation is not armed. Complete network approvals and run Start -NoPortal with the required spend approvals.")
        if submit_lock.locked():
            raise web.HTTPConflict(text="A submission is already being accepted. Wait for its job ID; do not resubmit.")
        async with submit_lock:
            form = await request.post()
            if form.get("profile", "wan") != "wan":
                raise web.HTTPBadRequest(text="Only the prepared WAN profile is supported.")
            if not 1 <= len(str(form.get("prompt", "")).strip()) <= 4000 or len(str(form.get("negative_prompt", ""))) > 4000:
                raise web.HTTPBadRequest(text="Describe the scene in 1-4000 characters; the negative prompt also has a 4000-character limit.")
            try:
                duration = float(form.get("duration_seconds", ""))
            except (ValueError, TypeError):
                raise web.HTTPBadRequest(text="Duration must be a number between 0.5 and 30 seconds.") from None
            if not math.isfinite(duration) or not 0.5 <= duration <= 30:
                raise web.HTTPBadRequest(text="Duration must be between 0.5 and 30 seconds.")
            return await upstream.handle_submit(request)

    async def health(request):
        return web.json_response({"status": "ok", "authentication": "required"})

    async def gallery(request):
        if upstream is None:
            raise web.HTTPConflict(text="WAN preparation is not complete. Complete Prepare and restart Portal to load the video library.")
        return await upstream.handle_gallery(request)

    async def job_status(request):
        if upstream is None:
            raise web.HTTPConflict(text="Complete Prepare and restart Portal before inspecting generation jobs.")
        return await upstream.handle_job_status(request)

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
    app.router.add_get("/api/jobs/{job_name}", job_status)
    app.router.add_get("/api/gallery", gallery)
    app.router.add_static("/web", WEB_ROOT, show_index=False)
    return app
