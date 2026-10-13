using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Persists a job record beside the library and pushes the change to clients. Shared by the service and the backends.</summary>
public sealed class JobRecords(IObjectStore store, IEventPublisher events, ILogger<JobRecords> logger)
{
    public const string Prefix = "video-library-jobs/"; // outside the library prefix so the gallery ignores job records

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public async Task SaveAsync(GenerationJob job, CancellationToken ct = default)
    {
        // One writer per job, serializing the state inside the lock, so an older save can never land after a newer one.
        var gate = _locks.GetOrAdd(job.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await store.WriteAsync($"{Prefix}{job.Name}.json", Json.ToBytes(job), true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Could not persist job {Job}", job.Name); // persistence must never fail a job
        }
        finally
        {
            gate.Release();
            if (JobStatus.IsTerminal(job.Status))
            {
                _locks.TryRemove(job.Name, out _);
            }
        }

        events.Publish("job", job);
    }

    public void Publish(GenerationJob job) => events.Publish("job", job);
}
