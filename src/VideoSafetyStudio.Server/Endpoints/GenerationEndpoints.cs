using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Infrastructure.Events;

namespace VideoSafetyStudio.Server.Endpoints;

public static class GenerationEndpoints
{
    public static void MapGeneration(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/session", () => new { name = "Local developer", csrfToken = "local-dev", expires = 32503680000.0 });

        api.MapGet("/status", async (GenerationService generation, StoreSelection store, CancellationToken ct) =>
        {
            var up = await generation.Comfy.IsReachableAsync(ct);
            var ready = up && generation.Workflow.Exists;
            return new
            {
                mode = "local",
                workspace = "local-dev",
                resourceGroup = "n/a",
                storage = store.Label,
                compute = $"ComfyUI {generation.Comfy.BaseUrl} ({(up ? "reachable" : "unreachable")})",
                computeState = up ? "Succeeded" : "Unreachable",
                prepared = generation.Workflow.Exists,
                version = (string?)null,
                armed = ready,
                generationEnabled = ready,
                checkedAt = DateTimeOffset.UtcNow,
                maxJobMinutes = 120,
                maxUploadMb = 512,
                profile = (object?)null,
            };
        });

        api.MapPost("/submit", async (HttpRequest request, GenerationService generation, CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var prompts = form["prompt"].Select(p => (p ?? string.Empty).Trim()).ToList();
            if (!double.TryParse(form["duration_seconds"], System.Globalization.CultureInfo.InvariantCulture, out var duration) ||
                !int.TryParse(form["takes"], out var takes))
            {
                throw new ValidationException("Duration and takes must be numbers.");
            }

            var images = new List<SceneImage?>();
            for (var index = 0; index < prompts.Count; index++)
            {
                var upload = form.Files.GetFile($"input_image_{index}");
                if (upload is { Length: > 0 })
                {
                    using var buffer = new MemoryStream();
                    await upload.CopyToAsync(buffer, ct);
                    images.Add(new SceneImage(Path.GetFileName(upload.FileName) is { Length: > 0 } name ? name[..Math.Min(name.Length, 100)] : "input.png", buffer.ToArray()));
                }
                else
                {
                    images.Add(null);
                }
            }

            var batch = await generation.SubmitAsync(new SubmitRequest(prompts, form["negative_prompt"].ToString().Trim(), form["aspect_ratio"].ToString(), duration, takes, images), ct);
            return Results.Json(new { batch }, Json.Options);
        });

        api.MapGet("/batches/{id}", (string id, GenerationService generation) => Results.Json(generation.GetBatch(id), Json.Options));
        api.MapGet("/jobs", (GenerationService generation) => Results.Json(new { jobs = generation.OpenJobs() }, Json.Options));

        // A job the server has no record of (e.g. remembered by a browser from before a restart) reads as Failed so clients stop waiting.
        api.MapGet("/jobs/{name}", (string name, GenerationService generation) => Results.Json(
            (object?)generation.FindJob(name) ?? new { name, status = JobStatus.Failed, error = "Unknown job (the API restarted)." }, Json.Options));

        api.MapGet("/gallery", async (GenerationService generation, CancellationToken ct) => Results.Json(await generation.GetGalleryAsync(ct), Json.Options));

        // Generated videos, with HTTP range support so players can seek.
        api.MapGet("/media/{**key}", async (string key, IObjectStore store, CancellationToken ct) =>
        {
            if (!key.StartsWith(GenerationService.Library, StringComparison.Ordinal) || key.Contains(".."))
            {
                return Results.NotFound();
            }

            var data = await store.ReadAsync(key, ct);
            return data is null
                ? Results.NotFound()
                : Results.File(new MemoryStream(data), ContentType(key), enableRangeProcessing: true);
        });

        // Server-sent events replace client polling: job progress and training run updates are pushed.
        api.MapGet("/events", (EventHub hub, CancellationToken ct) => TypedResults.ServerSentEvents(Stream(hub, ct)));
    }

    private static string ContentType(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        _ => "application/octet-stream",
    };

    private static async IAsyncEnumerable<System.Net.ServerSentEvents.SseItem<string>> Stream(
        EventHub hub, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new("{}", "ready");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var events = hub.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var next = events.MoveNextAsync().AsTask();
        while (!ct.IsCancellationRequested)
        {
            var tick = timer.WaitForNextTickAsync(ct).AsTask();
            var finished = await Task.WhenAny(next, tick);
            if (finished == next)
            {
                if (!await next)
                {
                    yield break;
                }

                yield return new(events.Current.Data, events.Current.Type);
                next = events.MoveNextAsync().AsTask();
            }
            else if (await tick)
            {
                yield return new("{}", "ping");
            }
        }
    }
}
