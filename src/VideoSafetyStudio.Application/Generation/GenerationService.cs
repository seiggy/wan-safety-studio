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

public sealed record WorkItem(GenerationJob Job, string Prompt, SceneImage Image, string Negative, double Duration, int Width, int Height);

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
    TimingSummary? Timing);

public sealed record GalleryItem(
    [property: JsonPropertyName("job_name")] string JobName,
    [property: JsonPropertyName("display_name")] string DisplayName,
    int Width,
    int Height,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    string Prompt,
    [property: JsonPropertyName("video_url")] string VideoUrl,
    double Created,
    TimingSummary? Timing);

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
        await workflowSource.LoadAsync(ct); // fail fast with a clear message when no workflow file exists
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
                };
                _jobs[job.Name] = job;
                batch.Jobs.Add(job);
                await SaveAsync(job, ct);
                await _queue.Writer.WriteAsync(new WorkItem(job, request.Prompts[scene - 1], image, request.NegativePrompt, request.DurationSeconds, width, height), ct);
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
                $"/api/media/{Library}{meta.JobName}/{meta.Video}", meta.Created, meta.Timing));
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

        try
        {
            var image = await Phase("upload_image", () => comfy.UploadImageAsync($"{job.Name}-{item.Image.Name}", item.Image.Data, ct));
            // Wan latent length: 4n+1 frames.
            length = (int)Math.Round(item.Duration * options.Value.Fps / 4) * 4 + 1;
            var o = options.Value;
            var workflow = WorkflowRenderer.Render(await workflowSource.LoadAsync(ct), new Dictionary<string, object?>
            {
                ["unet_high"] = o.UnetHigh,
                ["unet_low"] = o.UnetLow,
                ["vae_name"] = o.Vae,
                ["clip_name"] = o.Clip,
                ["positive_prompt"] = item.Prompt,
                ["negative_prompt"] = item.Negative,
                ["input_image"] = image,
                ["seed"] = seed,
                ["width"] = item.Width,
                ["height"] = item.Height,
                ["length"] = length,
                ["fps"] = o.Fps,
                ["filename_prefix"] = $"wan-studio/{job.Name}",
            });
            timer = new StageTimer(workflow);
            job.Context = new JobContext(item.Prompt, item.Width, item.Height, item.Duration, seed);
            var running = timer;
            var entry = await Phase("comfyui", () => comfy.RunAsync(workflow, running, promptId =>
            {
                job.PromptId = promptId;
                _ = SaveAsync(job, CancellationToken.None);
            }, () => PublishProgress(job, running), ct));
            size = await FinishAsync(job, entry, job.Context, () => BuildTiming(job, running, phases, started, length, item, null, null), phases, ct);
            ok = true;
        }
        finally
        {
            var finished = Now;
            if (timer is { Started: not null })
            {
                job.Timing = BuildTiming(job, timer, phases, started, length, item, finished, size);
                telemetry.Emit(job.Name, job.Timing.Params!, timer, phases, started, finished, ok);
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
            Params = new GenerationParams(item.Width, item.Height, length, item.Duration, "comfyui"),
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
        var meta = new GalleryMeta(job.Name, job.DisplayName, context.Width, context.Height, context.Duration, context.Prompt, context.Seed, name, Now, timing?.Invoke());
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
