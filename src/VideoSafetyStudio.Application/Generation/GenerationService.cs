using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

public sealed record WorkItem(GenerationJob Job, string Prompt, SceneImage Image, string Negative, double Duration, int Width, int Height, WanMode Mode);

/// <summary>Stored beside each video: what the Library shows and what benchmarks read.</summary>
public sealed record GalleryMeta(
    [property: JsonPropertyName("job_name")] string JobName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    int Width,
    int Height,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    string Prompt,
    long Seed,
    string Video,
    double Created,
    TimingSummary? Timing,
    string? Mode = null);

public sealed record GalleryItem(
    [property: JsonPropertyName("job_name")] string JobName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    int Width,
    int Height,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    string Prompt,
    [property: JsonPropertyName("video_url")] string VideoUrl,
    double Created,
    TimingSummary? Timing,
    string? Mode = null);

public sealed record GalleryResponse(
    IReadOnlyList<GalleryItem> Items,
    int Count,
    [property: JsonPropertyName("scanned_jobs")] int ScannedJobs);

/// <summary>Queues generation jobs on ComfyUI, runs them one at a time (like the single GPU node) and records timings.</summary>
public sealed class GenerationService(
    IObjectStore store,
    IComfyUiClient comfy,
    IVideoRetrieval retrieval,
    IWorkflowSource workflowSource,
    IGenerationTelemetry telemetry,
    IEventPublisher events,
    IOptions<GenerationOptions> options,
    TimeProvider time,
    ILogger<GenerationService> logger)
{
    public const string Library = "video-library/";
    public const string JobsPrefix = "video-library-jobs/"; // outside the library prefix so the gallery ignores job records

    private static readonly Dictionary<string, (int Width, int Height)> Aspects = new()
    {
        ["16:9"] = (1024, 576),
        ["4:3"] = (896, 672),
        ["1:1"] = (768, 768),
    };

    private const int MaxScenes = 5;
    private static readonly int[] TakeChoices = [1, 3, 5];

    private readonly ConcurrentDictionary<string, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<string, Batch> _batches = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    public IComfyUiClient Comfy => comfy;

    public IWorkflowSource Workflow => workflowSource;

    public IAsyncEnumerable<WorkItem> DequeueAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);

    // ---- submit
    public async Task<Batch> SubmitAsync(SubmitRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var mode = WanModes.Get(request.Mode);
        await workflowSource.LoadAsync(mode.Template, ct); // fail fast with a clear message when no workflow file exists
        var (width, height) = Aspects[request.AspectRatio];
        var batch = new Batch { Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_'), Total = request.Prompts.Count * request.Takes };
        for (var scene = 1; scene <= request.Prompts.Count; scene++)
        {
            var image = request.Images[scene - 1] ?? request.Images[0]!;
            for (var take = 1; take <= request.Takes; take++)
            {
                var job = new GenerationJob
                {
                    Name = "local-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)),
                    DisplayName = $"scene {scene} take {take}",
                    SubmittedAt = Now,
                    Scene = scene,
                    Take = take,
                    Mode = mode.Id,
                };
                _jobs[job.Name] = job;
                batch.Jobs.Add(job);
                await SaveAsync(job, ct);
                await _queue.Writer.WriteAsync(new WorkItem(job, request.Prompts[scene - 1], image, request.NegativePrompt, request.DurationSeconds, width, height, mode), ct);
            }
        }

        _batches[batch.Id] = batch;
        return batch;
    }

    private static void Validate(SubmitRequest request)
    {
        if (request.Prompts.Count is < 1 or > MaxScenes || request.Prompts.Any(p => p.Length is < 1 or > 4000))
        {
            throw new ValidationException($"Add 1-{MaxScenes} scenes, each 1-4000 characters.");
        }

        if (!Aspects.ContainsKey(request.AspectRatio))
        {
            throw new ValidationException("Choose a 16:9, 4:3 or 1:1 aspect ratio.");
        }

        if (request.DurationSeconds is < 5 or > 10 || !TakeChoices.Contains(request.Takes))
        {
            throw new ValidationException("Duration or takes out of range.");
        }

        if (request.Images.Count == 0 || request.Images[0] is null)
        {
            throw new ValidationException("Scene 1 needs a reference image.");
        }
    }

    /// <summary>The API-format workflow for a mode with example inputs, for importing into ComfyUI by hand.</summary>
    public async Task<System.Text.Json.Nodes.JsonObject> ExportWorkflowAsync(string? modeId, CancellationToken ct = default)
    {
        var mode = WanModes.Get(modeId);
        var (width, height) = Aspects["1:1"];
        return WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct), Values(mode, "Describe the scene and movement.", string.Empty, "example.png", 1, width, height,
            (int)Math.Round(5.0 * mode.Fps / 4) * 4 + 1, "wan-studio/example"));
    }

    private Dictionary<string, object?> Values(WanMode mode, string prompt, string negative, string image, long seed, int width, int height, int length, string prefix)
    {
        var o = options.Value;
        return new Dictionary<string, object?>
        {
            ["steps"] = mode.Steps, ["split_step"] = mode.SplitStep, ["cfg"] = mode.Cfg, ["shift"] = mode.Shift, ["sampler"] = mode.Sampler,
            ["lora_high"] = o.LoraHigh, ["lora_low"] = o.LoraLow, ["unet_5b"] = o.Unet5B, ["vae_5b"] = o.Vae5B,
            ["unet_high"] = o.UnetHigh, ["unet_low"] = o.UnetLow, ["vae_name"] = o.Vae, ["clip_name"] = o.Clip,
            ["positive_prompt"] = prompt, ["negative_prompt"] = negative, ["input_image"] = image, ["seed"] = seed,
            ["width"] = width, ["height"] = height, ["length"] = length, ["fps"] = mode.Fps, ["filename_prefix"] = prefix,
        };
    }

    // ---- queries
    public Batch GetBatch(string id) => _batches.TryGetValue(id, out var batch) ? batch : throw new NotFoundException("This batch is not available.");

    public GenerationJob? FindJob(string name) => _jobs.GetValueOrDefault(name);

    public IReadOnlyList<GenerationJob> OpenJobs() => [.. _jobs.Values.Where(j => !JobStatus.IsTerminal(j.Status))];

    public async Task<GalleryResponse> GetGalleryAsync(CancellationToken ct = default)
    {
        var items = new List<GalleryItem>();
        foreach (var key in (await store.ListAsync(Library.TrimEnd('/'), ct)).Where(k => k.EndsWith("/meta.json", StringComparison.Ordinal)))
        {
            var meta = Json.From<GalleryMeta>((await store.ReadAsync(key, ct))!)!;
            items.Add(new GalleryItem(meta.JobName, meta.DisplayName, meta.Width, meta.Height, meta.DurationSeconds, meta.Prompt,
                $"/api/media/{Library}{meta.JobName}/{meta.Video}", meta.Created, meta.Timing, meta.Mode));
        }

        return new GalleryResponse([.. items.OrderByDescending(i => i.Created)], items.Count, _jobs.Count);
    }

    // ---- worker side
    /// <summary>After a restart: resume jobs ComfyUI already accepted; fail the rest so clients stop waiting.</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        foreach (var key in await store.ListAsync(JobsPrefix.TrimEnd('/'), ct))
        {
            var data = await store.ReadAsync(key, ct);
            var job = data is null ? null : Json.From<GenerationJob>(data);
            if (job is null)
            {
                continue;
            }

            _jobs[job.Name] = job;
            if (JobStatus.IsTerminal(job.Status))
            {
                continue;
            }

            if (job.PromptId is not null && job.Context is not null)
            {
                job.Status = JobStatus.Running;
                _ = Task.Run(() => ResumeAsync(job, ct), CancellationToken.None);
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.Error = "Interrupted by a restart before ComfyUI accepted it.";
                await SaveAsync(job, ct);
            }
        }
    }

    private async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        try
        {
            var entry = await comfy.WaitAsync(job.PromptId!, ct);
            await FinishAsync(job, entry, job.Context!, null, null, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(job, error);
        }

        await SaveAsync(job, CancellationToken.None);
    }

    public async Task ProcessAsync(WorkItem item, CancellationToken ct)
    {
        var job = item.Job;
        job.Status = JobStatus.Running;
        job.AppQueueSeconds = Math.Round(Now - job.SubmittedAt, 3);
        await SaveAsync(job, ct);
        try
        {
            await GenerateAsync(item, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(job, error); // one failed job must not stop the queue
        }

        job.Progress = null;
        await SaveAsync(job, CancellationToken.None);
    }

    private void Fail(GenerationJob job, Exception error)
    {
        logger.LogError(error, "Job {Job} failed", job.Name);
        job.Status = JobStatus.Failed;
        job.Error = error.Message.Length > 300 ? error.Message[..300] : error.Message;
    }

    private async Task GenerateAsync(WorkItem item, CancellationToken ct)
    {
        var job = item.Job;
        var started = Now;
        var mode = item.Mode;
        double? queuedAt = null;
        var seed = RandomNumberGenerator.GetInt32(int.MaxValue) * 1000L + RandomNumberGenerator.GetInt32(1000);
        var phases = new Dictionary<string, double>();
        StageTimer? timer = null;
        int? length = null;
        long? size = null;
        var ok = false;

        async Task<T> Phase<T>(string name, Func<Task<T>> action)
        {
            var began = Now;
            try
            {
                return await action();
            }
            finally
            {
                phases[name] = Math.Round(Now - began, 3);
            }
        }

        // Waiting: submit -> start of processing here. ComfyUI queue: prompt accepted -> execution_start.
        void AddQueuePhases()
        {
            phases["waiting"] = Math.Round(started - job.SubmittedAt, 3);
            if (queuedAt is { } queued && timer?.Started is { } begun && begun >= queued)
            {
                phases["comfyui_queue"] = Math.Round(begun - queued, 3);
            }
        }

        try
        {
            var image = await Phase("upload_image", () => comfy.UploadImageAsync($"{job.Name}-{item.Image.Name}", item.Image.Data, ct));
            // Wan latent length: 4n+1 frames.
            length = (int)Math.Round(item.Duration * mode.Fps / 4) * 4 + 1;
            var workflow = WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct), Values(mode, item.Prompt, item.Negative, image, seed, item.Width, item.Height, length.Value, $"wan-studio/{job.Name}"));
            timer = new StageTimer(workflow);
            job.Context = new JobContext(item.Prompt, item.Width, item.Height, item.Duration, seed);
            var running = timer;
            var entry = await Phase("comfyui", () => comfy.RunAsync(workflow, running, promptId =>
            {
                queuedAt = Now;
                job.PromptId = promptId;
                _ = SaveAsync(job, CancellationToken.None);
            }, () => PublishProgress(job, running), ct));
            AddQueuePhases();
            size = await FinishAsync(job, entry, job.Context, () => BuildTiming(job, running, phases, started, length, item, null, null), phases, ct);
            ok = true;
        }
        finally
        {
            var finished = Now;
            if (timer is { Started: not null })
            {
                // Waiting: submit -> start of processing here. ComfyUI queue: prompt accepted -> execution_start.
                AddQueuePhases();
                var spans = new List<PhaseSpan> { new("waiting", job.SubmittedAt, started) };
                if (queuedAt is { } queued && timer.Started is { } begun && begun >= queued)
                {
                    spans.Add(new PhaseSpan("comfyui_queue", queued, begun));
                }

                job.Timing = BuildTiming(job, timer, phases, started, length, item, finished, size);
                telemetry.Emit(job.Name, job.Timing.Params!, timer, phases, spans, job.SubmittedAt, finished, ok);
            }
        }
    }

    private void PublishProgress(GenerationJob job, StageTimer timer)
    {
        var progress = timer.Progress;
        if (progress != job.Progress)
        {
            job.Progress = progress;
            events.Publish("job", job);
        }
    }

    private TimingSummary BuildTiming(GenerationJob job, StageTimer timer, Dictionary<string, double> phases, double started, int? length, WorkItem item, double? finished, long? size)
    {
        var summary = timer.Summary();
        return summary with
        {
            Phases = new Dictionary<string, double>(phases),
            TotalSeconds = Math.Round((finished ?? Now) - started, 3),
            AppQueueSeconds = job.AppQueueSeconds,
            Params = new GenerationParams(item.Width, item.Height, length, item.Duration, "comfyui", item.Mode.Id, item.Mode.Steps),
            VideoBytes = size,
        };
    }

    private async Task<long> FinishAsync(GenerationJob job, System.Text.Json.Nodes.JsonNode entry, JobContext context, Func<TimingSummary>? timing, Dictionary<string, double>? phases, CancellationToken ct)
    {
        var videos = WorkflowRenderer.FindVideos(entry);
        if (videos.Count == 0)
        {
            throw new ComfyUiException("ComfyUI finished but produced no video output.");
        }

        var reference = videos[0];
        phases ??= [];
        var began = Now;
        var data = await retrieval.FetchAsync(comfy, reference, ct);
        phases["retrieve"] = Math.Round(Now - began, 3);
        var name = "video" + Path.GetExtension(reference.Filename).ToLowerInvariant();
        began = Now;
        await store.WriteAsync($"{Library}{job.Name}/{name}", data, true, ct);
        phases["store_video"] = Math.Round(Now - began, 3);
        var meta = new GalleryMeta(job.Name, job.DisplayName, context.Width, context.Height, context.Duration, context.Prompt, context.Seed, name, Now, timing?.Invoke(), job.Mode);
        await store.WriteAsync($"{Library}{job.Name}/meta.json", Json.ToBytes(meta), true, ct);
        return data.Length;
    }

    private async Task SaveAsync(GenerationJob job, CancellationToken ct)
    {
        try
        {
            await store.WriteAsync($"{JobsPrefix}{job.Name}.json", Json.ToBytes(job), true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Could not persist job {Job}", job.Name); // persistence must never fail a job
        }

        events.Publish("job", job);
    }
}
