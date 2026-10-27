using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

public sealed record RemoteTrainingTimings(TimeSpan Poll, TimeSpan CancelProof)
{
    public static RemoteTrainingTimings Default { get; } = new(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(2));
}

/// <summary>
/// Runs a training run as one Azure ML command job on a region's shared GPU compute, with the promoted trainer image and the host's managed identity.
/// It takes a generation admission slot first, so training can never exceed the deployed or configured limits; the dataset is staged create-only under
/// the run's own folder; the job identity is saved before the single create call; and a restart follows the saved job instead of creating another.
/// </summary>
public sealed class AzureMlTrainingCompute(
    StudioFoundation foundation,
    IReadOnlyDictionary<string, RegionContext> regions,
    TrainerBindings bindings,
    StudioArmedGate gate,
    StudioSettingsSource settings,
    RegionalAdmission admission,
    TimeProvider time,
    RemoteTrainingTimings timings,
    ILogger<AzureMlTrainingCompute>? logger = null) : IDurableComputeTarget
{
    private const int MaxPollFailures = 20;
    private readonly ILogger<AzureMlTrainingCompute> log = logger ?? NullLogger<AzureMlTrainingCompute>.Instance;

    public string Name => "azureml";

    Task<int> IComputeTarget.RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct) =>
        throw new NotSupportedException("Remote training runs only through the durable path.");

    public async Task<int> RunAsync(TrainingRun run, TrainerManifest manifest, string dataDirectory, string outDirectory, Func<RemoteRun, Task> remember, CancellationToken ct)
    {
        var artifact = manifest.ArtifactId ?? throw new ConflictException("This trainer has no promoted build, so it cannot run remotely.");
        var (context, binding) = Reserve(manifest.Name, artifact);
        try
        {
            var seconds = (int)Math.Min(settings.Current.Regions[context.Key].MaxJobMinutes * 60L, AzureMlTrainingSpec.MaxSeconds);
            var spec = AzureMlTrainingSpecBuilder.Build(context.Foundation, binding, run, seconds);
            await StageDataAsync(context, run.Id, dataDirectory, ct);
            var remote = new RemoteRun(context.Key, spec.Name, artifact);
            await remember(remote);
            var created = await CreateOnceAsync(context, spec, ct);
            AzureMlTrainingSpecBuilder.VerifyCreated(created, spec, context.Foundation);
            return await FollowAsync(context, run, remote, outDirectory, ct);
        }
        finally
        {
            admission.Release(context.Key);
        }
    }

    public async Task<int> ResumeAsync(TrainingRun run, string outDirectory, CancellationToken ct)
    {
        var remote = run.Remote ?? throw new ConflictException("The run has no remote job to resume.");
        var context = Context(remote.Region);
        // Existing remote work is counted unconditionally: lowering a limit never hides or stops it.
        admission.Force(context.Key);
        try
        {
            return await FollowAsync(context, run, remote, outDirectory, ct);
        }
        finally
        {
            admission.Release(context.Key);
        }
    }

    public async Task CancelAsync(RemoteRun remote, CancellationToken ct)
    {
        var context = Context(remote.Region);
        var status = ArmAzureMlClient.JobStatus(await context.Client.GetJobAsync(remote.Job, ct));
        if (AzureMlStatus.IsTerminal(status)) return;
        await context.Client.CancelJobAsync(remote.Job, ct);
        var deadline = time.GetUtcNow() + timings.CancelProof;
        while (true)
        {
            status = ArmAzureMlClient.JobStatus(await context.Client.GetJobAsync(remote.Job, ct));
            if (AzureMlStatus.IsTerminal(status)) return;
            if (time.GetUtcNow() >= deadline) throw new ConflictException($"Cancellation of {remote.Job} is not proven; it is still {status}.");
            await Task.Delay(timings.Poll, time, ct);
        }
    }

    private RegionContext Context(string region) =>
        regions.TryGetValue(region, out var context) ? context : throw new ConflictException($"Training region {region} is not part of this deployment.");

    private (RegionContext Context, TrainerBinding Binding) Reserve(string trainer, string artifact)
    {
        var current = settings.Current;
        var plan = RegionalPlanner.Plan(foundation, current, gate.IsArmed, region => bindings.Binding(region, trainer, artifact) is not null, admission, new HashSet<string>());
        foreach (var candidate in plan.Candidates)
        {
            var limits = current.Regions[candidate.Region];
            if (admission.TryAcquire(candidate.Region, limits.MaxConcurrentJobs, current.MaxConcurrentJobs))
            {
                return (Context(candidate.Region), bindings.Binding(candidate.Region, trainer, artifact)!);
            }
        }

        throw new ConflictException(plan.AnyEligible
            ? "Every eligible region is at its job limit; training shares generation's capacity. Wait for a job to finish."
            : $"No region can run this trainer. A region must be armed and hold the promoted trainer build. {plan.Explain()}");
    }

    private static async Task StageDataAsync(RegionContext context, string runId, string dataDirectory, CancellationToken ct)
    {
        var prefix = AzureMlTrainingSpecBuilder.DataFolder(runId);
        var files = new List<object>();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(dataDirectory, file).Replace('\\', '/');
            await using (var stream = File.OpenRead(file))
            {
                files.Add(new { path = relative, bytes = stream.Length, sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) });
            }

            // Create-only: a run's inputs are written once and never replaced.
            await context.Store.UploadAsync(prefix + relative, file, overwrite: false, ct);
            expected.Add(prefix + relative);
        }

        if (!(await context.Store.ListAsync(prefix, ct)).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            throw new ConflictException("The staged training data does not match the dataset; nothing was submitted.");
        }

        await context.Store.WriteAsync($"studio/training/{runId}/data.manifest.json", Encoding.UTF8.GetBytes(StudioContracts.Write(new { schemaVersion = 1, files })), overwrite: false, ct);
    }

    private async Task<JsonNode> CreateOnceAsync(RegionContext context, AzureMlTrainingSpec spec, CancellationToken ct)
    {
        try
        {
            return await context.Client.CreateJobAsync(spec.Name, spec.ToArmBody(), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Never created twice: an unclear answer is settled by reading the job, not by creating it again.
            try
            {
                return await context.Client.GetJobAsync(spec.Name, ct);
            }
            catch (AzureMlException missing) when (missing.Status == HttpStatusCode.NotFound)
            {
                throw new ConflictException("Azure ML did not create the training job; nothing is running.");
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await TryCancelAsync(context, spec.Name);
                throw new ConflictException($"Could not confirm whether training job {spec.Name} was created; it was asked to stop. Check it in Azure ML before starting another run.");
            }
        }
    }

    private async Task TryCancelAsync(RegionContext context, string job)
    {
        using var limit = new CancellationTokenSource(timings.CancelProof, time);
        try
        {
            await context.Client.CancelJobAsync(job, limit.Token);
        }
        catch (Exception error) when (error is not OperationCanceledException || limit.IsCancellationRequested)
        {
            log.LogWarning(error, "Could not cancel training job {Job}.", job);
        }
    }

    private async Task<int> FollowAsync(RegionContext context, TrainingRun run, RemoteRun remote, string outDirectory, CancellationToken ct)
    {
        JsonNode job;
        var failures = 0;
        while (true)
        {
            try
            {
                job = await context.Client.GetJobAsync(remote.Job, ct);
                failures = 0;
                if (AzureMlStatus.IsTerminal(ArmAzureMlClient.JobStatus(job))) break;
            }
            catch (AzureMlException error) when (error.Status == HttpStatusCode.NotFound)
            {
                throw new ConflictException($"Training job {remote.Job} was not found in {remote.Region}.");
            }
            catch (AzureMlException error) when (error.Transient && ++failures < MaxPollFailures)
            {
                log.LogWarning(error, "Training job {Job} status read failed ({Count}).", remote.Job, failures);
            }
            catch (AzureMlException) when (failures >= MaxPollFailures - 1 && !ct.IsCancellationRequested)
            {
                await TryCancelAsync(context, remote.Job);
                throw new ConflictException($"Azure ML could not be reached to follow {remote.Job}; it was asked to stop. Check it in Azure ML.");
            }

            await Task.Delay(timings.Poll, time, ct);
        }

        var status = ArmAzureMlClient.JobStatus(job);
        await CollectAsync(context, run, remote, outDirectory, status, job, ct);
        return status == "Completed" ? 0 : 1;
    }

    /// <summary>Copies the job's output folder into <paramref name="outDirectory"/>, including after a failure so diagnostics can be salvaged.</summary>
    private static async Task CollectAsync(RegionContext context, TrainingRun run, RemoteRun remote, string outDirectory, string status, JsonNode job, CancellationToken ct)
    {
        Directory.CreateDirectory(outDirectory);
        var prefix = AzureMlTrainingSpecBuilder.OutFolder(run.Id);
        var root = Path.GetFullPath(outDirectory) + Path.DirectorySeparatorChar;
        foreach (var key in await context.Store.ListAsync(prefix, ct))
        {
            var relative = key[prefix.Length..];
            var target = Path.GetFullPath(Path.Combine(outDirectory, relative));
            if (relative.Length == 0 || !target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ConflictException("The job output contains an unsafe path; it was not copied.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await context.Store.DownloadAsync(key, target, ct);
        }

        var log = Path.Combine(outDirectory, "train.log");
        if (!File.Exists(log))
        {
            var reason = (job["properties"]?["error"]?["error"]?["message"] ?? job["properties"]?["error"]?["message"])?.ToString();
            await File.WriteAllTextAsync(log, $"Azure ML job {remote.Job} in {remote.Region} ended {status}.{(reason is null ? "" : " " + reason)}\n", ct);
        }
    }
}
