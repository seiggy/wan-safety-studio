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

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

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
        var batch = new Batch { Id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_'), Total = request.Prompts.Count * request.Takes };
        for (var scene = 1; scene <= request.Prompts.Count; scene++)
        {
            var image = request.Images[scene - 1] ?? request.Images[0]!;
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
                _jobs[job.Name] = job;
                batch.Jobs.Add(job);
                await records.SaveAsync(job, ct);
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

        if (request.Names?.Any(n => n.Length > MaxNameLength) == true)
        {
            throw new ValidationException($"Video names can be at most {MaxNameLength} characters.");
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
            var meta = Json.From<GalleryMeta>((await store.ReadAsync(key, ct))!)!;
            items.Add(new GalleryItem(meta.JobName, meta.DisplayName, meta.Width, meta.Height, meta.DurationSeconds, meta.Prompt,
                $"/api/media/{Library}{meta.JobName}/{meta.Video}", meta.Created, meta.Timing, meta.Mode));
        }

        return new GalleryResponse([.. items.OrderByDescending(i => i.Created)], items.Count, _jobs.Count);
    }

    // ---- worker side
    /// <summary>After a restart: resume jobs the provider already accepted; fail the rest so clients stop waiting.</summary>
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

            if (backend.CanResume(job))
            {
                job.Status = JobStatus.Running;
                _ = Task.Run(() => ResumeAsync(job, ct), CancellationToken.None);
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.Error = "Interrupted by a restart before the job was accepted.";
                await records.SaveAsync(job, ct);
            }
        }
    }

    private async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        try
        {
            await backend.ResumeAsync(job, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(job, error);
        }

        await records.SaveAsync(job, CancellationToken.None);
    }

    public async Task ProcessAsync(WorkItem item, CancellationToken ct)
    {
        var job = item.Job;
        job.Status = JobStatus.Running;
        job.AppQueueSeconds = Math.Round(Now - job.SubmittedAt, 3);
        await records.SaveAsync(job, ct);
        try
        {
            await backend.GenerateAsync(item, Now, ct);
            job.Status = JobStatus.Completed;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(job, error); // one failed job must not stop the queue
        }

        job.Progress = null;
        await records.SaveAsync(job, CancellationToken.None);
    }

    private void Fail(GenerationJob job, Exception error)
    {
        logger.LogError(error, "Job {Job} failed", job.Name);
        job.Status = JobStatus.Failed;
        job.Error = error.Message.Length > 300 ? error.Message[..300] : error.Message;
    }
}
