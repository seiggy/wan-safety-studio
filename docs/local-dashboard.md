# Run the studio locally and create videos

There is no Python portal any more. The retired `scripts\Invoke-WanSafetyStudio.ps1 -Action Portal` flow, which generated server settings from an operator cache and an Entra application created by `Initialize-PortalAuth.ps1`, no longer exists. This page keeps its path so existing links work.

## Run the .NET server locally

Local use is the .NET server under Aspire:

```powershell
aspire start --non-interactive
# UI: http://localhost:51881   server: http://localhost:51882
```

Local development runs with sign-in off (`Auth:Mode` defaults to `None` in Development) and, under Aspire, generates through ComfyUI (`Generation__Provider=comfyui`). Storage, ComfyUI, models and the git-ignored `appsettings.Development.json` are covered in [local generation](local-generation.md). Settings are in the [configuration reference](configuration.md).

Authenticated local sign-in is not a supported flow and is unverified. The server can be configured for it (`Auth__Mode=Entra`, `Auth__ClientId`, `Auth__PublicOrigin`, a tenant and a client credential; see `src\VideoSafetyStudio.Server\Auth\AuthSettings.cs`), but the [Entra bootstrap runbook](operations/entra-bootstrap.md) creates no client secret and registers only the hosted redirect URI unless you add a localhost one yourself. Use the hosted studio ([private App Service](app-service.md)) when sign-in is required.

Running the Azure ML provider against a real workspace from a developer machine is possible by setting `Generation:Provider=azureml` and the `AzureMl:*` settings in the AppHost configuration. It is outside any documented, tested flow, and submissions on a hosted deployment are governed by the durable fence in [hosted lifecycle](operations/hosted-lifecycle.md).

## 5. Create videos

The same form is used locally and on the [hosted studio](app-service.md).

| Option | Values | Notes |
| --- | --- | --- |
| Scenes | 1–5 | Each scene has a description and an optional reference image. Scene 1's image is required. Scenes without an image reuse Scene 1's image. |
| Aspect ratio | 16:9 (1024 × 576), 4:3 (896 × 672), 1:1 (768 × 768) | WAN center-crops and scales the reference image to this size, so use images of the same shape. All three sizes have about the same pixel count as the validated 768 × 768 run, so runtime and GPU memory stay close to it. |
| Duration | 5–10 seconds | 16 fps (81–161 frames). A 5-second clip took about 17 minutes on one Spot A100. A 10-second clip takes roughly twice as long. |
| Videos per scene | 1, 3 or 5 | Each video is a separate GPU job with its own random seed. Use several takes when you want to choose the best one. |

The form shows the total before you submit: scenes × videos per scene = GPU jobs. Every job is billed separately. The cluster has one node, so jobs run one after another. For example, 5 scenes × 5 videos is 25 jobs, or about 7 GPU-hours at 5 seconds each.

**Why the 10-second cap.** WAN 2.2 is tuned for about 5 seconds at 16 fps. Longer single passes drift from the reference image, and memory and runtime grow with frame count. A 30-second pass would likely exceed the A100's memory or the per-job timeout (at most 120 minutes). For longer material, create several scenes and edit the clips together.

**How submission works.**

- The server submits jobs to Azure ML in the background, one at a time. You can leave or reload the page, and the job list resumes in the same browser tab. The list also shows every unfinished job in the workspace's WAN experiment, read from Azure ML (`GET /api/jobs`), so earlier requests and other creators' queued jobs stay visible after a new submission. Finished videos are in the video library.
- Only the creator who submitted a batch can see its status. Everyone's completed videos appear in the video library.
- Only one batch is submitted at a time, so a second creator must wait for the first creator's job IDs.
- Submission stops, without retrying, if the region is disarmed (Stop) or a job cannot be created. Jobs that were already created keep running. The rest of the batch is never submitted.
- Restarting the server, or a hosted Stop, ends an unfinished submission in the same way. Check the video library before submitting again.

## Sign-in model on the hosted site

The server handles the Entra authorization-code flow, nonce and state checks. Tokens stay server-side; the browser receives an opaque HttpOnly session cookie. Sessions expire after one hour and are kept in process memory, so a restart signs users out and the [private App Service host](app-service.md) stays on one instance. The hosted site uses HTTPS/Secure cookies, a federated credential instead of a client secret, and its managed identity for Azure calls. On a hosted site `Auth__Mode=None` is an explicit opt-out for deployments where the network or platform already gates access ([container guide](app-service-docker.md#disabling-sign-in)).

The server's tests (`dotnet test tests\VideoSafetyStudio.Tests --artifacts-path .artifacts`) cover sign-in, role, CSRF and the submission gate with fake identity and Azure clients.
