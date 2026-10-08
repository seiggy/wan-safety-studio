// Local development orchestration. Settings (appsettings.json / appsettings.Development.json / env vars, see scripts/Initialize-LocalDev.ps1):
//   Generation:Provider  comfyui (default: direct local ComfyUI; the AppHost passes Generation__Provider=comfyui to the server) | azureml (the Azure ML provider
//                        in the server itself, run locally). For azureml pass the WAN_STUDIO_* settings (foundation receipt, release, gate, sign-in); see docs/app-service*.md.
//   Storage:Backend      comfyui only: azurite (default) | local | azure   (azureml always uses the foundation's datastore container)
//   Storage:Path         local only: folder or mounted LAN share
//   Storage:Account      azure only: storage account name (uses your az login)
//   ComfyUi:Retrieval    api (default) | share      ComfyUi:OutputDir   share only: ComfyUI's output folder (UNC path or mount)
//   ComfyUi:WorkflowsDir, ComfyUi:UnetHigh / UnetLow / Vae / Clip / LoraHigh / LoraLow / Unet5B / Vae5B   optional workflow folder and model file names
//   Ports:Web / Ports:Api  fixed ports for the UI (default 51881) and the server (default 51882)
//   Agent:Endpoint / Agent:Model   enable the Video Production Helper on the Create page: any OpenAI v1 endpoint (Azure AI Foundry or a custom server).
//   Agent:WireApi (completions|responses), Agent:Vision (true|false), Agent:Auth (ApiKey | AzureCli | SystemAssignedManagedIdentity | UserAssignedManagedIdentity), Agent:ManagedIdentityClientId.
//   Unset Agent:Auth = AzureCli for an *.azure.com endpoint, else ApiKey (the secret parameter agent-api-key). See docs/video-helper.md.
var builder = DistributedApplication.CreateBuilder(args);

var provider = builder.Configuration["Generation:Provider"] ?? "comfyui";
var webPort = int.Parse(builder.Configuration["Ports:Web"] ?? "51881");
var apiPort = int.Parse(builder.Configuration["Ports:Api"] ?? "51882");

if (provider is not ("comfyui" or "azureml"))
{
    throw new InvalidOperationException("Generation:Provider must be comfyui or azureml.");
}

// The server always gets an explicit provider: local development is direct ComfyUI unless azureml is chosen here (the server's own default is azureml).
var server = builder.AddProject<Projects.VideoSafetyStudio_Server>("api")
    .WithEndpoint("http", endpoint => { endpoint.Port = apiPort; endpoint.IsProxied = false; })
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Generation__Provider", provider);

