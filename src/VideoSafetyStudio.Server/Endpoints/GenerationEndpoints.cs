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


        api.MapGet("/status", (GenerationService generation, StoreSelection store, CancellationToken ct) => generation.StatusAsync(store.Label, ct));

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

            var mode = form["mode"].ToString();
            var batch = await generation.SubmitAsync(new SubmitRequest(prompts, form["negative_prompt"].ToString().Trim(), form["aspect_ratio"].ToString(), duration, takes, images, mode.Length > 0 ? mode : null), ct);
            return Results.Json(new { batch }, Json.Options);
        });

        api.MapGet("/modes", (GenerationService generation) => Results.Json(new
        {
            defaultMode = WanModes.DefaultId,
            modes = WanModes.All.Select(m => new { m.Id, m.Label, m.Description, m.Model, m.Steps, m.SplitStep, m.Cfg, m.Shift, m.Sampler, m.Fps, available = generation.ModeAvailable(m) }),
        }, Json.Options));

        api.MapGet("/modes/{mode}/workflow", async (string mode, GenerationService generation, CancellationToken ct) =>
            Results.Text((await generation.ExportWorkflowAsync(mode, ct)).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), "application/json"));

        api.MapGet("/batches/{id}", (string id, GenerationService generation) => Results.Json(generation.GetBatch(id), Json.Options));
        api.MapGet("/jobs", async (GenerationService generation, CancellationToken ct) => Results.Json(new { jobs = await generation.OpenJobsAsync(ct) }, Json.Options));

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
        var tick = timer.WaitForNextTickAsync(ct).AsTask(); // one outstanding wait at a time; PeriodicTimer allows no more
        while (!ct.IsCancellationRequested)
        {
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
            else
            {
                if (!await tick)
                {
                    yield break;
                }

                tick = timer.WaitForNextTickAsync(ct).AsTask();
                yield return new("{}", "ping");
            }
        }    }
}
