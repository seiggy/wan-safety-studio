# Prepare and run the local dashboard

Run these commands in PowerShell 7.4+ from the repository root. No personal profile or environment-switching module is required.

## 1. Select the customer environment and connect privately

```powershell
$config = Get-Content .\infra\terraform.tfvars.json -Raw | ConvertFrom-Json
az login --tenant $config.tenant_id
az account set --subscription $config.subscription_id
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Status
```

Connect the workstation to the customer's approved VPN/private network first. Storage, ACR, Key Vault and the workspace's **client-visible** hostnames must resolve to private IPs and accept TCP 443. A connected VPN icon alone is insufficient if its DNS-forwarder/gateway VM is stopped.

Private endpoint zone records can contain `privatelink` aliases and wildcard model/inference zones. The operator probes their concrete client-visible names, deduplicates aliases, and skips wildcard records; it does not require broad DNS overrides for internal CNAME targets. Do not change global workstation DNS or reopen Azure public endpoints to bypass a failed check.

## 2. Prepare the pinned WAN assets on CPU

```powershell
# Optional: only when your organization approves a credential-free HTTPS package mirror.
# Use the same override for Prepare, Portal, Start and Submit.
# $env:WAN_STUDIO_PYPI_INDEX = 'https://<approved-index-host>/pypi/simple'

.\scripts\Invoke-WanSafetyStudio.ps1 -Action Prepare -Profile wan
```

To reuse existing model files, add `-LocalModelsPath '<absolute-model-directory>'`. That directory must contain the model subfolders listed in `app\wan-pinned-manifest.json`. Every file is checked against its pinned size and SHA256 before reuse; models are not assumed trustworthy because they are cached.

Prepare builds/tests a Linux image on local CPU, pushes it to private ACR and registers immutable Azure ML model/code/environment assets. Keep Docker running with Linux containers and sufficient disk space. The four WAN weights total about 33.1 GiB. Prepare does not create GPU compute.

MSAL 1.38.0 was already resolved in the public lock through Azure Identity; the dashboard declares it directly without changing its artifacts or hashes. Mirror installs use the public lock's exact versions/hashes. Do not disable TLS verification. If public artifact TLS fails, obtain an approved mirror from the customer rather than committing an organization-specific feed as the default.

## 3. Initialize Entra authentication once

```powershell
.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges
```

Required authority: the configured operator's interactive Azure CLI login; Entra permissions to create/manage this application and group and assign its app role; Azure permission to create a vault-scoped role assignment; private Key Vault connectivity. Depending on the tenant, the identity owner may need to run/approve this step. Group-based enterprise-application assignment may require Entra ID P1/P2. Do not grant tenant-wide permissions merely to make the script pass.

The script:

1. Verifies the selected tenant/subscription, owned foundation, and signed-in operator.
2. Creates the single-tenant `WAN Safety Studio` application with `http://localhost:51881/auth/callback` and a `VideoCreator` role.
3. Creates `WAN Safety Studio Creators`, adds the operator as its initial direct member, and assigns that group to the enterprise app's role. Assignment is required for sign-in.
4. Grants the operator Key Vault Secrets Officer at the deployed vault only. This supports local credential creation/rotation/read; it is not a subscription-wide grant.
5. Creates a 90-day application secret, writes it directly to Key Vault as `wan-studio-msal`, and writes only non-secret metadata to `<operator-cache>\portal-auth.json`.

The app/group/role assignment and secret are managed by this script and the customer's identity owner, not by the foundation Terraform. Do not import or destroy them as a side effect of Terraform changes. A conflicting existing name, deployment tag, role or secret causes an error rather than silently adopting unrelated identity resources.

### If Microsoft sign-in says "Need admin approval"

App creation, group assignment, Azure subscription Owner and an account named "admin" do not by themselves grant application consent. The script always declares Microsoft Graph's OpenID Connect `openid` and `profile` delegated scopes on the registration (`requiredResourceAccess`). Because the enterprise application requires assignment (only the creator group may sign in), Entra never allows user self-consent for it, so an authorized Entra administrator must grant consent once per tenant, regardless of the tenant's user-consent setting:

```powershell
.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges -ApproveAdminConsent
```

The switch grants exactly the declared scopes tenant-wide using `oauth2PermissionGrants`. It refuses to overwrite unrelated additional permissions/consent. MSAL excludes `offline_access`, because this local portal does not need refresh tokens. No directory-reading or Azure resource permission is granted. Tenant-wide consent does not make the portal tenant-wide: enterprise-application assignment and the `VideoCreator` role are still required.

