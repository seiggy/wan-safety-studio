using Microsoft.Extensions.FileProviders;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Server;
using VideoSafetyStudio.Server.Auth;
using VideoSafetyStudio.Server.Endpoints;
using VideoSafetyStudio.Server.Health;
using VideoSafetyStudio.Server.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// One credential for Azure SDK calls, the blob store, Key Vault and the Entra federated assertion.
var credential = StudioCredential.Create(builder.Configuration);
builder.Services.AddSingleton(credential);
builder.Configuration.AddStudioKeyVault(builder.Configuration, credential);

// Auth:Mode defaults to None in Development and Entra elsewhere; resolving throws when Entra is misconfigured.
builder.Services.AddStudioAuth();

// The host never stops on bad configuration: it serves /health-ui, refuses everything else and runs no workers until the settings are fixed (restart after changing them).
// Evaluated when first needed, so every configuration source (including test and host overrides) has been applied.
builder.Services.AddSingleton(sp => ConfigHealth.Evaluate(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddStudio(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddGuardedWorker<GenerationWorker>();
builder.Services.AddGuardedWorker<StudioStateService>();
if (builder.Configuration.GetValue<bool>("FEATURE_DATASET_BUILDER_ENABLED")) builder.Services.AddGuardedWorker<TrainingRecoveryService>();
builder.Services.AddVideoHelper(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});

// A small global ceiling; the upload routes raise it with RequestSizeLimit metadata.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = BodyLimits.Default);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = BodyLimits.Video);
builder.Services.AddStudioRateLimits();

var app = builder.Build();
var health = app.Services.GetRequiredService<ConfigReport>();
app.MapDefaultEndpoints();
app.MapGet("/healthz", () => Results.Json(new { status = "ok", mode = app.Services.GetRequiredService<AuthSettings>().Mode == AuthMode.None ? "local-dev" : "hosted" }));

app.UseSecurityHeaders(app.Configuration);
app.UseMiddleware<ErrorMappingMiddleware>();
app.UseStatusCodePages(async status =>
{
    if (status.HttpContext.Request.Path.StartsWithSegments("/api"))
    {
        await status.HttpContext.Response.WriteAsJsonAsync(new { error = $"Request failed ({status.HttpContext.Response.StatusCode})." });
    }
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var blocked = !health.Ready && (path.StartsWithSegments("/api") || path.StartsWithSegments("/auth")) && !path.StartsWithSegments("/api/health/config");
    if (blocked)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { error = "The studio configuration is incomplete. Open /health-ui to see what to set." });
        return;
    }

    await next(context);
});
app.UseStudioAuth();
app.UseRateLimiter();
// Anonymous by design: it names settings and shows static examples, never a configured value.
app.MapGet("/api/health/config", (HttpContext http) =>
{
    http.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { status = health.Status, mode = health.Mode, issues = health.Issues }, Json.Options);
});
app.MapAuth();
app.MapGeneration();
app.MapStudioAdmin();
// Dataset builder (vision datasets, labelling, training) is hidden unless FEATURE_DATASET_BUILDER_ENABLED=true.
var datasetBuilder = app.Configuration.GetValue<bool>("FEATURE_DATASET_BUILDER_ENABLED");
app.MapGet("/api/features", () => new { datasetBuilder });
if (datasetBuilder) app.MapVision();
app.MapVideoHelper();

// The React app: built output (wwwroot after publish, or ../../web/dist in a source checkout) served beside the API.
var webRoot = ResolveWebRoot(app);
if (webRoot is not null)
{
    var files = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "Not found." }));
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
}

app.Run();

static string? ResolveWebRoot(WebApplication app)
{
    var configured = app.Configuration["Web:Root"];
    string[] candidates =
    [
        configured is null ? "" : Path.GetFullPath(configured, app.Environment.ContentRootPath),
        Path.Combine(app.Environment.ContentRootPath, "wwwroot"),
        Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "web", "dist")),
    ];
    return candidates.FirstOrDefault(path => path.Length > 0 && File.Exists(Path.Combine(path, "index.html")));
}

/// <summary>Entry point reference for integration tests.</summary>
public partial class Program;
