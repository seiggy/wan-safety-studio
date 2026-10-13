using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>An unfinished studio job in the workspace, whoever submitted it.</summary>
public sealed record AzureMlOpenJob(
    string Name,
    [property: JsonPropertyName("display_name")] string? DisplayName,
    string Status,
    [property: JsonPropertyName("studio_url")] string? StudioUrl,
    [property: JsonPropertyName("created_at")] string? CreatedAt);

/// <summary>Runs generations as Azure ML command jobs on the studio GPU node and reads the finished video from the datastore container.</summary>
public sealed class AzureMlBackend(
    IObjectStore store,
    IAzureMlClient client,
    AzureMlFoundation foundation,
    AzureMlRelease? release,
    AzureMlGate gate,
    IWorkflowSource workflowSource,
    IGenerationTelemetry telemetry,
    JobRecords records,
    IOptions<GenerationOptions> generationOptions,
    IOptions<AzureMlOptions> options,
    TimeProvider time,
    ILogger<AzureMlBackend>? logger = null) : IGenerationBackend
{
    private readonly ILogger<AzureMlBackend> logger = logger ?? NullLogger<AzureMlBackend>.Instance;

    public const string InputsFolder = "studio/web-inputs/";
    private const int OpenJobScan = 200;
    private const int MaxPollFailures = 20;
    private static readonly TimeSpan StatusCacheFor = TimeSpan.FromSeconds(15);

    private sealed record CachedStatus(DateTimeOffset At, object Value);

    private volatile CachedStatus? _status;

    public string Provider => "azureml";

    public int MaxConcurrency => Math.Max(1, options.Value.MaxConcurrency);

    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    private TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(0.001, options.Value.PollSeconds));

    public string NewJobName() => "wan-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public void EnsureCanSubmit(SubmitRequest request)
    {
        if (release is null)
        {
            throw new ConflictException("Azure ML generation is not prepared: AzureMl:Release:EnvironmentId is not set.");
        }

        if (!gate.IsArmed(release))
        {
            throw new ConflictException("Generation is not armed. Start the studio compute first.");
        }

        AzureMlImage.Check(request.Images, options.Value.MaxUploadMb * 1024L * 1024);
    }

    public bool ModeAvailable(WanMode mode) => release is not null && workflowSource.Exists(mode.Template);

    public async Task<object> StatusAsync(string storage, CancellationToken ct)
    {
        if (_status is { } cached && time.GetUtcNow() - cached.At < StatusCacheFor)
        {
            return cached.Value;
        }

        string computeState;
        try
        {
            computeState = await client.GetComputeStateAsync(ct) ?? "Not configured";
        }
        catch (Exception error) when (error is AzureMlException or HttpRequestException or Azure.Identity.AuthenticationFailedException)
        {
            computeState = "Unavailable";
        }

        var armed = gate.IsArmed(release);
        object value = new
        {
            mode = "azureml",
            workspace = foundation.WorkspaceName,
            resourceGroup = foundation.ResourceGroupName,
            storage = foundation.StorageAccountName,
            compute = foundation.ComputeName,
            computeState,
            prepared = release is not null,
            version = release?.Version,
            armed,
            generationEnabled = armed && computeState == "Succeeded",
            checkedAt = time.GetUtcNow(),
            maxJobMinutes = options.Value.MaxJobMinutes,
            maxUploadMb = options.Value.MaxUploadMb,
            profile = new { label = "Wan 2.2 image to video", fps = 16, width = 768, height = 768, durationSeconds = 5, durationStep = 0.25 },
        };
        _status = new CachedStatus(time.GetUtcNow(), value);
        return value;
    }

    public async Task<IReadOnlyList<object>> OpenJobsAsync(IReadOnlyList<GenerationJob> local, CancellationToken ct)
    {
        var found = new List<object>();
        foreach (var job in await client.ListJobsAsync(OpenJobScan, ct))
        {
            var properties = job["properties"];
            var status = properties?["status"]?.GetValue<string>() ?? string.Empty;
            if (properties?["experimentName"]?.GetValue<string>() != AzureMlJobSpecBuilder.Experiment || AzureMlStatus.IsTerminal(status))
            {
                continue;
            }

            var name = job["name"]!.GetValue<string>();
            found.Add(new AzureMlOpenJob(name, properties["displayName"]?.GetValue<string>(), status, StudioUrl(job, name), job["systemData"]?["createdAt"]?.GetValue<string>()));
        }

        return found;
    }

    private string StudioUrl(JsonNode job, string name)
    {
        if (job["properties"]?["services"]?["Studio"]?["endpoint"]?.GetValue<string>() is { } endpoint &&
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "ml.azure.com")
        {
            return endpoint;
        }

        var scope = $"/subscriptions/{foundation.SubscriptionId}/resourcegroups/{foundation.ResourceGroupName}/workspaces/{foundation.WorkspaceName}";
        return $"https://ml.azure.com/runs/{Uri.EscapeDataString(name)}?wsid={scope}&tid={foundation.TenantId}";
    }

    public bool CanResume(GenerationJob job) => job.RemoteId is not null && job.Context is not null;

    public async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        await PollAsync(job, Now, new Dictionary<string, double>(), ct);
        await FinishAsync(job, job.Context!, null, ct);
    }

    public async Task GenerateAsync(WorkItem item, double started, CancellationToken ct)
    {
        var job = item.Job;
        var current = release ?? throw new ConflictException("Azure ML generation is not prepared.");
        var mode = item.Mode;
        var phases = new Dictionary<string, double> { ["waiting"] = Math.Round(started - job.SubmittedAt, 3) };
        var seed = WorkflowValues.NewSeed();
        var length = WorkflowValues.Length(item.Duration, mode.Fps);
        var created = 0.0;
        double? runStart = null;
        var finished = 0.0;
        var ok = false;
        try
        {
            var began = Now;
            var imageName = AzureMlImage.SafeName(item.Image.Name);
            var stem = $"{InputsFolder}{time.GetUtcNow():yyyyMMdd}/{time.GetUtcNow():yyyyMMddHHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5))}";
            var imageKey = $"{stem}-{imageName}";
            var workflowKey = $"{stem}/workflow.json";
            var workflow = WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct),
                WorkflowValues.Build(generationOptions.Value, mode, item.Prompt, item.Negative, imageName, seed, item.Width, item.Height, length, "ComfyUI"));
            await store.WriteAsync(imageKey, item.Image.Data, true, ct);
            await store.WriteAsync(workflowKey, System.Text.Encoding.UTF8.GetBytes(workflow.ToJsonString()), true, ct);
            phases["upload_inputs"] = Math.Round(Now - began, 3);

            var spec = AzureMlJobSpecBuilder.Build(foundation, current, job.Name, $"comfyui-wan22-video-{time.GetUtcNow():yyyyMMddHHmmss}-s{job.Scene}t{job.Take}",
                foundation.DatastorePath(imageKey), foundation.DatastorePath(workflowKey),
                new AzureMlRunSettings(item.Prompt, item.Negative, seed, mode.Steps, mode.Cfg, mode.Sampler, item.Width, item.Height, item.Duration, length, mode.Fps, imageName), options.Value.MaxJobMinutes * 60);
            job.Context = new JobContext(item.Prompt, item.Width, item.Height, item.Duration, seed);

            began = Now;
            await SubmitAsync(job, spec, current, ct);
            created = Now;
            phases["submit"] = Math.Round(created - began, 3);

            runStart = await PollAsync(job, created, phases, ct);
            finished = Now;
            phases["run"] = Math.Round(finished - (runStart ?? created), 3);
            await FinishAsync(job, job.Context, () => BuildTiming(job, phases, started, length, item, null, "success"), ct);
            ok = true;
        }
        finally
        {
            if (created > 0)
            {
                finished = Now;
                job.Timing = BuildTiming(job, phases, started, length, item, finished, ok ? "success" : "error");
                var spans = new List<PhaseSpan> { new("waiting", job.SubmittedAt, started) };
                if (runStart is { } running)
                {
                    spans.Add(new PhaseSpan("azureml_queue", created, running));
                    spans.Add(new PhaseSpan("run", running, finished));
                }

                telemetry.Emit(job.Name, job.Timing.Params!, new StageTimer(new JsonObject()), phases, spans, job.SubmittedAt, finished, ok);
            }
        }
    }

    private async Task SubmitAsync(GenerationJob job, AzureMlJobSpec spec, AzureMlRelease current, CancellationToken ct)
    {
        AzureMlGuard.Validate(spec, foundation, current, gate.IsArmed(current)); // re-checked per job: Stop halts a batch
        var createdJob = await client.CreateJobAsync(spec, CancellationToken.None); // never retried; not cancelled halfway
        try
        {
            AzureMlGuard.VerifyCreated(await client.GetJobAsync(spec.Name, ct), spec, foundation);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try
            {
                await client.CancelJobAsync(spec.Name, CancellationToken.None);
            }
            catch (Exception)
            {
                // best effort: the failure below tells the operator to run Stop
            }

            throw new AzureMlSafetyException("Server job safety could not be proven; run Stop to verify compute release.");
        }

        job.RemoteId = spec.Name;
        job.StudioUrl = StudioUrl(createdJob, spec.Name);
        await records.SaveAsync(job, ct);
    }

    /// <summary>Polls until the job ends; returns when it started running (null if never seen running). Throws unless it completed.
    /// If polling gives up while the remote job may still be live, cancels it (best effort); a shutdown does not, so a restart can resume.</summary>
    private async Task<double?> PollAsync(GenerationJob job, double created, Dictionary<string, double> phases, CancellationToken ct)
    {
        var ended = false;
        try
        {
            return await PollLoopAsync(job, created, phases, ct, () => ended = true);
        }
        catch (Exception error) when (error is not OperationCanceledException && !ended)
        {
            logger.LogWarning(error, "Polling Azure ML job {Job} gave up; cancelling it", job.RemoteId);
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.CancelJobAsync(job.RemoteId!, limit.Token);
            }
            catch (Exception cancelError)
            {
                logger.LogWarning(cancelError, "Could not cancel Azure ML job {Job}; run Stop to verify compute release", job.RemoteId);
            }

            throw;
        }
    }

    private async Task<double?> PollLoopAsync(GenerationJob job, double created, Dictionary<string, double> phases, CancellationToken ct, Action markEnded)
    {
        double? runStart = null;
        var failures = 0;
        var last = string.Empty;
        while (true)
        {
            string status;
            try
            {
                status = (await client.GetJobAsync(job.RemoteId!, ct))["properties"]?["status"]?.GetValue<string>() ?? string.Empty;
                failures = 0;
            }
            catch (Exception error) when (error is AzureMlException { Transient: true } or HttpRequestException && ++failures < MaxPollFailures)
            {
                await Task.Delay(Poll, time, ct);
                continue;
            }

            if (status != last)
            {
                last = status;
                if (AzureMlStatus.IsRunning(status) && runStart is null)
                {
                    runStart = Now;
                    phases["azureml_queue"] = Math.Round(runStart.Value - created, 3);
                }

                var mapped = AzureMlStatus.ToJobStatus(status);
                if (!JobStatus.IsTerminal(mapped) && mapped != job.Status)
                {
                    job.Status = mapped;
                    await records.SaveAsync(job, ct);
                }
            }

            if (AzureMlStatus.IsTerminal(status))
            {
                markEnded();
                return status == "Completed" ? runStart : throw new AzureMlException($"Azure ML job ended as {status}.");
            }

            await Task.Delay(Poll, time, ct);
        }
    }

    private TimingSummary BuildTiming(GenerationJob job, Dictionary<string, double> phases, double started, int length, WorkItem item, double? finished, string outcome) =>
        new(outcome, phases.GetValueOrDefault("run"), new Dictionary<string, StageSummary>(), new Dictionary<string, double>(phases),
            Math.Round((finished ?? Now) - started, 3), job.AppQueueSeconds,
            new GenerationParams(item.Width, item.Height, length, item.Duration, "azureml", item.Mode.Id, item.Mode.Steps));

    /// <summary>Finds the video the job wrote and adds meta.json beside it; the library serves it from where it already is.</summary>
    private async Task FinishAsync(GenerationJob job, JobContext context, Func<TimingSummary>? timing, CancellationToken ct)
    {
        var folder = $"{GenerationService.Library}{job.Name}/";
        string? video = null;
        for (var attempt = 0; attempt < 4 && video is null; attempt++)
        {
            video = AzureMlOutput.PickVideo((await store.ListAsync(folder.TrimEnd('/'), ct)).Select(key => key[folder.Length..]));
            if (video is null && attempt < 3)
            {
                await Task.Delay(Poll, time, ct);
            }
        }

        if (video is null)
        {
            throw new AzureMlException("Azure ML finished but produced no video output.");
        }

        var meta = new GalleryMeta(job.Name, job.DisplayName, context.Width, context.Height, context.Duration, context.Prompt, context.Seed, video, Now, timing?.Invoke(), job.Mode);
        await store.WriteAsync($"{folder}meta.json", Json.ToBytes(meta), true, ct);
    }
}
