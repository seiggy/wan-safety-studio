using Microsoft.Extensions.FileProviders;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Server;
using VideoSafetyStudio.Server.Endpoints;
using VideoSafetyStudio.Server.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// This server has no sign-in yet: it is for local development. Refuse to run elsewhere unless explicitly allowed.
if (!builder.Environment.IsDevelopment() && !builder.Configuration.GetValue<bool>("Server:AllowAnonymous"))
{
    throw new InvalidOperationException("VideoSafetyStudio.Server has no authentication. Run it in Development or set Server:AllowAnonymous=true behind a gateway that authenticates.");
}

builder.Services.AddStudio(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddHostedService<GenerationWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    options.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});

const long MaxUpload = 512L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = MaxUpload);

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/healthz", () => Results.Json(new { status = "ok", mode = "local-dev" }));

app.UseMiddleware<ErrorMappingMiddleware>();
app.MapGeneration();
app.MapVision();

// The React app: built output (wwwroot after publish, or ../../web/dist in a source checkout) served beside the API.
var webRoot = ResolveWebRoot(app);
if (webRoot is not null)
{
    var files = new PhysicalFileProvider(webRoot);
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
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
