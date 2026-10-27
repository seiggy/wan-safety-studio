using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>One region's Azure ML workspace and private run-data store, derived from the validated foundation receipt.</summary>
public sealed record RegionContext(string Key, RegionalFoundation Region, AzureMlFoundation Foundation, IAzureMlClient Client, IObjectStore Store)
{
    public static AzureMlFoundation Derive(StudioFoundation foundation, RegionalFoundation region) => new(
        foundation.Scope.SubscriptionId, foundation.Scope.TenantId, region.WorkspaceId.Split('/')[4], StudioResourceNames.NameOf(region.WorkspaceId),
        StudioResourceNames.NameOf(region.ComputeId), region.ComputeIdentityClientId, StudioResourceNames.NameOf(region.StorageAccountId),
        region.Container, region.Datastore, region.ReleaseContainer, region.ReleaseDatastore);
}

public sealed record ResolvedRelease(AzureMlRelease Release, string ArtifactId);

public interface IRegionalReleases
{
    /// <summary>The verified release a region is armed with, or null when it has none. Never acquires or registers anything.</summary>
    Task<ResolvedRelease?> ResolveAsync(string region, CancellationToken ct);
}

/// <summary>Reads the regional binding the pipeline published and the host's durable fence selected; both must validate against the cached scope.</summary>
public sealed class RegionalReleaseResolver(StudioFoundation foundation, IStudioReleaseReader reader, StudioArmedGate gate) : IRegionalReleases
{
    private readonly ConcurrentDictionary<(string, string), ResolvedRelease> cache = new();

    public async Task<ResolvedRelease?> ResolveAsync(string region, CancellationToken ct)
    {
        if (gate.ArmedArtifact(region) is not { } artifact) return null;
        if (cache.TryGetValue((region, artifact), out var known)) return known;
        var build = await reader.ReadAsync(region, $"builds/{artifact}/manifest.json", ct);
        var binding = await reader.ReadAsync(region, $"releases/{artifact}/{StudioContracts.RegionFingerprint(foundation, region)}.json", ct);
        if (build is null || binding is null) return null;
        var manifest = StudioContracts.ReadBuild(build);
        var release = StudioContracts.ReadRelease(binding, foundation, manifest);
        if (release.Region != region || manifest.ArtifactId != artifact) throw new ConflictException("The regional release binding belongs to a different region or build.");
        var resolved = new ResolvedRelease(new AzureMlRelease(release.EnvironmentId, release.ModelsRef, "model", release.CodeUri, artifact[..12]), artifact);
        cache[(region, artifact)] = resolved;
        return resolved;
    }
}

/// <summary>
/// Library reads span every regional run-data store, because a video lives in the region of the attempt that produced it. A job is pinned to the
/// region its attempt record names; unpinned (older) videos are found by where the listing or a probe actually locates the bytes, never from settings.
/// Every write goes to the control store (the backend writes regional outputs through its own explicit regional handle).
/// </summary>
public sealed class RegionalLibraryStore(IObjectStore control, IReadOnlyDictionary<string, IObjectStore> regions, string controlRegion) : IObjectStore
{
    private const string Library = "video-library";
    private readonly ConcurrentDictionary<string, IObjectStore> pins = new(StringComparer.Ordinal);

    private IEnumerable<IObjectStore> Order => regions.OrderBy(pair => pair.Key == controlRegion ? 0 : 1).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value);

    public void Pin(string job, string region)
    {
        if (regions.TryGetValue(region, out var store)) pins[job] = store;
    }

    private static bool IsLibrary(string key) => key == Library || key.StartsWith(Library + "/", StringComparison.Ordinal);

    private static string? JobOf(string key)
    {
        var parts = key.Split('/', 3);
        return parts.Length >= 2 && parts[0] == Library && parts[1].Length > 0 ? parts[1] : null;
    }

    private IEnumerable<IObjectStore> Candidates(string key) =>
        JobOf(key) is { } job && pins.TryGetValue(job, out var pinned) ? [pinned] : Order;

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (!IsLibrary(key)) return await control.ExistsAsync(key, ct);
        foreach (var store in Candidates(key)) if (await store.ExistsAsync(key, ct)) return true;
        return false;
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        if (!IsLibrary(key)) return await control.ReadAsync(key, ct);
        foreach (var store in Candidates(key)) if (await store.ReadAsync(key, ct) is { } data) return data;
        return null;
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        if (!IsLibrary(key)) return await control.OpenReadAsync(key, ct);
        foreach (var store in Candidates(key)) if (await store.OpenReadAsync(key, ct) is { } stream) return stream;
        return null;
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!IsLibrary(prefix)) return await control.ListAsync(prefix, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<string>();
        foreach (var store in Order)
        {
            foreach (var key in await store.ListAsync(prefix, ct))
            {
                if (!seen.Add(key)) continue; // the first (control-preferred) holder of a key wins
                all.Add(key);
                if (JobOf(key) is { } job) pins.TryAdd(job, store);
            }
        }

        return all;
    }

    public Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default) => control.WriteAsync(key, data, overwrite, ct);

    public Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default) => control.UploadAsync(key, path, overwrite, ct);

    public async Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        if (!IsLibrary(key)) { await control.DownloadAsync(key, path, ct); return; }
        foreach (var store in Candidates(key))
        {
            if (!await store.ExistsAsync(key, ct)) continue;
            await store.DownloadAsync(key, path, ct);
            return;
        }

        throw new FileNotFoundException("Library item not found.");
    }
}

