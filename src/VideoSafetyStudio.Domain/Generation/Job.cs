using System.Text.Json.Serialization;

namespace VideoSafetyStudio.Domain.Generation;

public static class JobStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    public static bool IsTerminal(string status) => status is Completed or Failed;
}

/// <summary>Where the running job is, for live progress (stage name and sampler step).</summary>
public sealed record JobProgress(string Stage, int Step, int MaxSteps);

public sealed record JobContext(string Prompt, int Width, int Height, double Duration, long Seed);

public sealed record GenerationParams(int Width, int Height, int? Frames, double Seconds, string Provider, string? Mode = null, int? Steps = null);

public sealed record StageSummary(double Seconds, List<string> Nodes, int Steps, bool Cached, double? SecondsPerStep);

public sealed record TimingSummary(
    string? Outcome,
    double? ExecutionSeconds,
    IReadOnlyDictionary<string, StageSummary> Stages,
    IReadOnlyDictionary<string, double>? Phases = null,
    double? TotalSeconds = null,
    double? AppQueueSeconds = null,
    GenerationParams? Params = null,
    long? VideoBytes = null);

/// <summary>One video generation. Persisted as JSON so a restart can resume jobs ComfyUI already accepted.</summary>
public sealed class GenerationJob
{
    public required string Name { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    public string Status { get; set; } = JobStatus.Queued;
    public string? Mode { get; init; }

    [JsonPropertyName("studio_url")]
    public string? StudioUrl { get; set; }

    public double SubmittedAt { get; init; }
    public int Scene { get; init; }
    public int Take { get; init; }
    public double? AppQueueSeconds { get; set; }
    public string? PromptId { get; set; }

    /// <summary>The provider's own id once it accepted the job (the Azure ML job name); PromptId stays for ComfyUI records.</summary>
    public string? RemoteId { get; set; }
    public JobContext? Context { get; set; }
    public string? Error { get; set; }
    public JobProgress? Progress { get; set; }
    public TimingSummary? Timing { get; set; }
}

public sealed class Batch
{
    public required string Id { get; init; }
    public int Total { get; init; }
    public List<GenerationJob> Jobs { get; init; } = [];
    public bool Done { get; init; } = true;
    public string? Error { get; init; }
}
