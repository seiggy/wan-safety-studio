using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Server.Endpoints;

public sealed record CreateProjectRequest(string? Name, List<string>? Classes);

public sealed record ExtractRequest(double? Fps);

public sealed record GalleryImportRequest(string? Blob);

public sealed record PutLabelsRequest(bool Reviewed, List<Box>? Boxes);

public sealed record FreezeRequest(double? ValFraction, int? Seed);

public sealed record StartRunRequest(string? Dataset, string? Trainer, string? Preset, int? Epochs);

public static class VisionEndpoints
{
    public static void MapVision(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/vision");

        api.MapGet("/projects", async (VisionService vision, CancellationToken ct) => Results.Json(new { projects = await vision.ListProjectsAsync(ct) }, Json.Options));
        api.MapPost("/projects", async (CreateProjectRequest request, VisionService vision, CancellationToken ct) =>
            Results.Json(await vision.CreateProjectAsync(request.Name, request.Classes, ct), Json.Options, statusCode: StatusCodes.Status201Created));

        var project = api.MapGroup("/projects/{project}");
        project.MapGet("", async (string project, VisionService vision, CancellationToken ct) => Results.Json(await vision.GetProjectAsync(project, ct), Json.Options));
        project.MapGet("/videos", async (string project, VisionService vision, CancellationToken ct) => Results.Json(new { videos = await vision.ListVideosAsync(project, ct) }, Json.Options));

        project.MapPost("/videos", async (string project, HttpRequest request, VisionService vision, CancellationToken ct) =>
        {
            await vision.GetProjectAsync(project, ct);
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new ValidationException("Upload one video as the 'file' field.");
            var temp = Path.Combine(Path.GetTempPath(), "vision-upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var path = Path.Combine(temp, "upload.bin");
                await using (var output = File.Create(path))
                {
                    await file.CopyToAsync(output, ct);
                }

                var name = Path.GetFileName(file.FileName);
                var id = await vision.ImportVideoAsync(project, path, new VideoOrigin("upload", name[..Math.Min(name.Length, 200)]), ct);
                return Results.Json(new { id }, Json.Options, statusCode: StatusCodes.Status201Created);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        });

        project.MapPost("/videos/from-gallery", async (string project, GalleryImportRequest request, VisionService vision, CancellationToken ct) =>
            Results.Json(new { id = await vision.ImportGalleryVideoAsync(project, request.Blob ?? string.Empty, ct) }, Json.Options, statusCode: StatusCodes.Status201Created));

        project.MapPost("/videos/{video}/extract", async (string project, string video, ExtractRequest request, VisionService vision, CancellationToken ct) =>
        {
            var index = await vision.ExtractAsync(project, video, request.Fps ?? 2, ct);
            return Results.Json(new { frames = index.Frames.Count }, Json.Options, statusCode: StatusCodes.Status201Created);
        });

        project.MapGet("/frames/{video}", async (string project, string video, VisionService vision, CancellationToken ct) =>
            Results.Json(await vision.FrameIndexAsync(project, video, ct), Json.Options));
        project.MapGet("/frames/{video}/{frame}.jpg", async (string project, string video, string frame, VisionService vision, CancellationToken ct) =>
            Results.File(await vision.FrameImageAsync(project, video, frame, ct), "image/jpeg"));

        project.MapGet("/labels/{video}/{frame}", async (string project, string video, string frame, VisionService vision, CancellationToken ct) =>
            Results.Json(await vision.GetLabelsAsync(project, video, frame, ct), Json.Options));
        project.MapPut("/labels/{video}/{frame}", async (string project, string video, string frame, PutLabelsRequest request, VisionService vision, CancellationToken ct) =>
            Results.Json(await vision.PutLabelsAsync(project, video, frame, request.Reviewed, request.Boxes ?? [], ct), Json.Options));

        project.MapPost("/datasets", async (string project, FreezeRequest request, VisionService vision, CancellationToken ct) =>
            Results.Json(await vision.FreezeDatasetAsync(project, request.ValFraction ?? 0.2, request.Seed ?? 0, ct), Json.Options, statusCode: StatusCodes.Status201Created));
        project.MapGet("/datasets", async (string project, VisionService vision, CancellationToken ct) =>
            Results.Json(new { datasets = await vision.ListDatasetsAsync(project, ct) }, Json.Options));

        // training
        api.MapGet("/trainers", (TrainingService training) => Results.Json(new { trainers = training.Trainers() }, Json.Options));
        project.MapPost("/runs", async (string project, StartRunRequest request, TrainingService training, CancellationToken ct) =>
            Results.Json(await training.StartAsync(project, request.Dataset ?? string.Empty, request.Trainer ?? string.Empty, request.Preset, request.Epochs, ct),
                Json.Options, statusCode: StatusCodes.Status202Accepted));
        project.MapGet("/runs", async (string project, TrainingService training, CancellationToken ct) =>
            Results.Json(new { runs = await training.ListAsync(project, ct) }, Json.Options));
        project.MapGet("/runs/{run}", async (string project, string run, TrainingService training, CancellationToken ct) =>
            Results.Json(await training.GetAsync(project, run, ct), Json.Options));
    }
}
