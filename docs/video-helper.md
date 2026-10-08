# Video Production Helper

An AI helper on the **Create** page (and only there) that writes and improves Wan 2.2 prompts and can operate the form for you: edit positive and negative prompts, add or remove scenes, and set aspect ratio, duration, takes and quality.

## How it is built

```
React (Streamdown chat)  --AG-UI over SSE-->  POST /api/agent  (Microsoft Agent Framework, MapAGUIServer)
                                                   |
                                          VideoHelperAgent  (GitHub Copilot harness, per-run session)
                                                   |  BYOK, OpenAI v1 wire protocol
                                          Foundry deployment or any OpenAI-compatible server
```

- **Always bring your own key.** The helper never uses GitHub Copilot's hosted models or a GitHub login. The harness only supplies the agent loop and tool calling; every token goes to the endpoint you configure.
- **It can see your images.** Each scene's reference image is downscaled in the browser (1024 px JPEG) and sent once, with the next message, as an AG-UI image part; the harness forwards it to the model as an `image_url`. Nothing is stored. Turn this off with `Agent:Vision=false`.
- **Locked down.** Each run offers the model only the eight form tools (`VideoHelperTools.cs`). The harness's built-in shell, file and web tools are not available and permission requests for anything else are rejected.
- **The browser owns the form.** Tool calls stream to the page as AG-UI `TOOL_CALL_*` events and the page applies them (`helper/formTools.ts`, which enforces the same limits as the form). Each message carries the live form as a `<form_state>` block, so the model never works from a stale copy. The server keeps no conversation state.
- The harness runs the Copilot CLI that the SDK downloads at build time (through the package feed set by `CopilotNpmRegistryUrl` in `VideoSafetyStudio.Infrastructure.csproj`).

## Configure

Every setting is bound from the `Agent` configuration section, so each one is also an environment variable with `__` for `:` (`Agent:Endpoint` is `Agent__Endpoint`). Under Aspire, put them in `src\VideoSafetyStudio.AppHost\appsettings.Development.json` (git-ignored) and restart the AppHost once; it passes them to the server.

| Setting | Env variable | Meaning |
|---|---|---|
| `Agent:Endpoint` | `Agent__Endpoint` | OpenAI v1 base URL. Empty keeps the helper off (the Create page then shows what is missing). |
| `Agent:Model` | `Agent__Model` | Model name, or the Foundry **deployment** name. Should be multimodal and support tool calling. |
| `Agent:Auth` | `Agent__Auth` | `ApiKey`, `AzureCli`, `SystemAssignedManagedIdentity` or `UserAssignedManagedIdentity` (below). |
| `Agent:ApiKey` | `Agent__ApiKey` | Key for `ApiKey` auth. Under Aspire this is the secret parameter `agent-api-key`. |
| `Agent:ManagedIdentityClientId` | `Agent__ManagedIdentityClientId` | Client ID for `UserAssignedManagedIdentity`. |
| `Agent:WireApi` | `Agent__WireApi` | `completions` or `responses`. Default: `responses` for `*.azure.com` endpoints, `completions` otherwise. Foundry reasoning models (for example gpt-5.x) reject function tools on `completions`, so use `responses` there. |
| `Agent:Vision` | `Agent__Vision` | `true` (default) lets the helper see scene reference images; `false` for text-only models. |

If `Agent:Auth` is unset: an `*.azure.com` endpoint uses `AzureCli` (unless a key is present), anything else uses `ApiKey`. A missing or inconsistent setting is reported in the Create page's helper panel and in `GET /api/agent/status`.

### Authentication methods

All token-based methods request `https://cognitiveservices.azure.com/.default` and cache and refresh tokens automatically.

| `Agent:Auth` | Use it for | Needs |
|---|---|---|
| `AzureCli` | **Local development against Foundry.** | `az login` as a user with the **Cognitive Services OpenAI User** role (or **Azure AI User**) on the Foundry resource. The server process must see your Azure CLI profile (start Aspire from a shell where `az account show` works). |
| `ApiKey` | Other OpenAI-compatible servers, or Foundry key auth. | `Agent:ApiKey`. Any non-empty value for servers that ignore it (Ollama, vLLM). |
| `SystemAssignedManagedIdentity` | Azure-hosted (App Service, Container Apps, VM). | Enable the system-assigned identity on the host and grant it the role above on the Foundry resource. No other settings. |
| `UserAssignedManagedIdentity` | Azure-hosted with a shared or pre-created identity. | Assign the identity to the host, grant it the role above, and set `Agent:ManagedIdentityClientId` to its **client ID** (not the object ID). |

### Local Foundry with Azure CLI (the usual setup)

```json
{
  "Agent": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/openai/v1/",
    "Model": "your-deployment-name"
  }
}
```

That is all: `Auth` defaults to `AzureCli` for an `*.azure.com` endpoint and no key prompt appears. Add `"Auth": "AzureCli"` to make it explicit.

### Azure-hosted, for example

```
Agent__Endpoint=https://YOUR-RESOURCE.openai.azure.com/openai/v1/
Agent__Model=your-deployment-name
Agent__Auth=UserAssignedManagedIdentity
Agent__ManagedIdentityClientId=00000000-0000-0000-0000-000000000000
```

For key auth with the AppHost, set `"Auth": "ApiKey"`; the dashboard prompts for `agent-api-key` (or set `Parameters__agent-api-key`, or use user secrets). Without the AppHost, set the variables on the server directly.
## Tune it

- Instructions (Wan 2.2 prompt format, negative prompt defaults, settings advice): `src\VideoSafetyStudio.Infrastructure\Agent\VideoHelperInstructions.cs`.
- Add a control: define it in `VideoHelperTools.cs` (validation only), implement it in `web/src/features/create/helper/formTools.ts` and add a test.
- The chat renders Markdown with [Streamdown](https://streamdown.ai); style it under `.helper-md` in `web/src/styles/app.css`.

## Security notes

The server has no sign-in yet (Development only), so `/api/agent` is as open as the rest of the API and spends your endpoint's tokens. Put it behind authentication before sharing a deployment. The API key is held only in server memory and is never sent to the browser.