This action needs an **active** Entra role of Cloud Application Administrator, Application Administrator or Privileged Role Administrator (activate eligible roles in PIM first; Global Reader and Azure Owner are not enough). If the configured operator does not have one, provide the app/client ID from `portal-auth.json` to the identity admin to approve only the two sign-in permissions in **Entra admin center > Enterprise applications > WAN Safety Studio > Permissions**. Do not use broad directory permissions or disable assignment as a workaround. After consent, start a fresh sign-in from the dashboard.

Rerunning the script reuses matching objects and a valid existing secret. For planned rotation:

```powershell
.\scripts\Initialize-PortalAuth.ps1 -ApproveIdentityChanges -RotateSecret
```

Restart the local Portal process after rotation. The old script-owned credential is revoked after the replacement is saved. If a new credential cannot be saved to Key Vault, the script revokes that new credential and fails. Keep error details, correct the reported permissions/connectivity issue and rerun with the same customer configuration; do not delete the app or group to recover.

Additional creators must be direct members of the creator group. Group access is represented by the `VideoCreator` app-role claim; the dashboard does not enumerate the tenant or request Graph directory-reading permissions during sign-in. The sign-in role is separate from backend Azure RBAC: during local development, the backend still uses the configured Azure CLI operator's identity and existing submission guards. Opening a job directly in Azure ML Studio requires that user's own Azure RBAC; group membership alone does not grant direct Azure access.

## 4. Start the dashboard without arming the GPU

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Portal -Profile wan
```

Open **http://localhost:51881/** and choose **Sign in with Microsoft**. Use the configured tenant and an approved creator account. Keep this terminal open; Ctrl+C stops only the local UI.

This first-pass authenticated UI supports the validated `wan` profile. Experimental LTX/H3 preparation paths remain available, but Start requires `-NoPortal` for those profiles; they are not represented as tested customer UI workflows.

Use `localhost`, not `127.0.0.1`, for browser sign-in so the callback and session-cookie host match. The listener still binds only to `127.0.0.1`. Health checks do not authenticate a user or prove Azure readiness. API calls require an authenticated creator session; modifying calls also require the session's CSRF token and same-origin requests.

The UI shows real prepared-asset, compute and connection state. Once its locked Python dependencies and authentication are initialized, Portal can also be opened while Prepare is still running: it shows "Not prepared" and disables generation/library access instead of inventing asset references or empty results. Restart Portal after Prepare completes to load the verified release. An empty library after preparation means no completed videos exist in this workspace.

Starting Portal does not write an `armed.json` gate, create compute, approve spend, or bypass missing egress. To generate later, complete the NSG/egress prerequisites and run the documented Start action with `-NoPortal` in another terminal and the required explicit approvals.

**Not configured is not scale-to-zero.** The initial compute-disabled Deploy leaves the AML compute resource absent. An approved compute-enabled Deploy can create the min-zero/max-one target without arming submission. Start verifies the release and arms that target, creating it if needed; neither operation warms up a GPU. The configured, armed cluster accepts jobs even with zero allocated nodes, and Azure ML allocates a Spot node when a job is queued. Stop deletes the compute target as well as owned temporary egress. The UI must not require a running GPU before allowing submission; it requires a prepared release, a successfully configured target and the operator's submission gate.

Changing approved network configuration invalidates the prepared scope receipt; rerun Prepare on the idle, unarmed target. Checksummed model files are reused in the selected private container even after a network-only change; code/environment registrations and the receipt are reverified. Do not edit or spoof an old preparation receipt to bypass those checks.

### "The GPU cluster is configured, but submission is not armed"

This is a separate operator approval, not a requirement to keep GPU nodes running. Once the release and customer NSG/egress are ready, use a second terminal with the same Azure login and package-mirror setting used by Prepare:

```powershell
.\scripts\Invoke-WanSafetyStudio.ps1 -Action Start -Profile wan -NoPortal `
    -ApproveGpuSpend -ApproveCustomerNsgRules
```

For a pre-existing target this performs read-only cloud/asset/price/quota checks, validates the NAT links and declared IP tags, and writes the local gate. It does not apply Terraform, queue a job or allocate a GPU. In the dashboard, select **Environment > Check connections**, or wait for its periodic refresh. Generate becomes available when the configured target and loaded prepared version match the operator's gate. Do not create `armed.json` manually.

### Recovering an interrupted infrastructure apply

