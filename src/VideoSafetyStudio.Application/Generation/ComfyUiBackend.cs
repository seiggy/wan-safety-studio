using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Runs generations on a ComfyUI server, one at a time (the single GPU), and records stage timings from its websocket.</summary>
public sealed class ComfyUiBackend(
    IObjectStore store,
    IComfyUiClient comfy,
    IVideoRetrieval retrieval,
    IWorkflowSource workflowSource,
    IGenerationTelemetry telemetry,
    IEventPublisher events,
    JobRecords records,
    IOptions<GenerationOptions> options,
    TimeProvider time) : IGenerationBackend
{
    public string Provider => "comfyui";

    public int MaxConcurrency => 1;

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    public string NewJobName() => "local-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));

    public void EnsureCanSubmit(SubmitRequest request)
    {
    }

    public bool ModeAvailable(WanMode mode) => workflowSource.Exists(mode.Template);

    public async Task<object> StatusAsync(string storage, CancellationToken ct)
    {
        var up = await comfy.IsReachableAsync(ct);
        var prepared = workflowSource.Exists(WanModes.Get(null).Template);
        var ready = up && prepared;
        return new
        {
            mode = "local",
            workspace = "local-dev",
            resourceGroup = "n/a",
            storage,
            compute = $"ComfyUI {comfy.BaseUrl} ({(up ? "reachable" : "unreachable")})",
            computeState = up ? "Succeeded" : "Unreachable",
            prepared,
            version = (string?)null,
            armed = ready,
            generationEnabled = ready,
            checkedAt = DateTimeOffset.UtcNow,
            maxJobMinutes = 120,
            maxUploadMb = 512,
            profile = (object?)null,
        };
    }

    public Task<IReadOnlyList<object>> OpenJobsAsync(IReadOnlyList<GenerationJob> local, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<object>>([.. local]);

    public bool CanResume(GenerationJob job) => job.PromptId is not null && job.Context is not null;

    public async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        var entry = await comfy.WaitAsync(job.PromptId!, ct);
        await FinishAsync(job, entry, job.Context!, null, null, ct);
    }

    public async Task GenerateAsync(WorkItem item, double started, CancellationToken ct)
    {
        var job = item.Job;
        var mode = item.Mode;
        double? queuedAt = null;
        var seed = WorkflowValues.NewSeed();
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
            length = WorkflowValues.Length(item.Duration, mode.Fps);
            var workflow = WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct),
                WorkflowValues.Build(options.Value, mode, item.Prompt, item.Negative, image, seed, item.Width, item.Height, length.Value, $"wan-studio/{job.Name}"));
            timer = new StageTimer(workflow);
            job.Context = new JobContext(item.Prompt, item.Width, item.Height, item.Duration, seed);
            var running = timer;
            var entry = await Phase("comfyui", () => comfy.RunAsync(workflow, running, promptId =>
            {
                queuedAt = Now;
                job.PromptId = promptId;
                _ = records.SaveAsync(job, CancellationToken.None);
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

    private async Task<long> FinishAsync(GenerationJob job, JsonNode entry, JobContext context, Func<TimingSummary>? timing, Dictionary<string, double>? phases, CancellationToken ct)
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
        await store.WriteAsync($"{GenerationService.Library}{job.Name}/{name}", data, true, ct);
        phases["store_video"] = Math.Round(Now - began, 3);
        var meta = new GalleryMeta(job.Name, job.DisplayName, context.Width, context.Height, context.Duration, context.Prompt, context.Seed, name, Now, timing?.Invoke(), job.Mode);
        await store.WriteAsync($"{GenerationService.Library}{job.Name}/meta.json", Json.ToBytes(meta), true, ct);
        return data.Length;
    }
}
