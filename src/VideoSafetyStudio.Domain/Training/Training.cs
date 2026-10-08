using System.Text.Json.Serialization;

namespace VideoSafetyStudio.Domain.Training;

public sealed record TrainerManifest(
    string Name,
    string Version,
    string Image,
    string LicenseCode,
    string LicenseWeights,
    IReadOnlyList<string> Presets,
    string DefaultPreset);

public sealed record RunMetrics(
    double Map50,
    [property: JsonPropertyName("map50_95")] double Map50To95,
    [property: JsonPropertyName("per_class")] IReadOnlyDictionary<string, double>? PerClass);

public static class RunStatus
{
    public const string Queued = "Queued";
    public const string Preparing = "Preparing";
    public const string Running = "Running";
    public const string Uploading = "Uploading";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed class TrainingRun
{
    public required string Id { get; init; }
    public required string Project { get; init; }
    public required string Dataset { get; init; }
    public required string Trainer { get; init; }
    public required string TrainerVersion { get; init; }
    public required string Preset { get; init; }
    public int? Epochs { get; init; }
    public required string Compute { get; init; }
    public string Status { get; set; } = RunStatus.Queued;
    public string? Error { get; set; }
    public RunMetrics? Metrics { get; set; }
    public required string CreatedAt { get; init; }
}
