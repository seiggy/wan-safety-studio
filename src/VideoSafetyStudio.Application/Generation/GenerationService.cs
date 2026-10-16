using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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

/// <summary>Queues generation jobs, hands them to the configured backend (ComfyUI or Azure ML) and records timings.</summary>
public sealed class GenerationService(
    IObjectStore store,
    IGenerationBackend backend,
    IWorkflowSource workflowSource,
    JobRecords records,
    IOptions<GenerationOptions> options,
    TimeProvider time,
    ILogger<GenerationService> logger)
{
    public const string Library = "video-library/";
    public const string JobsPrefix = JobRecords.Prefix;

    private static readonly Dictionary<string, (int Width, int Height)> Aspects = new()
    {
        ["16:9"] = (1024, 576),
        ["4:3"] = (896, 672),
        ["1:1"] = (768, 768),
    };

    private const int MaxScenes = 5;
    public const int MaxNameLength = 50;
    private static readonly int[] TakeChoices = [1, 3, 5];

    private readonly ConcurrentDictionary<string, GenerationJob> _jobs = new();
    private readonly ConcurrentDictionary<string, Batch> _batches = new();
    private readonly Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly SemaphoreSlim _submitGate = new(1, 1);

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Drops finished jobs (and batches made only of them) older than the retention from memory; their stored records and gallery entries stay.</summary>
    private void Prune()
    {
        var cutoff = Now - Math.Max(0, options.Value.JobRetentionHours) * 3600;
        foreach (var job in _jobs.Values.Where(j => JobStatus.IsTerminal(j.Status) && j.SubmittedAt < cutoff))
        {
            _jobs.TryRemove(job.Name, out _);
        }

        foreach (var batch in _batches.Values.Where(b => b.Jobs.All(j => JobStatus.IsTerminal(j.Status) && j.SubmittedAt < cutoff)))
        {
            _batches.TryRemove(batch.Id, out _);
        }
    }

    public IGenerationBackend Backend => backend;

    public IWorkflowSource Workflow => workflowSource;

    public bool ModeAvailable(WanMode mode) => backend.ModeAvailable(mode);

    public Task<object> StatusAsync(string storage, CancellationToken ct) => backend.StatusAsync(storage, ct);

    public IAsyncEnumerable<WorkItem> DequeueAllAsync(CancellationToken ct) => _queue.Reader.ReadAllAsync(ct);

    // ---- submit
    public async Task<Batch> SubmitAsync(SubmitRequest request, CancellationToken ct = default)
    {
        Validate(request);
        backend.EnsureCanSubmit(request);
        var mode = WanModes.Get(request.Mode);
        await workflowSource.LoadAsync(mode.Template, ct); // fail fast with a clear message when no workflow file exists
        var (width, height) = Aspects[request.AspectRatio];
        await _submitGate.WaitAsync(ct); // makes the open-jobs check and the registration below one step
        try
        {
            Prune();
            if (OpenJobs().Count + request.Prompts.Count * request.Takes > Math.Max(1, options.Value.MaxOpenJobs))
            {
                throw new ConflictException("Too many unfinished jobs. Wait for some to finish.");
            }

            return await EnqueueAsync(request, mode, width, height, ct);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    private async Task<Batch> EnqueueAsync(SubmitRequest request, WanMode mode, int width, int height, CancellationToken ct)
    {
        var batch = new Batch { Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_'), Total = request.Prompts.Count * request.Takes };
        var work = new List<WorkItem>(batch.Total);
        for (var scene = 1; scene <= request.Prompts.Count; scene++)
        {
            var image = request.Images.ElementAtOrDefault(scene - 1) ?? request.Images[0]!;
            var title = request.Names?.ElementAtOrDefault(scene - 1) is { Length: > 0 } custom ? custom : $"{Path.GetFileNameWithoutExtension(image.Name)} Scene {scene}";
            for (var take = 1; take <= request.Takes; take++)
            {
                var job = new GenerationJob
                {
                    Name = backend.NewJobName(),
                    DisplayName = request.Takes > 1 ? $"{title} Take {take}" : title,
                    SubmittedAt = Now,
                    Scene = scene,
                    Take = take,
                    Mode = mode.Id,
                };
                batch.Jobs.Add(job);
                work.Add(new WorkItem(job, request.Prompts[scene - 1], image, request.NegativePrompt, request.DurationSeconds, width, height, mode));
            }
        }

        ct.ThrowIfCancellationRequested();
        // Commitment: caller cancellation can no longer leave an inaccessible partial batch.
        _batches[batch.Id] = batch;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var item in work)
        {
            _jobs[item.Job.Name] = item.Job;
            try
            {
                await records.SaveAsync(item.Job, limit.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                logger.LogWarning("Initial persistence budget expired for job {Job}; committed work will still be queued", item.Job.Name);
            }
        }

        foreach (var item in work)
        {
            // This unbounded channel is never completed; TryWrite cannot be cancelled.
            if (!_queue.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("The generation queue is unavailable.");
            }
        }
        return batch;
    }

    private static void Validate(SubmitRequest request)
    {
        if (request.Prompts.Count is < 1 or > MaxScenes || request.Prompts.Any(p => p.Length is < 1 or > 4000))
        {
            throw new ValidationException($"Add 1-{MaxScenes} scenes, each 1-4000 characters.");
        }

        if (request.NegativePrompt.Length > 4000)
        {
            throw new ValidationException("The negative prompt can be at most 4000 characters.");
        }

        if (request.Names?.Any(n => n.Length > MaxNameLength) == true)
        {
            throw new ValidationException($"Video names can be at most {MaxNameLength} characters.");
        }

        if (!Aspects.ContainsKey(request.AspectRatio))
        {
            throw new ValidationException("Choose a 16:9, 4:3 or 1:1 aspect ratio.");
        }

        if (!double.IsFinite(request.DurationSeconds) || request.DurationSeconds is < 5 or > 10 || !TakeChoices.Contains(request.Takes))
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
        return WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct), WorkflowValues.Build(options.Value, mode, "Describe the scene and movement.", string.Empty, "example.png", 1, width, height,
            WorkflowValues.Length(5.0, mode.Fps), "wan-studio/example"));
    }

    // ---- queries
    public Batch GetBatch(string id) => _batches.TryGetValue(id, out var batch) ? batch : throw new NotFoundException("This batch is not available.");

    public GenerationJob? FindJob(string name) => _jobs.GetValueOrDefault(name);

    public IReadOnlyList<GenerationJob> OpenJobs() => [.. _jobs.Values.Where(j => !JobStatus.IsTerminal(j.Status))];

    public Task<IReadOnlyList<object>> OpenJobsAsync(CancellationToken ct) => backend.OpenJobsAsync(OpenJobs(), ct);

    public async Task<GalleryResponse> GetGalleryAsync(CancellationToken ct = default)
    {
        var items = new List<GalleryItem>();
        foreach (var key in (await store.ListAsync(Library.TrimEnd('/'), ct)).Where(k => k.EndsWith("/meta.json", StringComparison.Ordinal)))
        {
            var data = await store.ReadAsync(key, ct);
            var meta = ParseDocument<GalleryMeta>(key, data);
            if (meta is null || string.IsNullOrWhiteSpace(meta.JobName) || string.IsNullOrWhiteSpace(meta.Video) ||
                meta.DisplayName is null || meta.Prompt is null || meta.Width <= 0 || meta.Height <= 0 ||
                !double.IsFinite(meta.DurationSeconds) || meta.DurationSeconds <= 0 || !double.IsFinite(meta.Created))
            {
                logger.LogWarning("Skipping damaged gallery document {Key}", key);
                continue;
            }
            items.Add(new GalleryItem(meta.JobName, meta.DisplayName, meta.Width, meta.Height, meta.DurationSeconds, meta.Prompt,
                $"/api/media/{Library}{meta.JobName}/{meta.Video}", meta.Created, meta.Timing, meta.Mode));
        }

        return new GalleryResponse([.. items.OrderByDescending(i => i.Created)], items.Count, _jobs.Count);
    }

    // ---- worker side
    /// <summary>After a restart: resume jobs the provider already accepted; fail the rest so clients stop waiting.</summary>
    public async Task<IReadOnlyList<GenerationJob>> RecoverAsync(CancellationToken ct = default)
    {
        var recovered = new List<GenerationJob>();
        foreach (var key in await store.ListAsync(JobsPrefix.TrimEnd('/'), ct))
        {
            var data = await store.ReadAsync(key, ct);
            var job = ParseDocument<GenerationJob>(key, data);
            if (job is null || string.IsNullOrWhiteSpace(job.Name) || job.DisplayName is null ||
                job.Status is not (JobStatus.Queued or JobStatus.Running or JobStatus.Completed or JobStatus.Failed) ||
                !double.IsFinite(job.SubmittedAt) ||
                job.Context is { } context && (context.Prompt is null || context.Width <= 0 || context.Height <= 0 ||
                    !double.IsFinite(context.Duration) || context.Duration <= 0))
            {
                logger.LogWarning("Skipping damaged job document {Key}", key);
                continue;
            }

            if (JobStatus.IsTerminal(job.Status))
            {
                if (job.SubmittedAt >= Now - Math.Max(0, options.Value.JobRetentionHours) * 3600)
                {
                    _jobs[job.Name] = job;
                }

                continue;
            }

            _jobs[job.Name] = job;

            if (backend.CanResume(job))
            {
                job.Status = JobStatus.Running;
                recovered.Add(job);
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.Error = "Interrupted by a restart before the job was accepted.";
                await records.SaveAsync(job, ct);
            }
        }

        return recovered;
    }

    private T? ParseDocument<T>(string key, byte[]? data) where T : class
    {
        try
        {
            return data is null ? null : Json.From<T>(data);
        }
        catch (JsonException error)
        {
            logger.LogWarning(error, "Skipping malformed document {Key}", key);
            return null;
        }
    }

    public async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        try
        {
            await backend.ResumeAsync(job, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Fail(job, error);
        }

        await SaveFinalAsync(job);
    }

    public async Task ProcessAsync(WorkItem item, CancellationToken ct)
    {
        var job = item.Job;
        job.Status = JobStatus.Running;
        job.AppQueueSeconds = Math.Round(Now - job.SubmittedAt, 3);
        try
        {
            await records.SaveAsync(job, ct);
            await backend.GenerateAsync(item, Now, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Fail(job, error); // one failed job must not stop the queue
        }

        job.Progress = null;
        await SaveFinalAsync(job);
    }

    private async Task SaveFinalAsync(GenerationJob job)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        try
        {
            await records.SaveAsync(job, limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            logger.LogError("Final persistence budget expired for generation job {Job}", job.Name);
            records.Publish(job);
        }
    }

    private void Fail(GenerationJob job, Exception error)
    {
        logger.LogError(error, "Job {Job} failed", job.Name);
        job.Status = JobStatus.Failed;
        job.Error = SafeMessage(error);
    }

    private static string SafeMessage(Exception error)
    {
        string text;
        switch (error)
        {
            case ISafeMessage or ConflictException or ValidationException:
                text = error.Message;
                break;
            case ComfyUiException:
                text = Regex.Replace(error.Message, @"(->\s*\d{3}):.*", "$1", RegexOptions.Singleline);
                break;
            default:
                return "Generation failed unexpectedly. The server log has the details.";
        }

        text = Regex.Replace(text, @"https?://\S+|/subscriptions/\S+|\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "[redacted]");
        return text.Length > 200 ? text[..200] : text;
    }
}
