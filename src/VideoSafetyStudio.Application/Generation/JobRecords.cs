using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Persists a job record beside the library and pushes the change to clients. Shared by the service and the backends.</summary>
public sealed class JobRecords(IObjectStore store, IEventPublisher events, ILogger<JobRecords> logger)
{
    public const string Prefix = "video-library-jobs/"; // outside the library prefix so the gallery ignores job records

    public async Task SaveAsync(GenerationJob job, CancellationToken ct = default)
    {
        try
        {
            await store.WriteAsync($"{Prefix}{job.Name}.json", Json.ToBytes(job), true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Could not persist job {Job}", job.Name); // persistence must never fail a job
        }

        events.Publish("job", job);
    }

    public void Publish(GenerationJob job) => events.Publish("job", job);
}
