// Local development orchestration. Settings (appsettings.json / appsettings.Development.json / env vars, see scripts/Initialize-LocalDev.ps1):
//   Generation:Provider  comfyui (default, fully local) | azureml (the legacy Azure-backed Python portal; being ported)
//   Storage:Backend      azurite (default) | local | azure
//   Storage:Path         local only: folder or mounted LAN share
//   Storage:Account      azure only: storage account name (uses your az login)
//   ComfyUi:Retrieval    api (default) | share      ComfyUi:OutputDir   share only: ComfyUI's output folder (UNC path or mount)
//   ComfyUi:WorkflowsDir, ComfyUi:UnetHigh / UnetLow / Vae / Clip / LoraHigh / LoraLow / Unet5B / Vae5B   optional workflow folder and model file names
//   Ports:Web / Ports:Api  fixed ports for the UI (default 51881) and the server (default 51882)
var builder = DistributedApplication.CreateBuilder(args);

var provider = builder.Configuration["Generation:Provider"] ?? "comfyui";
var webPort = int.Parse(builder.Configuration["Ports:Web"] ?? "51881");
var apiPort = int.Parse(builder.Configuration["Ports:Api"] ?? "51882");

IResourceBuilder<IResourceWithEndpoints> api;

if (provider == "azureml")
{
    // Needs: az login, a completed Deploy/Prepare/Start and Initialize-PortalAuth. No ComfyUI or Azurite.
    var portalArgs = new List<string> { "-NoProfile", "-File", Path.Combine("scripts", "Invoke-WanSafetyStudio.ps1"), "-Action", "Portal" };
    if (builder.Configuration["AzureMl:ConfigPath"] is { Length: > 0 } configPath) portalArgs.AddRange(["-ConfigPath", configPath]);
    if (builder.Configuration["AzureMl:CacheDirectory"] is { Length: > 0 } cacheDir) portalArgs.AddRange(["-CacheDirectory", cacheDir]);
    api = builder.AddExecutable("api", "pwsh", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")), [.. portalArgs])
        .WithHttpEndpoint(port: apiPort, env: "WAN_STUDIO_LISTEN_PORT", isProxied: false);
}
else if (provider == "comfyui")
{
    var storageBackend = builder.Configuration["Storage:Backend"] ?? "azurite";
    var comfyUi = builder.AddParameter("comfyui-url", builder.Configuration["Parameters:comfyui-url"] ?? "http://localhost:8188");

    var server = builder.AddProject<Projects.VideoSafetyStudio_Server>("api")
        .WithEndpoint("http", endpoint => { endpoint.Port = apiPort; endpoint.IsProxied = false; })
        .WithHttpHealthCheck("/health")
        .WithEnvironment("ComfyUi__Url", comfyUi)
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

    api = server;
}
else
{
    throw new InvalidOperationException("Generation:Provider must be comfyui or azureml.");
}

// The React app in dev: Vite with hot reload, proxying /api and /auth to the server. (The built UI can also be hosted by the server itself.)
builder.AddViteApp("web", "../../web")
    .WithEndpoint("http", endpoint => { endpoint.Port = webPort; endpoint.IsProxied = false; })
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
