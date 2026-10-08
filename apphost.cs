#:sdk Aspire.AppHost.Sdk@13.6.1
#:package Aspire.Hosting.Python@13.6.1
#:package Aspire.Hosting.JavaScript@13.6.1
#:package Aspire.Hosting.Azure.Storage@13.6.1
#:property AspireUseCliBundle=true

var builder = DistributedApplication.CreateBuilder(args);

// Settings (appsettings.json, appsettings.Development.json via scripts/Initialize-LocalDev.ps1, or env vars like Storage__Backend=azure):
//   Generation:Provider  comfyui (default, fully local, no Azure ML) | azureml (use your deployed Azure ML workspace)
// comfyui provider:
//   Storage:Backend      azurite (default) | local | azure
//   Storage:Path         local only: folder or mounted LAN share (\\server\share, /mnt/share); default .dev-data/blob
//   Storage:Account      azure only: storage account name (uses your az login)
//   ComfyUi:Retrieval    api (default, downloads via ComfyUI's /view) | share (reads ComfyUI's output folder)
//   ComfyUi:OutputDir    share only: ComfyUI output folder as mounted on this machine
//   ComfyUi:Workflow     optional path to the API-format workflow JSON
//   ComfyUi:UnetHigh / UnetLow / Vae / Clip   optional model file names on the ComfyUI server (defaults: Wan 2.2 fp8 scaled)
// azureml provider (runs the Azure-backed portal through scripts/Invoke-WanSafetyStudio.ps1 -Action Portal):
//   AzureMl:ConfigPath / AzureMl:CacheDirectory   optional -ConfigPath / -CacheDirectory for that script
var provider = builder.Configuration["Generation:Provider"] ?? "comfyui";
IResourceBuilder<IResourceWithEndpoints> api;

if (provider == "azureml")
{
    // Needs: az login, a completed Deploy/Prepare/Start, and Initialize-PortalAuth (Entra sign-in). No ComfyUI or Azurite.
    var portalArgs = new List<string> { "-NoProfile", "-File", Path.Combine("scripts", "Invoke-WanSafetyStudio.ps1"), "-Action", "Portal" };
    if (builder.Configuration["AzureMl:ConfigPath"] is { Length: > 0 } configPath) portalArgs.AddRange(["-ConfigPath", configPath]);
    if (builder.Configuration["AzureMl:CacheDirectory"] is { Length: > 0 } cacheDir) portalArgs.AddRange(["-CacheDirectory", cacheDir]);
    api = builder.AddExecutable("api", "pwsh", builder.AppHostDirectory, [.. portalArgs])
        .WithHttpEndpoint(port: 51882, env: "WAN_STUDIO_LISTEN_PORT", isProxied: false);
}
else if (provider == "comfyui")
{
    var storageBackend = builder.Configuration["Storage:Backend"] ?? "azurite";
    var retrieval = builder.Configuration["ComfyUi:Retrieval"] ?? "api";
    var comfyUi = builder.AddParameter("comfyui-url", builder.Configuration["Parameters:comfyui-url"] ?? "http://localhost:8188");

    var local = builder.AddPythonApp("api", "./app", "dev_server.py")
        .WithUv(install: false)
        .WithHttpEndpoint(port: 51882, env: "PORT", isProxied: false)
        .WithHttpHealthCheck("/healthz")
        .WithEnvironment("COMFYUI_URL", comfyUi)
        .WithEnvironment("WAN_STUDIO_DEV_DATA", Path.Combine(builder.AppHostDirectory, ".dev-data"))
        .WithEnvironment("WAN_STUDIO_STORAGE", storageBackend)
        .WithEnvironment("WAN_STUDIO_VIDEO_RETRIEVAL", retrieval);

    if (builder.Configuration["ComfyUi:OutputDir"] is { Length: > 0 } outputDir)
    {
        local.WithEnvironment("COMFYUI_OUTPUT_DIR", outputDir);
        // SMB credentials (UNC paths on Windows) come from parameters: Parameters__comfyui-smb-user / Parameters__comfyui-smb-password
        // env vars, user secrets or the dashboard prompt. Never put the password in a committed file.
        if (outputDir.StartsWith(@"\\"))
        {
            local.WithEnvironment("COMFYUI_SMB_USER", builder.AddParameter("comfyui-smb-user"))
                 .WithEnvironment("COMFYUI_SMB_PASSWORD", builder.AddParameter("comfyui-smb-password", secret: true));
        }
    }
    foreach (var (setting, variable) in new[] { ("ComfyUi:UnetHigh", "COMFYUI_UNET_HIGH"), ("ComfyUi:UnetLow", "COMFYUI_UNET_LOW"),
                                                 ("ComfyUi:Vae", "COMFYUI_VAE"), ("ComfyUi:Clip", "COMFYUI_CLIP") })
        if (builder.Configuration[setting] is { Length: > 0 } modelFile) local.WithEnvironment(variable, modelFile);
    if (builder.Configuration["ComfyUi:Workflow"] is { Length: > 0 } workflow)
        local.WithEnvironment("COMFYUI_WORKFLOW", workflow);

    switch (storageBackend)
    {
        case "azurite":
            var blobs = builder.AddAzureStorage("storage")
                .RunAsEmulator(emulator => emulator.WithDataVolume())
                .AddBlobs("blobs");
            local.WithReference(blobs).WaitFor(blobs);
            break;
        case "azure":
            local.WithEnvironment("WAN_STUDIO_STORAGE_ACCOUNT", builder.Configuration["Storage:Account"]
                ?? throw new InvalidOperationException("Storage:Account is required when Storage:Backend=azure."));
            break;
        case "local":
            if (builder.Configuration["Storage:Path"] is { Length: > 0 } path)
                local.WithEnvironment("WAN_STUDIO_STORAGE_PATH", path);
            break;
        default:
            throw new InvalidOperationException("Storage:Backend must be local, azurite or azure.");
    }
    api = local;
}
else
{
    throw new InvalidOperationException("Generation:Provider must be comfyui or azureml.");
}

// The API checks Host/Origin against localhost:51881, so the front end keeps that fixed origin and proxies to the API.
builder.AddViteApp("web", "./web")
    .WithEndpoint("http", e => { e.Port = 51881; e.IsProxied = false; })
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WaitFor(api);

builder.Build().Run();
