"""Run with the locked project Python; all identity and Azure calls are fakes."""
import json
from pathlib import Path
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

from aiohttp import web
from aiohttp.test_utils import TestClient, TestServer
from azure.core.exceptions import ResourceNotFoundError

import portal


class PortalChecks(unittest.IsolatedAsyncioTestCase):
    async def test_auth_role_csrf_gate_and_real_status_contract(self):
        tenant = "11111111-1111-1111-1111-111111111111"
        client_id = "22222222-2222-2222-2222-222222222222"
        self.claims = {"tid": tenant, "aud": client_id, "oid": "33333333-3333-3333-3333-333333333333",
                       "roles": ["VideoCreator"], "name": "Fixture creator", "exp": int(time.time()) + 600}
        result = {"id_token_claims": self.claims}
        identity = Mock()
        identity.initiate_auth_code_flow.return_value = {
            "auth_uri": f"https://login.microsoftonline.com/{tenant}/authorize?state=fixture",
            "state": "fixture", "nonce": "fixture-nonce",
        }

        def exchange(flow, response):
            if flow["state"] != response.get("state"):
                raise ValueError("state mismatch")
            return result

        identity.acquire_token_by_auth_code_flow.side_effect = exchange
        settings = SimpleNamespace(
            compute="wan-gpu", workspace_name="fixture-workspace", resource_group="fixture-group",
            storage_account="fixturestorage", storage_container="videos", max_upload_mb=1,
            profiles={"wan": SimpleNamespace(label="WAN", fps=16, width=512, height=512,
                                            duration_seconds=1, duration_step_seconds=0.5)},
        )
        submitted = []

        async def submit(request):
            submitted.append(await request.post())
            return web.json_response({"job": {"name": "fixture-job", "status": "Queued"}})

        async def read(request):
            return web.json_response({"items": [], "count": 0, "scanned_jobs": 0})

        blob = Mock()
        blob.__enter__ = Mock(return_value=blob)
        blob.__exit__ = Mock(return_value=False)
        client = Mock()
        client.compute.get.side_effect = ResourceNotFoundError("compute is off")
        upstream = SimpleNamespace(
            handle_submit=submit, handle_gallery=read, handle_job_status=read,
            workspace_ml_client=Mock(return_value=client), make_blob_service_client=Mock(return_value=blob),
        )
        manifest = {"profile": "wan", "version": "fixture-version"}
        with tempfile.TemporaryDirectory() as temporary, patch.object(portal.msal, "ConfidentialClientApplication", return_value=identity) as create_identity:
            cache = Path(temporary)
            app = portal.create_app(settings, manifest, cache, {"tenantId": tenant, "clientId": client_id},
                                    "test-only-not-a-real-secret", upstream)
            self.assertEqual(create_identity.call_args.kwargs["exclude_scopes"], ["offline_access"])
            async with TestClient(TestServer(app)) as browser:
                base_headers = {"Host": "localhost:51881"}

                async def get(path, cookies=None):
                    return await browser.get(path, headers=base_headers, cookies=cookies or {}, allow_redirects=False)

                async def signin():
                    begin = await get("/auth/login")
                    self.assertEqual(begin.status, 302)
                    self.assertTrue(begin.cookies["wan_login"]["httponly"])
                    ticket = begin.cookies["wan_login"].value
                    return await get("/auth/callback?state=fixture&code=fixture", {"wan_login": ticket})

                self.assertEqual((await get("/healthz")).status, 200)
                self.assertEqual((await get("/api/status")).status, 401)
                bad_host = await browser.get("/healthz", headers={"Host": "attacker.example"})
                self.assertEqual(bad_host.status, 403)
                unauthenticated = await browser.post("/api/submit", headers=base_headers)
                self.assertEqual(unauthenticated.status, 401)
                self.assertEqual(submitted, [])

                begin = await get("/auth/login")
                ticket = begin.cookies["wan_login"].value
                bad = await get("/auth/callback?state=wrong&code=x", {"wan_login": ticket})
                self.assertEqual(bad.headers["Location"], "/?signin=failed")
                replay = await get("/auth/callback?state=fixture&code=x", {"wan_login": ticket})
                self.assertEqual(replay.headers["Location"], "/?signin=failed")

                for change in ({"roles": []}, {"roles": "VideoCreator"}, {"tid": client_id}, {"aud": tenant}):
                    result["id_token_claims"] = {**self.claims, **change}
                    denied = await signin()
                    self.assertEqual(denied.headers["Location"], "/?signin=forbidden")
                    self.assertNotIn("wan_session", denied.cookies)
                result["id_token_claims"] = {**self.claims, "exp": 1}
                expired = await signin()
                self.assertEqual(expired.headers["Location"], "/?signin=failed")
                result["id_token_claims"] = self.claims
                signed_in = await signin()
                sid = signed_in.cookies["wan_session"].value
                cookies = {"wan_session": sid}
                self.assertTrue(signed_in.cookies["wan_session"]["httponly"])
                self.assertEqual(signed_in.cookies["wan_session"]["samesite"], "Lax")
                user = await (await get("/api/session", cookies)).json()
                self.assertNotIn("access_token", user)
                state = await (await get("/api/status", cookies)).json()
                self.assertEqual(state["computeState"], "Not configured")
                self.assertFalse(state["generationEnabled"])
                blob.get_container_client.return_value.get_container_properties.assert_called_once()
                self.assertEqual((await get("/api/gallery", cookies)).status, 200)
                no_csrf = await browser.post("/api/submit", cookies=cookies, headers=base_headers)
                self.assertEqual(no_csrf.status, 403)
                headers = {**base_headers, "Origin": portal.ORIGIN, "X-CSRF-Token": user["csrfToken"]}
                unarmed = await browser.post("/api/submit", cookies=cookies, headers=headers)
                self.assertEqual(unarmed.status, 409)
                self.assertEqual(submitted, [])
                (cache / "armed.json").write_text(json.dumps({"compute": "wan-gpu", "profile": "wan", "version": "old"}))
                stale = await browser.post("/api/submit", cookies=cookies, headers=headers)
                self.assertEqual(stale.status, 409)
                (cache / "armed.json").write_text(json.dumps({"compute": "wan-gpu", "profile": "wan", "version": "fixture-version"}))
                client.compute.get.side_effect = None
                client.compute.get.return_value = SimpleNamespace(provisioning_state="Succeeded", current_node_count=0)
                idle = await (await get("/api/status", cookies)).json()
                self.assertTrue(idle["generationEnabled"], "A configured, armed cluster must accept jobs with zero allocated nodes.")
                for duration in ("NaN", "Infinity", "31", "0", "bad"):
                    invalid = await browser.post("/api/submit", cookies=cookies, headers=headers,
                                                 data={"prompt": "scene", "duration_seconds": duration})
                    self.assertEqual(invalid.status, 400)
                foreign = await browser.post("/api/submit", cookies=cookies,
                    headers={**headers, "Origin": "https://attacker.example"},
                    data={"prompt": "scene", "duration_seconds": "1"})
                self.assertEqual(foreign.status, 403)
                accepted = await browser.post("/api/submit", cookies=cookies, headers=headers,
                    data={"prompt": "scene", "negative_prompt": "", "duration_seconds": "1"})
                self.assertEqual(accepted.status, 200)
                self.assertEqual(submitted[0]["negative_prompt"], "")
                logout = await browser.post("/auth/logout", cookies=cookies, headers=headers)
                self.assertEqual(logout.status, 200)
                self.assertEqual((await get("/api/session", cookies)).status, 401)
                self.assertTrue((cache / "armed.json").exists(), "Logout must not change the operator's gate.")

    async def test_hosted_host_cookie_and_assertion_credential(self):
        tenant, client_id = "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"
        host = "app-fixture.azurewebsites.net"
        identity = Mock()
        identity.initiate_auth_code_flow.return_value = {"auth_uri": "https://login.microsoftonline.com/x", "state": "s"}
        identity.acquire_token_by_auth_code_flow.return_value = {"id_token_claims": {
            "tid": tenant, "aud": client_id, "oid": "33333333-3333-3333-3333-333333333333",
            "roles": ["VideoCreator"], "exp": int(time.time()) + 600}}
        settings = SimpleNamespace(compute="wan-gpu", workspace_name="w", resource_group="g",
                                   storage_account="fixturestorage", storage_container="videos", max_upload_mb=1)
        assertion = {"client_assertion": lambda: "fixture-assertion"}
        with tempfile.TemporaryDirectory() as temporary, \
                patch.multiple(portal, HOSTED=True, HOST=host, ORIGIN="https://" + host,
                               REDIRECT=f"https://{host}/auth/callback"), \
                patch.object(portal.msal, "ConfidentialClientApplication", return_value=identity) as create_identity:
            app = portal.create_app(settings, None, Path(temporary), {"tenantId": tenant, "clientId": client_id},
                                    assertion, None)
            self.assertIs(create_identity.call_args.kwargs["client_credential"], assertion)
            async with TestClient(TestServer(app)) as browser:
                site = {"Host": host}
                self.assertEqual((await browser.get("/healthz", headers={"Host": "10.0.0.4:8000"})).status, 200)
                self.assertEqual((await browser.get("/", headers={"Host": "localhost:51881"})).status, 403)
                self.assertEqual((await browser.get("/api/status", headers=site)).status, 401)
                begin = await browser.get("/auth/login", headers=site, allow_redirects=False)
                self.assertEqual(identity.initiate_auth_code_flow.call_args.kwargs["redirect_uri"], f"https://{host}/auth/callback")
                done = await browser.get("/auth/callback?state=s&code=c", headers=site, allow_redirects=False,
                                         cookies={"wan_login": begin.cookies["wan_login"].value})
                self.assertTrue(done.cookies["wan_session"]["secure"])


if __name__ == "__main__":
    unittest.main()
