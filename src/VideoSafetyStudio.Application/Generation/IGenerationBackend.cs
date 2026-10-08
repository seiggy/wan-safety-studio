using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Application.Generation;

/// <summary>Where generations actually run: a local ComfyUI server or the Azure ML workspace.</summary>
public interface IGenerationBackend
{
    /// <summary>comfyui | azureml</summary>
    string Provider { get; }

    /// <summary>How many jobs the worker may run at once (ComfyUI: one GPU; Azure ML: queued on the node by the service).</summary>
    int MaxConcurrency { get; }

    /// <summary>Name for a new job; it is also the library folder of its video.</summary>
    string NewJobName();

    /// <summary>Rejects a request this backend cannot run (fail fast, before anything is queued).</summary>
    void EnsureCanSubmit(SubmitRequest request);

    bool ModeAvailable(WanMode mode);

    /// <summary>The /api/status document.</summary>
    Task<object> StatusAsync(string storage, CancellationToken ct);

    /// <summary>Unfinished jobs for /api/jobs; <paramref name="local"/> are the ones this server knows about.</summary>
    Task<IReadOnlyList<object>> OpenJobsAsync(IReadOnlyList<GenerationJob> local, CancellationToken ct);

    /// <summary>Runs one job to completion and stores its video and meta.json. Throws on failure.</summary>
    Task GenerateAsync(WorkItem item, double started, CancellationToken ct);

    /// <summary>True when a persisted unfinished job can be picked up again after a restart.</summary>
    bool CanResume(GenerationJob job);

    Task ResumeAsync(GenerationJob job, CancellationToken ct);
}
