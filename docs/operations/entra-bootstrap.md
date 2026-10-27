# Entra sign-in bootstrap (manual runbook)

One-time, privileged identity work for a hosted studio. It replaces the retired `scripts\Initialize-PortalAuth.ps1`, which
needed tenant rights and was never something CI or the web host should hold. An **identity owner** performs these steps in the
customer tenant; nothing here is automated and the repository never receives tenant-admin rights. Record the resulting
client ID and nothing else (no secret is created).

## What to create

| Item | Setting |
| --- | --- |
| App registration | Display name `WAN Safety Studio`, **single tenant** (`AzureADMyOrg`), tags `wan-safety-studio` and `deployment:<deployment_name>` |
| Redirect URI | `https://<site host>/auth/callback` (web platform). Add `http://localhost:<port>/auth/callback` only for a developer machine that signs in |
| API permissions | Microsoft Graph delegated `openid` and `profile` only. Nothing else |
| App roles | `VideoCreator` (member type User; creates videos) and `StudioAdmin` (member type User; Start/Stop, release selection and settings). Both enabled |
| Enterprise application | Service principal for the registration with **Assignment required = Yes** |
| Creators group | Security group `WAN Safety Studio Creators`, assigned the `VideoCreator` role. Create a separate administrators group for `StudioAdmin` and keep it small |
| Federated credential | On the registration: issuer `https://login.microsoftonline.com/<tenant id>/v2.0`, subject = the **object (principal) ID of the site's user-assigned managed identity**, audience `api://AzureADTokenExchange`. The site signs in with this assertion, so no client secret exists |
| Admin consent | Tenant-wide consent for `openid` and `profile` only. Needs an active Cloud Application Administrator, Application Administrator, Privileged Role Administrator or Global Administrator role |

The site accepts a token only from the configured tenant, for this client ID, carrying `VideoCreator` or `StudioAdmin`.
Assigning a person to the group is the access decision; an administrator who should not create videos gets only `StudioAdmin`.

## Steps

1. Sign in as the identity owner (`az login`, or the Entra portal). Confirm the tenant equals `tenant_id` in your deployment configuration.
2. Create (or find) the app registration. If one with the display name exists, confirm it is single tenant, carries both tags and
   declares no other permissions; stop and review with its owner if not.
3. Declare the two delegated Microsoft Graph scopes `openid` and `profile` on the registration, and add both app roles. Keep the
   role IDs; they are fixed once created.
4. Add the hosted redirect URI and the federated credential above. The managed identity's principal ID is the Terraform output
   `identityPrincipalId` for the host (the client ID of the same identity is `AZURE_CLIENT_ID` on the site).
5. Create the enterprise application if it is missing and set **Assignment required**.
6. Create the groups, add their members, and assign each group to its app role on the enterprise application.
7. Grant admin consent for `openid` and `profile`. If the tenant already holds a consent grant for this application with any other
   scope, stop: do not edit it automatically; have the identity owner review it.
8. Give the deployment the registration's **Application (client) ID** as `Auth__ClientId` (with `Auth__PublicOrigin`, the site's
   HTTPS origin). Both are ordinary, non-secret app settings. `AzureMl__TenantId` is the tenant check.

## Verify

- Sign in as a creator: the studio opens. Sign in as a member of neither group: the sign-in is refused. An administrator without
  `VideoCreator` sees only Administration.
- `GET /api/session` for each account reports `isCreator` and `isAdmin` that match the roles assigned.
- No client secret exists on the registration. If one was created for an earlier version, remove it.

## Rotation and removal

- There is no secret to rotate. If the site's managed identity is replaced, update the federated credential's subject.
- To remove access, remove the person from the group. To retire the deployment, delete the groups, then the registration.

Never put tenant-administrator credentials in a workflow secret, an app setting or a configuration file.
