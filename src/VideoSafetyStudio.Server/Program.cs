using Microsoft.Extensions.FileProviders;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Server;
using VideoSafetyStudio.Server.Auth;
using VideoSafetyStudio.Server.Endpoints;
using VideoSafetyStudio.Server.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// One credential for Azure SDK calls, the blob store, Key Vault and the Entra federated assertion.
var credential = StudioCredential.Create(builder.Configuration);
builder.Services.AddSingleton(credential);
builder.Configuration.AddStudioKeyVault(builder.Configuration, credential);

// Auth:Mode defaults to None in Development and Entra elsewhere; resolving throws when Entra is misconfigured.
builder.Services.AddStudioAuth();

builder.Services.AddStudio(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddHostedService<GenerationWorker>();
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
app.MapDefaultEndpoints();
app.MapGet("/healthz", () => Results.Json(new { status = "ok", mode = app.Services.GetRequiredService<AuthSettings>().Mode == AuthMode.None ? "local-dev" : "hosted" }));

app.UseSecurityHeaders(app.Configuration);
app.UseMiddleware<ErrorMappingMiddleware>();
app.UseStudioAuth();
app.UseRateLimiter();
app.MapAuth();
app.MapGeneration();
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
