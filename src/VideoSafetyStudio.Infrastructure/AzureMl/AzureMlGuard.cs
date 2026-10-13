using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>A spending rule was broken, or the created job could not be proven within the rules.</summary>
public sealed class AzureMlSafetyException(string message) : Exception(message), VideoSafetyStudio.Domain.Generation.ISafeMessage;

/// <summary>Port of the Python cost guard: checked before a job is created and again on the job Azure ML stored.</summary>
public static class AzureMlGuard
{
    private static readonly string[] ForbiddenInPath = ["?", "#", "..", "\\", "%"];

    /// <summary>True for azureml://datastores/{datastore}/paths/{folder}<c>x</c> with something after the prefix and no query, fragment, traversal, backslash or escape.</summary>
    public static bool IsDatastorePath(string? path, string datastore, string folder)
    {
        var prefix = $"azureml://datastores/{datastore}/paths/{folder}";
        return path is not null && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length..].Trim('/').Length > 0 &&
               !ForbiddenInPath.Any(path.Contains);
    }

    public static void Validate(AzureMlJobSpec spec, AzureMlFoundation foundation, AzureMlRelease release, bool armed)
    {
        if (!armed)
        {
            throw new AzureMlSafetyException("Generation is not armed: Start the studio compute first. Stop disables all submissions.");
        }

        if (spec.Experiment != AzureMlJobSpecBuilder.Experiment || spec.ComputeName != foundation.ComputeName ||
            !string.Equals(spec.ComputeId, foundation.ComputeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureMlSafetyException("Unapproved compute.");
        }

        if (spec.TimeoutSeconds is < 1 or > AzureMlJobSpecBuilder.MaxSeconds)
        {
            throw new AzureMlSafetyException("AzureML server timeout must be 1..7200 seconds.");
        }

        if (spec.InstanceCount != 1)
        {
            throw new AzureMlSafetyException("Only one instance per job is permitted.");
        }

        if (spec.BatchSize != 1)
        {
            throw new AzureMlSafetyException("Only one video per job is permitted.");
        }

        if (spec.Command != AzureMlJobSpecBuilder.Command ||
            spec.EnvironmentId != AzureMlJobSpecBuilder.EnvironmentArmId(foundation, release) ||
            spec.ModelsRef != release.ModelsRef || spec.RepoUri != release.CodeUri || spec.ModelsKind != release.ModelsKind)
        {
            throw new AzureMlSafetyException("Job assets differ from the prepared release.");
        }

        if (spec.IdentityClientId != foundation.ComputeIdentityClientId)
        {
            throw new AzureMlSafetyException("Prepared compute identity changed.");
        }

        if (!IsDatastorePath(spec.OutputUri, foundation.DatastoreName, "video-library/"))
        {
            throw new AzureMlSafetyException("Job output must use the selected private datastore video-library namespace.");
        }

        if (!IsDatastorePath(spec.ImageUri, foundation.DatastoreName, "studio/web-inputs/") ||
            !IsDatastorePath(spec.WorkflowUri, foundation.DatastoreName, "studio/web-inputs/"))
        {
            throw new AzureMlSafetyException("Inputs must use private datastore file inputs, not signed URLs.");
        }
    }

    /// <summary>Checks the job as Azure ML stored it; anything that cannot be proven within the limits throws.</summary>
    public static void VerifyCreated(JsonNode? job, AzureMlJobSpec spec, AzureMlFoundation foundation)
    {
        var properties = job?["properties"] ?? job;
        if (job?["name"]?.GetValue<string>() != spec.Name)
        {
            throw new AzureMlSafetyException("Server returned a different job.");
        }

        var timeout = NormalizeTimeout(properties?["limits"]?["timeout"]);
        if (timeout > Math.Min(AzureMlJobSpecBuilder.MaxSeconds, spec.TimeoutSeconds))
        {
            throw new AzureMlSafetyException("Server timeout exceeds the approved/requested maximum.");
        }

        var count = properties?["resources"]?["instanceCount"];
        if (count is not JsonValue value || !value.TryGetValue<int>(out var instances) || instances != 1)
        {
            throw new AzureMlSafetyException("Server instance count is missing or is not exactly one.");
        }

        var compute = properties?["computeId"] is JsonValue computeValue && computeValue.TryGetValue<string>(out var text) ? text : null;
        if (compute is null || !(string.Equals(compute, foundation.ComputeName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(compute, foundation.ComputeId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AzureMlSafetyException("Server job is not assigned to the selected studio compute.");
        }
    }

    /// <summary>Seconds from a number, a numeric string or an ISO 8601 duration; never assumes a missing limit.</summary>
    public static double NormalizeTimeout(JsonNode? node)
    {
        double seconds;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            seconds = value.GetValue<double>();
        }
        else if (node is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            var raw = text.GetValue<string>().Trim();
            try
            {
                seconds = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : XmlConvert.ToTimeSpan(raw).TotalSeconds;
            }
            catch (FormatException)
            {
                throw new AzureMlSafetyException("Unrecognized server timeout; refusing to assume a limit.");
            }
        }
        else
        {
            throw new AzureMlSafetyException("Server timeout is missing or unrecognized.");
        }

        return double.IsFinite(seconds) && seconds > 0 ? seconds : throw new AzureMlSafetyException("Server timeout must be finite and positive.");
    }
}