if (provider == "azureml")
{
    // Needs az login (or a managed identity) and a foundation receipt; no ComfyUI or Azurite. Storage is the foundation's datastore container.
    foreach (var variable in new[]
    {
        "WAN_STUDIO_FOUNDATION", "WAN_STUDIO_FOUNDATION_JSON", "WAN_STUDIO_RELEASE_JSON", "WAN_STUDIO_CONTRACT", "WAN_STUDIO_ARMED", "WAN_STUDIO_GATE",
        "WAN_STUDIO_PUBLIC_ORIGIN", "WAN_STUDIO_PORTAL_CLIENT_ID", "WAN_STUDIO_MANAGED_IDENTITY_CLIENT_ID", "WAN_STUDIO_DISABLE_AUTH",
    })
    {
        if (builder.Configuration[variable] is { Length: > 0 } value) server.WithEnvironment(variable, value);
    }
}
else
{
    var storageBackend = builder.Configuration["Storage:Backend"] ?? "azurite";
    var comfyUi = builder.AddParameter("comfyui-url", builder.Configuration["Parameters:comfyui-url"] ?? "http://localhost:8188");

    server.WithEnvironment("ComfyUi__Url", comfyUi)
        .WithEnvironment("Storage__Backend", storageBackend)
        .WithEnvironment("ComfyUi__Retrieval", builder.Configuration["ComfyUi:Retrieval"] ?? "api");

    foreach (var (setting, variable) in new[]
    {
        ("ComfyUi:OutputDir", "ComfyUi__OutputDir"), ("ComfyUi:WorkflowsDir", "ComfyUi__WorkflowsDir"),
        ("ComfyUi:LoraHigh", "ComfyUi__LoraHigh"), ("ComfyUi:LoraLow", "ComfyUi__LoraLow"),
        ("ComfyUi:Unet5B", "ComfyUi__Unet5B"), ("ComfyUi:Vae5B", "ComfyUi__Vae5B"),
        ("ComfyUi:UnetHigh", "ComfyUi__UnetHigh"), ("ComfyUi:UnetLow", "ComfyUi__UnetLow"),
        ("ComfyUi:Vae", "ComfyUi__Vae"), ("ComfyUi:Clip", "ComfyUi__Clip"),
    })
    {
        if (builder.Configuration[setting] is { Length: > 0 } value) server.WithEnvironment(variable, value);
    }

    // SMB credentials (UNC output path on Windows): parameters from env vars (Parameters__comfyui-smb-user/-password), user secrets or the dashboard prompt.
    if (builder.Configuration["ComfyUi:OutputDir"] is { } outputDir && outputDir.StartsWith(@"\\"))
    {
        server.WithEnvironment("ComfyUi__SmbUser", builder.AddParameter("comfyui-smb-user"))
              .WithEnvironment("ComfyUi__SmbPassword", builder.AddParameter("comfyui-smb-password", secret: true));
    }

    switch (storageBackend)
    {
        case "azurite":
            var blobs = builder.AddAzureStorage("storage").RunAsEmulator(emulator => emulator.WithDataVolume()).AddBlobs("blobs");
            server.WithReference(blobs).WaitFor(blobs);
            break;
        case "azure":
            server.WithEnvironment("Storage__Account", builder.Configuration["Storage:Account"]
                ?? throw new InvalidOperationException("Storage:Account is required when Storage:Backend=azure."));
            break;
        case "local":
            if (builder.Configuration["Storage:Path"] is { Length: > 0 } path) server.WithEnvironment("Storage__Path", path);
            break;
        default:
            throw new InvalidOperationException("Storage:Backend must be local, azurite or azure.");
    }
}

// Video Production Helper (bring-your-own-key). Off unless Agent:Endpoint is set, so no parameter prompt appears for people who don't use it.
if (builder.Configuration["Agent:Endpoint"] is { Length: > 0 } agentEndpoint)
{
    server.WithEnvironment("Agent__Endpoint", agentEndpoint)
          .WithEnvironment("Agent__Model", builder.Configuration["Agent:Model"] ?? throw new InvalidOperationException("Agent:Model is required when Agent:Endpoint is set."))
          .WithEnvironment("Agent__Vision", builder.Configuration["Agent:Vision"] ?? "true");
    if (builder.Configuration["Agent:WireApi"] is { Length: > 0 } wireApi) server.WithEnvironment("Agent__WireApi", wireApi);
    var agentAuth = builder.Configuration["Agent:Auth"];
    if (agentAuth is { Length: > 0 }) server.WithEnvironment("Agent__Auth", agentAuth);
    if (builder.Configuration["Agent:ManagedIdentityClientId"] is { Length: > 0 } identityClientId) server.WithEnvironment("Agent__ManagedIdentityClientId", identityClientId);

    // Only API-key auth needs a secret. Unset auth means a key for non-Azure endpoints and the Azure CLI login for Azure ones.
    var usesKey = string.IsNullOrWhiteSpace(agentAuth)
        ? !new Uri(agentEndpoint).Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase)
        : agentAuth.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || agentAuth.Equals("key", StringComparison.OrdinalIgnoreCase);
    if (usesKey)
    {
        server.WithEnvironment("Agent__ApiKey", builder.AddParameter("agent-api-key", secret: true));
    }
}

// The React app in dev: Vite with hot reload, proxying /api and /auth to the server. (The built UI can also be hosted by the server itself.)
builder.AddViteApp("web", "../../web")
    .WithEndpoint("http", endpoint => { endpoint.Port = webPort; endpoint.IsProxied = false; })
    .WithEnvironment("API_URL", server.GetEndpoint("http"))
    .WaitFor(server);

builder.Build().Run();
