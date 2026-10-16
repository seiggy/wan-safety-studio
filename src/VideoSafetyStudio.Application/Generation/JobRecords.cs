using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Persists a job record beside the library and pushes the change to clients. Shared by the service and the backends.</summary>
public sealed class JobRecords(IObjectStore store, IEventPublisher events, ILogger<JobRecords> logger)
{
    public const string Prefix = "video-library-jobs/"; // outside the library prefix so the gallery ignores job records

    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task SaveAsync(GenerationJob job, CancellationToken ct = default)
    {
        try
        {
            await SaveRequiredAsync(job, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(error, "Could not persist job {Job}", job.Name);
            events.Publish("job", job);
        }
    }

    /// <summary>Persists recovery-critical state; unlike ordinary progress saves, failure is returned to the owner.</summary>
    public async Task SaveRequiredAsync(GenerationJob job, CancellationToken ct = default)
    {
        // One writer per job, serializing the state inside the lock, so an older save can never land after a newer one.
        // Stripes are bounded and never removed while a writer still holds or awaits one.
        var gate = _locks[(uint)StringComparer.Ordinal.GetHashCode(job.Name) % (uint)_locks.Length];
        await gate.WaitAsync(ct);
        try
        {
            await store.WriteAsync($"{Prefix}{job.Name}.json", Json.ToBytes(job), true, ct);
        }
        finally
        {
            gate.Release();
        }

        events.Publish("job", job);
    }

    public void Publish(GenerationJob job) => events.Publish("job", job);
}