Stop and have the infrastructure operator establish final live state before any retry: terminating Terraform does not cancel an ARM operation already accepted. Confirm the compute/NAT/public IP/subnet states, inspect active jobs, and verify that no matching Terraform process is still running. Keep submission blocked during recovery and back up `infra\terraform.tfstate`.

If customer policy added an IP tag, record it in `egress_public_ip_tags`; do not remove the policy value or ignore all public-IP changes. A later reviewed plan must not replace the public IP just to arm an existing cluster.

If the live owned NAT/public-IP link exists but an interrupted apply omitted only its Terraform association entry, confirm both exact IDs/ownership and import that one association (do not import the customer NSG/subnet):

```powershell
# Obtain these IDs from the selected foundation.json / Terraform studio output.
terraform '-chdir=infra' import '-var-file=terraform.tfvars.json' `
    'azurerm_nat_gateway_public_ip_association.compute[0]' '<owned-nat-id>|<owned-public-ip-id>'
```

Use the normal sanitized operator environment described in the README when running Terraform with Azure credentials. A stale state lock may be cleared only after the original Terraform process is gone and ARM operations are terminal; do not disable locking or force-unlock an active deployment. Import/refresh is state reconciliation, not permission to apply unrelated changes. Review a fresh plan for deletions/replacements before proceeding, then rerun Prepare if the approved configuration or runtime changed.

Run lifecycle/Terraform commands serially against one checkout and state file. On Windows, even a read-only plan can temporarily block Prepare's Terraform-output read. Wait for the first command to finish, then rerun the failed command; do not disable state locking.

## 5. Create videos

The same form is used locally and on the [hosted portal](app-service.md).

| Option | Values | Notes |
| --- | --- | --- |
| Scenes | 1–5 | Each scene has a description and an optional reference image. Scene 1's image is required. Scenes without an image reuse Scene 1's image. |
| Aspect ratio | 16:9 (1024 × 576), 4:3 (896 × 672), 1:1 (768 × 768) | WAN center-crops and scales the reference image to this size, so use images of the same shape. All three sizes have about the same pixel count as the validated 768 × 768 run, so runtime and GPU memory stay close to it. |
| Duration | 5–10 seconds | 16 fps (81–161 frames). A 5-second clip took about 17 minutes on one Spot A100. A 10-second clip takes roughly twice as long. |
| Videos per scene | 1, 3 or 5 | Each video is a separate GPU job with its own random seed. Use several takes when you want to choose the best one. |

The form shows the total before you submit: scenes × videos per scene = GPU jobs. Every job is billed separately. The cluster has one node, so jobs run one after another. For example, 5 scenes × 5 videos is 25 jobs, or about 7 GPU-hours at 5 seconds each.

**Why the 10-second cap.** WAN 2.2 is tuned for about 5 seconds at 16 fps. Longer single passes drift from the reference image, and memory and runtime grow with frame count. A 30-second pass would likely exceed the A100's memory or the 2-hour job timeout. For longer material, create several scenes and edit the clips together.

**How submission works.**

- The server submits jobs to Azure ML in the background, one at a time. You can leave or reload the page, and the job list resumes in the same browser tab. The list also shows every unfinished job in the workspace's WAN experiment, read from Azure ML (`GET /api/jobs`), so earlier requests and other creators' queued jobs stay visible after a new submission. Finished videos are in the video library.
- Only the creator who submitted a batch can see its status. Everyone's completed videos appear in the video library.
- Only one batch is submitted at a time, so a second creator must wait for the first creator's job IDs.
- Submission stops, without retrying, if the operator disarms generation or a job cannot be created. Jobs that were already created keep running. The rest of the batch is never submitted.
- Restarting the portal (Ctrl+C locally, or hosted Publish/Start/Stop) ends an unfinished submission in the same way. Check the video library before submitting again.

## Local development boundary

MSAL handles the authorization-code flow and nonce/state checks. Tokens and client secrets stay server-side; the browser receives an opaque HttpOnly session cookie. Sessions expire and are kept only in this process's memory. Restarting the server signs users out. The [private App Service host](app-service.md) keeps the same one-process session model on one instance, but uses HTTPS/Secure cookies, a federated credential instead of the client secret, and its managed identity instead of Azure CLI credentials. Scale-out would still need an approved shared session store. There is no authentication bypass mode.

With the locked project Python environment installed, run `.\app\.venv\Scripts\python.exe .\app\test_portal.py` for the offline auth/role/CSRF/submission-gate and batch-validation check. It uses fake identity and Azure clients, not a runtime authentication bypass. The framework-free `-Action Check` suite continues to cover operator controls and the pinned upstream patch separately.
