"""Deploy a previously verified server artifact by digest. Never builds or arms submissions."""
import argparse
from pathlib import Path
import re
import time
import urllib.request

from pipeline import Azure, contracts, load, private_host, promote_image, require


def deploy(settings, artifact, region_key):
    checked = contracts(settings)
    receipt = checked["foundation"]
    require(region_key == receipt["controlRegion"], "The shared server belongs to control_region.")
    site_id = load(settings).get("Pipeline", {}).get("SiteId")
    require(site_id, "No shared App Service (Pipeline.SiteId) was explicitly selected.")
    azure = Azure(receipt["scope"])
    region = receipt["regions"][region_key]
    catalog = azure.container(region).get_blob_client(f"images/server/{artifact}.json").download_blob().readall()
    import hashlib
    import json
    record = json.loads(catalog)
    from pipeline import json_bytes
    require(set(record) == {"schemaVersion", "component", "sourceSha", "imageDigest"} and
            type(record["schemaVersion"]) is int and hashlib.sha256(json_bytes(record)).hexdigest() == artifact and record["schemaVersion"] == 1 and
            record["component"] == "server" and re.fullmatch(r"[a-f0-9]{40}", record["sourceSha"]),
            "Server catalog identity differs.")
    image = promote_image(azure, region, region, "wan/server", record["imageDigest"])
    name = site_id.rsplit("/", 1)[1]
    site = azure.owned(site_id, region["ownershipTags"], "2023-12-01")
    require(site["properties"]["publicNetworkAccess"] == "Disabled" and site["properties"]["httpsOnly"],
            "App Service is not private/HTTPS-only.")
    host = site["properties"]["defaultHostName"]
    require(re.fullmatch(re.escape(name) + r"(?:-[a-z0-9]+)?(?:\.[a-z0-9-]+)*\.azurewebsites\.net", host),
            "Unexpected App Service hostname.")
    private_host(host)
    settings_id = site_id + "/config/appsettings"
    app_settings = azure.arm(settings_id + "/list", "2023-12-01", method="POST")["properties"]
    require(app_settings.get("AzureMl__Armed") == "false", "Disarm the shared server before deploying.")
    require(app_settings.get("Auth__ClientId") and app_settings.get("Auth__TenantId") == receipt["scope"]["tenantId"],
            "Initialize and approve Entra authentication separately before deployment.")
    web_id = site_id + "/config/web"
    web = azure.arm(web_id, "2023-12-01")
    identity = site.get("identity", {}).get("userAssignedIdentities", {})
    pull_client = str(web["properties"].get("acrUserManagedIdentityID", "")).lower()
    pull = [key for key, value in identity.items() if pull_client and str(value.get("clientId", "")).lower() == pull_client]
    require(web["properties"].get("acrUseManagedIdentityCreds") is True and len(pull) == 1 and
            pull[0].lower().startswith(f"/subscriptions/{receipt['scope']['subscriptionId']}/") and
            "/providers/microsoft.managedidentity/userassignedidentities/" in pull[0].lower(),
            "App Service registry pull identity is not exactly one assigned user-assigned identity.")
    azure.arm(web_id, "2023-12-01", method="PATCH", body={"properties": {"linuxFxVersion": "DOCKER|" + image}})
    require(azure.arm(web_id, "2023-12-01")["properties"]["linuxFxVersion"] == "DOCKER|" + image,
            "App Service image read-back differs.")
    azure.arm(site_id + "/start", "2023-12-01", method="POST")
    deadline = time.monotonic() + 600
    while True:
        try:
            with urllib.request.urlopen(f"https://{host}/healthz", timeout=15) as response:
                if response.status == 200:
                    break
        except (urllib.error.URLError, TimeoutError):
            if time.monotonic() >= deadline:
                raise RuntimeError("Deployment did not become healthy; submissions remain disarmed.") from None
            time.sleep(5)
    try:
        with urllib.request.urlopen(f"https://{host}/api/status", timeout=15) as response:
            status = response.status
    except urllib.error.HTTPError as error:
        status = error.code
    if status != 401:
        azure.arm(site_id + "/stop", "2023-12-01", method="POST")
        require(azure.arm(site_id, "2023-12-01")["properties"]["state"] == "Stopped",
                "API authentication failed and stopping the server was not verified.")
        raise RuntimeError("API did not require sign-in. Server stopped; investigate before redeployment.")
    print("Approved server digest deployed; submissions remain disarmed. No generation or training.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--region", required=True)
    args = parser.parse_args()
    require(re.fullmatch(r"[a-f0-9]{64}", args.artifact), "Invalid immutable server identity.")
    deploy(args.settings, args.artifact, args.region)