public sealed record RegionalTimings(TimeSpan CancelProof)
{
    public static RegionalTimings Default { get; } = new(TimeSpan.FromMinutes(2));
}

/// <summary>
/// Runs each generation as at most a sequence of Azure ML command jobs, one live attempt at a time, across regional workspaces.
/// A job still waiting for the node past the queue-wait threshold may move once to another eligible region, but only after its
/// cancellation is proven by a fresh read; started, completed or uncertain attempts are never replayed or duplicated.
/// </summary>
public sealed class RegionalAzureMlBackend(
    StudioFoundation foundation,
    IReadOnlyDictionary<string, RegionContext> regions,
    IRegionalReleases releases,
    StudioArmedGate gate,
    StudioSettingsSource settings,
    RegionalAdmission admission,
    AttemptLedger ledgerStore,
    RegionalLibraryStore library,
    IWorkflowSource workflowSource,
    IGenerationTelemetry telemetry,
    JobRecords records,
    IOptions<GenerationOptions> generationOptions,
    IOptions<AzureMlOptions> options,
    TimeProvider time,
    RegionalTimings timings,
    ILogger<RegionalAzureMlBackend>? logger = null) : IGenerationBackend
{
    private static readonly ActivitySource Source = new("VideoSafetyStudio.Operations");
    private static readonly TimeSpan StatusCacheFor = TimeSpan.FromSeconds(15);
    public const string LegacyArtifactId = "0000000000000000000000000000000000000000000000000000000000000000";
    private const int OpenJobScan = 200;
    private const int MaxPollFailures = 20;

    private readonly ILogger<RegionalAzureMlBackend> log = logger ?? NullLogger<RegionalAzureMlBackend>.Instance;
    private sealed record CachedStatus(DateTimeOffset At, object Value);
    private volatile CachedStatus? status;

    public string Provider => "azureml";

    /// <summary>Worker slots equal the deployed ceiling; the settings' global and regional limits are enforced by admission, so lowering them never needs a restart.</summary>
    public int MaxConcurrency => Math.Max(1, foundation.Regions.Values.Sum(region => region.Policy.MaxNodes));

    public string NewJobName() => "wan-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    private DateTimeOffset UtcNow => time.GetUtcNow();

    private double Now => UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private TimeSpan Poll => TimeSpan.FromSeconds(Math.Max(0.001, options.Value.PollSeconds));

    public void EnsureCanSubmit(SubmitRequest request)
    {
        if (!foundation.Regions.Keys.Any(gate.IsArmed))
        {
            throw new ConflictException("Generation is not armed in any region. Start a region's compute first.");
        }

        AzureMlImage.Check(request.Images, options.Value.MaxUploadMb * 1024L * 1024);
    }

    public bool ModeAvailable(WanMode mode) => workflowSource.Exists(mode.Template) && foundation.Regions.Keys.Any(gate.HasSelection);

    // ---- status and open jobs --------------------------------------------------------------------------------------------

    public async Task<object> StatusAsync(string storage, CancellationToken ct)
    {
        if (status is { } cached && UtcNow - cached.At < StatusCacheFor) return cached.Value;
        var current = settings.Current;
        var rows = new List<object>();
        var controlState = "Not configured";
        var enabled = false;
        foreach (var key in foundation.Regions.Keys.Order(StringComparer.Ordinal))
        {
            string state;
            try
            {
                state = await regions[key].Client.GetComputeStateAsync(ct) ?? "Not configured";
            }
            catch (Exception error) when (error is AzureMlException or HttpRequestException or Azure.Identity.AuthenticationFailedException)
            {
                state = "Unavailable";
            }

            if (key == foundation.ControlRegion) controlState = state;
            enabled |= gate.IsArmed(key) && state == "Succeeded";
            var regional = current.Regions[key];
            rows.Add(new
            {
                region = key, armed = gate.IsArmed(key), prepared = gate.ArmedArtifact(key) is not null || gate.HasSelection(key), computeState = state,
                activeJobs = admission.Active(key), maxConcurrentJobs = regional.MaxConcurrentJobs, maxJobMinutes = regional.MaxJobMinutes, priority = regional.Priority,
            });
        }

        var anyArmed = foundation.Regions.Keys.Any(gate.IsArmed);
        var control = regions[foundation.ControlRegion].Foundation;
        object value = new
        {
            mode = "azureml", workspace = control.WorkspaceName, resourceGroup = control.ResourceGroupName, storage = control.StorageAccountName, compute = control.ComputeName,
            computeState = controlState, prepared = foundation.Regions.Keys.Any(gate.HasSelection), version = gate.ArmedArtifact(foundation.ControlRegion)?[..12],
            armed = anyArmed, generationEnabled = enabled,
            checkedAt = UtcNow, maxJobMinutes = current.Regions.Values.Max(item => item.MaxJobMinutes), maxUploadMb = options.Value.MaxUploadMb,
            maxConcurrentJobs = current.MaxConcurrentJobs, queueMoveMinutes = current.QueueMoveMinutes, regions = rows,
            profile = new { label = "Wan 2.2 image to video", fps = 16, width = 768, height = 768, durationSeconds = 5, durationStep = 0.25 },
        };
        status = new CachedStatus(UtcNow, value);
        return value;
    }

    public async Task<IReadOnlyList<object>> OpenJobsAsync(IReadOnlyList<GenerationJob> local, CancellationToken ct)
    {
        var found = new List<object>();
        foreach (var (key, context) in regions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            IReadOnlyList<JsonNode> jobs;
            try
            {
                jobs = await context.Client.ListJobsAsync(OpenJobScan, ct);
            }
            catch (Exception error) when (error is AzureMlException or HttpRequestException)
            {
                log.LogWarning(error, "Open Azure ML jobs for {Region} could not be listed.", key);
                continue;
            }

            foreach (var job in jobs)
            {
                var properties = job["properties"];
                var state = properties?["status"]?.GetValue<string>() ?? string.Empty;
                if (properties?["experimentName"]?.GetValue<string>() != AzureMlJobSpecBuilder.Experiment || AzureMlStatus.IsTerminal(state)) continue;
                var name = job["name"]!.GetValue<string>();
                found.Add(new AzureMlOpenJob(name, properties["displayName"]?.GetValue<string>(), state, StudioUrl(context.Foundation, job, name), job["systemData"]?["createdAt"]?.GetValue<string>()));
            }
        }

        return found;
    }

    private static string StudioUrl(AzureMlFoundation context, JsonNode job, string name)
    {
        if (job is JsonObject root && root["properties"] is JsonObject properties && properties["services"] is JsonObject services && services["Studio"] is JsonObject studio &&
            studio["endpoint"] is JsonValue value && value.TryGetValue<string>(out var endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.Host == "ml.azure.com")
        {
            return endpoint;
        }

        var scope = $"/subscriptions/{context.SubscriptionId}/resourcegroups/{context.ResourceGroupName}/workspaces/{context.WorkspaceName}";
        return $"https://ml.azure.com/runs/{Uri.EscapeDataString(name)}?wsid={scope}&tid={context.TenantId}";
    }

    // ---- entry points ----------------------------------------------------------------------------------------------------

    public bool CanResume(GenerationJob job) => job.RemoteId is not null && job.Context is not null;

    public async Task GenerateAsync(WorkItem item, double started, CancellationToken ct)
    {
        var job = item.Job;
        var imageName = AzureMlImage.SafeName(item.Image.Name);
        job.Context = new JobContext(item.Prompt, item.Width, item.Height, item.Duration, WorkflowValues.NewSeed(), item.Negative, imageName);
        var phases = new Dictionary<string, double> { ["waiting"] = Math.Round(started - job.SubmittedAt, 3) };
        var ok = false;
        try
        {
            await RunAsync(job, item, null, phases, attempt => BuildTiming(job, item, phases, started, attempt, "success"), ct);
            ok = true;
        }
        finally
        {
            if (job.RemoteId is not null)
            {
                var last = (await TryLoadAsync(job.Name))?.Attempts.LastOrDefault();
                job.Timing = BuildTiming(job, item, phases, started, last, ok ? "success" : "error");
                var finished = Now;
                var created = last?.CreatedAt.ToUnixTimeMilliseconds() / 1000.0 ?? finished;
                var runStart = last?.StartedAt?.ToUnixTimeMilliseconds() / 1000.0;
                var spans = new List<PhaseSpan> { new("waiting", job.SubmittedAt, started), new("azureml_queue", created, runStart ?? finished) };
                if (runStart is { } running) spans.Add(new PhaseSpan("run", running, finished));
                telemetry.Emit(job.Name, job.Timing.Params!, new StageTimer(new JsonObject()), phases, spans, job.SubmittedAt, finished, ok);
            }
        }
    }

    /// <summary>Phases of the attempt that produced the result; earlier cancelled attempts add only to the total.</summary>
    private TimingSummary BuildTiming(GenerationJob job, WorkItem item, Dictionary<string, double> phases, double started, JobAttempt? attempt, string outcome)
    {
        var finished = Now;
        var created = attempt?.CreatedAt.ToUnixTimeMilliseconds() / 1000.0 ?? finished;
        var runStart = attempt?.StartedAt?.ToUnixTimeMilliseconds() / 1000.0;
        phases["azureml_queue"] = Math.Round((runStart ?? finished) - created, 3);
        phases["run"] = Math.Round(finished - (runStart ?? created), 3);
        var mode = item.Mode;
        return new TimingSummary(outcome, phases["run"], new Dictionary<string, StageSummary>(), new Dictionary<string, double>(phases), Math.Round(finished - started, 3), job.AppQueueSeconds,
            new GenerationParams(item.Width, item.Height, WorkflowValues.Length(item.Duration, mode.Fps), item.Duration, "azureml", mode.Id, mode.Steps));
    }

    private async Task<StudioJobRecord?> TryLoadAsync(string name)
    {
        try
        {
            return await ledgerStore.LoadAsync(name, CancellationToken.None);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task ResumeAsync(GenerationJob job, CancellationToken ct)
    {
        var ledger = await ledgerStore.LoadAsync(job.Name, ct);
        if (ledger is null && job.Region is { } named)
        {
            // The job record was saved but the intent never was: the remote job cannot have been created. Verify, do not resubmit.
            var absent = await TryGetAsync(regions[named], job.RemoteId!, ct) is null;
            throw new ConflictException(absent ? "The job was not accepted before the host stopped; it was not resubmitted."
                : "An unrecorded remote job exists for this studio job; run Stop and verify release before submitting again.");
        }

        ledger ??= await ledgerStore.SaveAsync(Legacy(job), ct);
        await RunAsync(job, null, ledger, new Dictionary<string, double>(), null, ct);
    }

    /// <summary>A job accepted before regional routing ran in the control region; it is adopted as one recorded attempt with an explicit marker artifact.</summary>
    private StudioJobRecord Legacy(GenerationJob job)
    {
        var region = foundation.ControlRegion;
        var context = regions[region];
        var created = DateTimeOffset.FromUnixTimeMilliseconds((long)(job.SubmittedAt * 1000)).ToUniversalTime();
        var attempt = new JobAttempt(1, region, context.Region.WorkspaceId, job.RemoteId!, LegacyArtifactId, context.Region.Policy.MaxJobMinutes * 60, AttemptPhase.Accepted,
            created, UtcNow > created ? UtcNow : created, null, null, context.Foundation.DatastorePath($"video-library/{job.RemoteId}/"));
        job.Region = region;
        return new StudioJobRecord(1, 0, foundation.Scope, job.Name, [attempt]);
    }

    // ---- the attempt loop ------------------------------------------------------------------------------------------------

    private async Task<StudioJobRecord> RunAsync(
        GenerationJob job, WorkItem? item, StudioJobRecord? ledger, Dictionary<string, double> phases, Func<JobAttempt, TimingSummary?>? timing, CancellationToken ct)
    {
        string? reserved = null;
        try
        {
            while (true)
            {
                var last = ledger?.Attempts[^1];
                if (last is { Phase: AttemptPhase.Completed })
                {
                    await FinishAsync(job, regions[last.Region], timing?.Invoke(last), ct);
                    return ledger!;
                }

                if (last is { Phase: AttemptPhase.Failed })
                {
                    throw new AzureMlException("Azure ML job ended as Failed.");
                }

                if (last is null || last.Phase == AttemptPhase.Canceled)
                {
                    if (last?.StartedAt is not null) throw new AzureMlException("Azure ML job ended as Canceled.");
                    reserved ??= await ReserveAsync(job, ledger, ct);
                    try
                    {
                        ledger = await SubmitAttemptAsync(job, item, ledger, reserved, phases, ct);
                    }
                    catch
                    {
                        admission.Release(reserved);
                        reserved = null;
                        throw;
                    }

                    reserved = null; // the attempt now owns the slot; FollowAsync releases it
                }
                else
                {
                    admission.Force(last.Region); // resumed remote work always counts, whatever the limits now say
                }

                var (done, followed, next) = await FollowAsync(job, ledger!, phases, ct);
                ledger = followed;
                reserved = next;
                if (done)
                {
                    await FinishAsync(job, regions[ledger.Attempts[^1].Region], timing?.Invoke(ledger.Attempts[^1]), ct);
                    return ledger;
                }
            }
        }
        finally
        {
            if (reserved is not null) admission.Release(reserved);
        }
    }

    private async Task<(RegionPlan Plan, Dictionary<string, ResolvedRelease> Resolved)> PlanAsync(IReadOnlySet<string> visited, CancellationToken ct)
    {
        var resolved = new Dictionary<string, ResolvedRelease>(StringComparer.Ordinal);
        foreach (var key in foundation.Regions.Keys.Where(gate.IsArmed))
        {
            try
            {
                if (await releases.ResolveAsync(key, ct) is { } release) resolved[key] = release;
            }
            catch (Exception error) when (error is ValidationException or ConflictException)
            {
                log.LogWarning(error, "The release for {Region} did not validate; the region is ineligible.", key);
            }
        }

        return (RegionalPlanner.Plan(foundation, settings.Current, gate.IsArmed, resolved.ContainsKey, admission, visited), resolved);
    }

    private string? TryReserve(RegionPlan plan)
    {
        var current = settings.Current;
        foreach (var candidate in plan.Candidates)
        {
            if (admission.TryAcquire(candidate.Region, current.Regions[candidate.Region].MaxConcurrentJobs, current.MaxConcurrentJobs)) return candidate.Region;
        }

        return null;
    }

    /// <summary>Waits for capacity in an eligible region. Nothing eligible is an explicit failure with the reason for each region.</summary>
    private async Task<string> ReserveAsync(GenerationJob job, StudioJobRecord? ledger, CancellationToken ct)
    {
        var visited = ledger is null ? new HashSet<string>(StringComparer.Ordinal) : ledger.Attempts.Select(attempt => attempt.Region).ToHashSet(StringComparer.Ordinal);
        var waiting = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (plan, _) = await PlanAsync(visited, ct);
            if (TryReserve(plan) is { } region)
            {
                if (waiting)
                {
                    job.Status = JobStatus.Running;
                    await records.SaveAsync(job, ct);
                }

                return region;
            }

            if (!plan.AnyEligible) throw new ConflictException($"No region can run this job. {plan.Explain()}");
            if (!waiting)
            {
                waiting = true;
                job.Status = JobStatus.Queued;
                await records.SaveAsync(job, ct);
            }

            await Task.Delay(Poll, time, ct);
        }
    }

    private async Task<StudioJobRecord> SetAttemptAsync(StudioJobRecord ledger, Func<JobAttempt, JobAttempt> change, bool required, CancellationToken ct)
    {
        var last = ledger.Attempts[^1];
        var next = change(last);
        next = next with { UpdatedAt = UtcNow > next.UpdatedAt ? UtcNow : next.UpdatedAt };
        var updated = ledger with { Attempts = [.. ledger.Attempts[..^1], next] };
        try
        {
            return await ledgerStore.SaveAsync(updated, ct);
        }
        catch (Exception error) when (!required && error is not OperationCanceledException)
        {
            log.LogWarning(error, "Attempt progress for {Job} was not persisted; it is re-read from Azure ML on recovery.", ledger.Name);
            return updated;
        }
    }

    private Task<StudioJobRecord> TerminalAsync(StudioJobRecord ledger, AttemptPhase phase, CancellationToken ct) =>
        SetAttemptAsync(ledger, attempt => attempt with { Phase = phase, TerminalVerifiedAt = UtcNow > attempt.UpdatedAt ? UtcNow : attempt.UpdatedAt }, true, ct);

    private async Task<JsonNode?> TryGetAsync(RegionContext context, string name, CancellationToken ct)
    {
        try
        {
            return await context.Client.GetJobAsync(name, ct);
        }
        catch (AzureMlException error) when (error.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ---- submit one attempt ----------------------------------------------------------------------------------------------

    private async Task<StudioJobRecord> SubmitAttemptAsync(
        GenerationJob job, WorkItem? item, StudioJobRecord? ledger, string key, Dictionary<string, double> phases, CancellationToken ct)
    {
        using var activity = Source.StartActivity("studio.attempt.submit");
        activity?.SetTag("studio.region", key);
        var context = regions[key];
        var resolved = await releases.ResolveAsync(key, ct) ?? throw new ConflictException($"Region {key} has no verified release.");
        var regional = settings.Current.Regions[key];
        // Snapshot the destination region's timeout for this attempt: the same value limits the Azure ML job and the runner.
        var timeoutSeconds = Math.Min(regional.MaxJobMinutes, context.Region.Policy.MaxJobMinutes) * 60;
        var data = job.Context ?? throw new ConflictException("The job has no recorded run settings.");
        var imageName = data.ImageName ?? throw new ConflictException("The job has no recorded input image name.");
        var mode = WanModes.Get(job.Mode);
        var stem = $"{AzureMlBackend.InputsFolder}{job.Name}/";
        var imageKey = stem + imageName;
        var workflowKey = stem + "workflow.json";

        var began = Now;
        if (ledger is null && item is not null)
        {
            var workflow = WorkflowRenderer.Render(await workflowSource.LoadAsync(mode.Template, ct),
                WorkflowValues.Build(generationOptions.Value, mode, data.Prompt, data.Negative ?? string.Empty, imageName, data.Seed, data.Width, data.Height,
                    WorkflowValues.Length(data.Duration, mode.Fps), "ComfyUI"));
            await context.Store.WriteAsync(imageKey, item.Image.Data, true, ct);
            await context.Store.WriteAsync(workflowKey, System.Text.Encoding.UTF8.GetBytes(workflow.ToJsonString()), true, ct);
        }
        else
        {
            var from = regions[ledger!.Attempts[^1].Region].Store;
            foreach (var input in new[] { imageKey, workflowKey })
            {
                var bytes = await from.ReadAsync(input, ct) ?? throw new ConflictException("The job's inputs are no longer available to move it.");
                await context.Store.WriteAsync(input, bytes, true, ct);
            }
        }

        phases["upload_inputs"] = Math.Round(Now - began, 3);
        var length = WorkflowValues.Length(data.Duration, mode.Fps);
        var spec = AzureMlJobSpecBuilder.Build(context.Foundation, resolved.Release, job.Name, $"comfyui-wan22-video-{UtcNow:yyyyMMddHHmmss}-s{job.Scene}t{job.Take}",
            context.Foundation.DatastorePath(imageKey), context.Foundation.DatastorePath(workflowKey),
            new AzureMlRunSettings(data.Prompt, data.Negative ?? string.Empty, data.Seed, mode.Steps, mode.Cfg, mode.Sampler, data.Width, data.Height, data.Duration, length, mode.Fps, imageName),
            timeoutSeconds);
        AzureMlGuard.Validate(spec, context.Foundation, resolved.Release, gate.IsArmed(key)); // Stop fences between attempts too
        ct.ThrowIfCancellationRequested();

        // Durable identity before the remote create: job record first, so a restart always knows where to look.
        job.Region = key;
        job.RemoteId = job.Name;
        using var handoff = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        await records.SaveRequiredAsync(job, handoff.Token);
        var now = UtcNow;
        var attempt = new JobAttempt((ledger?.Attempts.Length ?? 0) + 1, key, context.Region.WorkspaceId, job.Name, resolved.ArtifactId, timeoutSeconds,
            AttemptPhase.Intent, now, now, null, null, spec.OutputUri);
        ledger = await ledgerStore.SaveAsync(
            ledger is null ? new StudioJobRecord(1, 0, foundation.Scope, job.Name, [attempt]) : ledger with { Attempts = [.. ledger.Attempts, attempt] }, handoff.Token);

        began = Now;
        JsonNode created;
        try
        {
            created = await context.Client.CreateJobAsync(spec, CancellationToken.None); // never retried; not cancelled halfway
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Acceptance is uncertain: reconcile by reading the job, never by creating it again.
            JsonNode? found;
            try
            {
                found = await TryGetAsync(context, job.Name, CancellationToken.None);
            }
            catch (Exception readError) when (readError is not OperationCanceledException)
            {
                log.LogCritical(error, "Acceptance of Azure ML job {Job} in {Region} is unknown; do not resubmit; run Stop and verify release", job.Name, key);
                await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Unknown }, false, CancellationToken.None);
                throw new AzureMlHandoffException("Azure ML job acceptance could not be determined; run Stop before submitting again.", readError);
            }

            if (found is null)
            {
                await TerminalAsync(ledger, AttemptPhase.Failed, CancellationToken.None);
                throw;
            }

            created = found;
        }

        phases["submit"] = Math.Round(Now - began, 3);
        job.StudioUrl = StudioUrl(context.Foundation, created, spec.Name);
        try
        {
            ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Accepted }, true, handoff.Token);
        }
        catch (Exception error)
        {
            log.LogCritical(error, "Recovery-critical handoff failed for accepted Azure ML job {Job}; do not resubmit; run Stop and recover this identity manually", spec.Name);
            await CancelAsync(context, spec.Name);
            throw new AzureMlHandoffException("Accepted Azure ML job recovery information could not be persisted; run Stop before submitting again.", error);
        }

        try
        {
            AzureMlGuard.VerifyCreated(await context.Client.GetJobAsync(spec.Name, handoff.Token), spec, context.Foundation);
        }
        catch (Exception error)
        {
            log.LogError(error, "Server job safety could not be proven for {Job}; run Stop to verify compute release", spec.Name);
            await CancelAsync(context, spec.Name);
            throw;
        }

        return ledger;
    }

    private async Task CancelAsync(RegionContext context, string name)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            await context.Client.CancelJobAsync(name, limit.Token);
        }
        catch (Exception error)
        {
            log.LogWarning(error, "Could not cancel Azure ML job {Job}; run Stop to verify compute release", name);
        }
    }

    // ---- follow one attempt ----------------------------------------------------------------------------------------------

    private static bool Started(string state) => state is "Starting" or "Preparing" or "Running" or "Finalizing";

    private static bool Movable(string state) => state is "NotStarted" or "Queued" or "Provisioning";

    private static AttemptPhase PhaseOf(string state, AttemptPhase current) => state switch
    {
        "Completed" => AttemptPhase.Completed,
        "Failed" or "NotResponding" => AttemptPhase.Failed,
        "Canceled" or "Cancelled" => AttemptPhase.Canceled,
        "CancelRequested" => AttemptPhase.CancelRequested,
        _ when Started(state) => AttemptPhase.Running,
        _ => current is AttemptPhase.Unknown or AttemptPhase.CancelRequested ? current : AttemptPhase.Queued,
    };

    /// <summary>
    /// Polls the last attempt. Returns done=true when it completed; done=false with a reserved destination when its cancellation was proven and it never started.
    /// Throws when the attempt ended any other way. The attempt's admission slot is always released on exit.
    /// </summary>
    private async Task<(bool Done, StudioJobRecord Ledger, string? Reserved)> FollowAsync(GenerationJob job, StudioJobRecord ledger, Dictionary<string, double> phases, CancellationToken ct)
    {
        var attempt = ledger.Attempts[^1];
        var context = regions[attempt.Region];
        string? reserved = null;
        var handedOff = false;
        var cancelIssued = attempt.Phase is AttemptPhase.CancelRequested or AttemptPhase.Unknown;
        DateTimeOffset? proofStart = cancelIssued ? UtcNow : null;
        var failures = 0;
        try
        {
            if (attempt.Phase == AttemptPhase.Intent)
            {
                if (await TryGetAsync(context, attempt.RemoteName, ct) is null)
                {
                    await TerminalAsync(ledger, AttemptPhase.Failed, ct);
                    throw new ConflictException("The job was not accepted before the host stopped; it was not resubmitted.");
                }

                ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Accepted }, true, ct);
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt = ledger.Attempts[^1];
                string state;
                try
                {
                    state = ArmAzureMlClient.JobStatus(await context.Client.GetJobAsync(attempt.RemoteName, ct));
                    failures = 0;
                }
                catch (Exception error) when (!ct.IsCancellationRequested && error is AzureMlException { Transient: true } or HttpRequestException or OperationCanceledException)
                {
                    if (++failures >= MaxPollFailures)
                    {
                        log.LogWarning(error, "Polling Azure ML job {Job} in {Region} gave up; cancelling it", attempt.RemoteName, attempt.Region);
                        await CancelAsync(context, attempt.RemoteName);
                        ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Unknown }, false, CancellationToken.None);
                        throw;
                    }

                    await Task.Delay(Poll, time, ct);
                    continue;
                }

                var phase = PhaseOf(state, attempt.Phase);
                if (Started(state) && attempt.StartedAt is null)
                {
                    ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Running, StartedAt = UtcNow }, false, ct);
                }
                else if (phase != attempt.Phase && !AzureMlStatus.IsTerminal(state))
                {
                    ledger = await SetAttemptAsync(ledger, a => a with { Phase = phase }, phase == AttemptPhase.CancelRequested, ct);
                }

                var mapped = AzureMlStatus.ToJobStatus(state);
                if (!JobStatus.IsTerminal(mapped) && mapped != job.Status && reserved is null)
                {
                    job.Status = mapped;
                    await records.SaveAsync(job, ct);
                }

                if (AzureMlStatus.IsTerminal(state))
                {
                    switch (phase)
                    {
                        case AttemptPhase.Completed:
                            ledger = await TerminalAsync(ledger, AttemptPhase.Completed, ct);
                            return (true, ledger, null);
                        case AttemptPhase.Canceled when cancelIssued && ledger.Attempts[^1].StartedAt is null:
                            ledger = await TerminalAsync(ledger, AttemptPhase.Canceled, ct);
                            handedOff = true;
                            return (false, ledger, reserved);
                        default:
                            ledger = await TerminalAsync(ledger, phase == AttemptPhase.Canceled ? AttemptPhase.Canceled : AttemptPhase.Failed, ct);
                            throw new AzureMlException($"Azure ML job ended as {state}.");
                    }
                }

                if (!cancelIssued && Movable(state) && job.Context?.ImageName is not null && UtcNow - attempt.CreatedAt >= TimeSpan.FromMinutes(settings.Current.QueueMoveMinutes))
                {
                    var (plan, _) = await PlanAsync(ledger.Attempts.Select(a => a.Region).ToHashSet(StringComparer.Ordinal), ct);
                    if (TryReserve(plan) is { } destination)
                    {
                        reserved = destination;
                        // This read just showed the job unstarted. Record intent durably, cancel exactly once, then require fresh terminal proof.
                        ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.CancelRequested }, true, ct);
                        cancelIssued = true;
                        proofStart = UtcNow;
                        using var activity = Source.StartActivity("studio.attempt.move");
                        activity?.SetTag("studio.from", attempt.Region);
                        activity?.SetTag("studio.to", destination);
                        try
                        {
                            await context.Client.CancelJobAsync(attempt.RemoteName, CancellationToken.None);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            log.LogWarning(error, "Cancelling queued job {Job} in {Region} failed; movement waits for fresh terminal proof.", attempt.RemoteName, attempt.Region);
                        }

                        continue;
                    }
                }

                if (cancelIssued && proofStart is { } started && UtcNow - started >= timings.CancelProof)
                {
                    // No proof: the queued job may still run here. Stay fenced to it, surface it, and never cancel or move again.
                    log.LogError("Cancellation of {Job} in {Region} is not proven; the job stays tracked there and is not moved.", attempt.RemoteName, attempt.Region);
                    ledger = await SetAttemptAsync(ledger, a => a with { Phase = AttemptPhase.Unknown }, false, ct);
                    if (reserved is not null)
                    {
                        admission.Release(reserved);
                        reserved = null;
                    }

                    proofStart = null;
                }

                await Task.Delay(Poll, time, ct);
            }
        }
        finally
        {
            admission.Release(attempt.Region);
            if (!handedOff && reserved is not null) admission.Release(reserved);
        }
    }

    // ---- output ----------------------------------------------------------------------------------------------------------

    /// <summary>Finds the video the successful attempt wrote in its own regional store and adds meta.json beside it.</summary>
    private async Task FinishAsync(GenerationJob job, RegionContext context, TimingSummary? timing, CancellationToken ct)
    {
        var folder = $"{GenerationService.Library}{job.Name}/";
        string? video = null;
        for (var attempt = 0; attempt < 4 && video is null; attempt++)
        {
            video = AzureMlOutput.PickVideo((await context.Store.ListAsync(folder.TrimEnd('/'), ct)).Select(key => key[folder.Length..]));
            if (video is null && attempt < 3) await Task.Delay(Poll, time, ct);
        }

        if (video is null) throw new AzureMlException("Azure ML finished but produced no video output.");
        var data = job.Context!;
        var meta = new GalleryMeta(job.Name, job.DisplayName, data.Width, data.Height, data.Duration, data.Prompt, data.Seed, video, Now, timing, job.Mode);
        await context.Store.WriteAsync($"{folder}meta.json", Json.ToBytes(meta), true, ct);
        job.Region = context.Key;
        library.Pin(job.Name, context.Key);
    }
}
