using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain.Training;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>One Azure ML command job for one training run. Built only from the promoted binding, the region and the run.</summary>
public sealed record AzureMlTrainingSpec(
    string Name, string ComputeId, string EnvironmentId, string Command, int TimeoutSeconds, string IdentityClientId, string DataUri, string OutputUri)
{
    public const string Experiment = "wan-trainer";
    public const int MaxSeconds = 7200;

    public JsonObject ToArmBody() => new()
    {
        ["properties"] = new JsonObject
        {
            ["jobType"] = "Command",
            ["displayName"] = Name,
            ["experimentName"] = Experiment,
            ["computeId"] = ComputeId,
            ["command"] = Command,
            ["environmentId"] = EnvironmentId,
            ["inputs"] = new JsonObject { ["data"] = new JsonObject { ["jobInputType"] = "uri_folder", ["uri"] = DataUri, ["mode"] = "ReadOnlyMount" } },
            ["outputs"] = new JsonObject { ["out"] = new JsonObject { ["jobOutputType"] = "uri_folder", ["uri"] = OutputUri, ["mode"] = "ReadWriteMount" } },
            ["limits"] = new JsonObject { ["jobLimitsType"] = "Command", ["timeout"] = $"PT{TimeoutSeconds.ToString(CultureInfo.InvariantCulture)}S" },
            ["resources"] = new JsonObject { ["instanceCount"] = 1 },
            ["identity"] = new JsonObject { ["identityType"] = "Managed", ["clientId"] = IdentityClientId },
            ["environmentVariables"] = new JsonObject(),
        },
    };
}

public static partial class AzureMlTrainingSpecBuilder
{
    [GeneratedRegex(@"^r[a-f0-9]{10}\z")]
    private static partial Regex RunId();

    public static string JobName(string runId) => RunId().IsMatch(runId) ? "train-" + runId : throw new AzureMlSafetyException("Invalid training run identity.");

    public static string DataFolder(string runId) => $"studio/training/{runId}/data/";

    public static string OutFolder(string runId) => $"studio/training/{runId}/out/";

    /// <summary>The fixed command form. Module, preset and epochs were validated by the binding contract and the run; nothing else is interpolated.</summary>
    public static string Command(TrainerBinding binding, string preset, int? epochs) =>
        $"python -m {binding.Module} all --preset {preset}" +
        (epochs is { } count ? $" --epochs {count.ToString(CultureInfo.InvariantCulture)}" : "") +
        " --data \"${{inputs.data}}\" --out \"${{outputs.out}}\"";

    public static AzureMlTrainingSpec Build(AzureMlFoundation foundation, TrainerBinding binding, TrainingRun run, int timeoutSeconds)
    {
        var spec = new AzureMlTrainingSpec(
            JobName(run.Id), foundation.ComputeId, binding.EnvironmentId, Command(binding, run.Preset, run.Epochs), timeoutSeconds,
            foundation.ComputeIdentityClientId, foundation.DatastorePath(DataFolder(run.Id)), foundation.DatastorePath(OutFolder(run.Id)));
        Validate(spec, foundation, binding, run);
        return spec;
    }

    /// <summary>The spending and scope rules for a training job, checked before the single create call.</summary>
    public static void Validate(AzureMlTrainingSpec spec, AzureMlFoundation foundation, TrainerBinding binding, TrainingRun run)
    {
        if (!binding.Presets.Contains(run.Preset, StringComparer.Ordinal) || (run.Epochs is { } epochs && epochs is < 1 or > 1000))
        {
            throw new AzureMlSafetyException("Preset or epochs are outside the promoted trainer's contract.");
        }

        if (spec.Name != JobName(run.Id) || !string.Equals(spec.ComputeId, foundation.ComputeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureMlSafetyException("Unapproved training job name or compute.");
        }

        if (spec.TimeoutSeconds is < 1 or > AzureMlTrainingSpec.MaxSeconds)
        {
            throw new AzureMlSafetyException($"Training timeout must be 1..{AzureMlTrainingSpec.MaxSeconds} seconds.");
        }

        if (spec.EnvironmentId != binding.EnvironmentId || spec.Command != Command(binding, run.Preset, run.Epochs) ||
            spec.IdentityClientId != foundation.ComputeIdentityClientId)
        {
            throw new AzureMlSafetyException("Training job differs from the promoted trainer.");
        }

        if (spec.DataUri != foundation.DatastorePath(DataFolder(run.Id)) || spec.OutputUri != foundation.DatastorePath(OutFolder(run.Id)))
        {
            throw new AzureMlSafetyException("Training data and output must use this run's private datastore folders.");
        }
    }

    /// <summary>Checks the job as Azure ML stored it; anything that cannot be proven within the limits throws.</summary>
    public static void VerifyCreated(JsonNode? job, AzureMlTrainingSpec spec, AzureMlFoundation foundation)
    {
        var properties = job?["properties"] ?? job;
        if (job?["name"]?.GetValue<string>() != spec.Name)
        {
            throw new AzureMlSafetyException("Server returned a different training job.");
        }

        if (AzureMlGuard.NormalizeTimeout(properties?["limits"]?["timeout"]) > Math.Min(AzureMlTrainingSpec.MaxSeconds, spec.TimeoutSeconds))
        {
            throw new AzureMlSafetyException("Server timeout exceeds the approved maximum.");
        }

        if (properties?["resources"]?["instanceCount"] is not JsonValue count || !count.TryGetValue<int>(out var instances) || instances != 1)
        {
            throw new AzureMlSafetyException("Server instance count is missing or is not exactly one.");
        }

        var compute = properties?["computeId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (compute is null || !(string.Equals(compute, foundation.ComputeName, StringComparison.OrdinalIgnoreCase) || string.Equals(compute, foundation.ComputeId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AzureMlSafetyException("Server training job is not assigned to the selected studio compute.");
        }
    }
}
