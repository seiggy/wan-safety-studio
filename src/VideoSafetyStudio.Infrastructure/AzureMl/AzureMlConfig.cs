using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>The workspace scope this server may spend in.</summary>
public sealed record AzureMlFoundation(
    string SubscriptionId, string TenantId, string ResourceGroupName, string WorkspaceName, string ComputeName,
    string ComputeIdentityClientId, string StorageAccountName, string ContainerName, string DatastoreName)
{
    public string WorkspaceArmId => $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroupName}/providers/Microsoft.MachineLearningServices/workspaces/{WorkspaceName}";

    public string ComputeId => $"{WorkspaceArmId}/computes/{ComputeName}";

    public string DatastorePath(string folder) => $"azureml://datastores/{DatastoreName}/paths/{folder}";
}

/// <summary>Pointers to the prepared environment, models and code in the workspace, and the version they were prepared as.</summary>
public sealed record AzureMlRelease(string EnvironmentId, string ModelsRef, string ModelsKind, string CodeUri, string Version);

/// <summary>Turns the bound <see cref="AzureMlOptions"/> into validated scope and release; every rule rejects with the setting's name.</summary>
public static partial class AzureMlConfig
{
    public const string Profile = "wan";
    public const string UpstreamSha = "dc0d29031b73a5ba7376d910adb7a58c206f32b1";

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^azureml:[A-Za-z0-9][A-Za-z0-9_.-]*:[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex AssetRule();

    public static AzureMlFoundation Foundation(AzureMlOptions options)
    {
        var required = new (string Key, string? Value)[]
        {
            ("SubscriptionId", options.SubscriptionId), ("TenantId", options.TenantId), ("ResourceGroup", options.ResourceGroup), ("Workspace", options.Workspace),
            ("Compute", options.Compute), ("ComputeIdentityClientId", options.ComputeIdentityClientId), ("StorageAccount", options.StorageAccount),
            ("Container", options.Container), ("Datastore", options.Datastore),
        };
        var missing = required.Where(item => string.IsNullOrWhiteSpace(item.Value)).Select(item => $"{AzureMlOptions.Section}:{item.Key}").ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Azure ML is not configured: {string.Join(", ", missing)} must be set.");
        }

        CheckLimits(options);
        return new AzureMlFoundation(
            Uuid(options.SubscriptionId, "SubscriptionId"), Uuid(options.TenantId, "TenantId"),
            Name(options.ResourceGroup, "ResourceGroup"), Name(options.Workspace, "Workspace"), Name(options.Compute, "Compute"),
            Uuid(options.ComputeIdentityClientId, "ComputeIdentityClientId"),
            Name(options.StorageAccount, "StorageAccount"), Name(options.Container, "Container"), Name(options.Datastore, "Datastore"));
    }

    private static void CheckLimits(AzureMlOptions options)
    {
        if (options.MaxJobMinutes < 1 || options.MaxJobMinutes * 60 > AzureMlJobSpecBuilder.MaxSeconds)
        {
            throw new InvalidOperationException($"{AzureMlOptions.Section}:MaxJobMinutes must be between 1 and {AzureMlJobSpecBuilder.MaxSeconds / 60}.");
        }

        if (options.MaxUploadMb < 1)
        {
            throw new InvalidOperationException($"{AzureMlOptions.Section}:MaxUploadMb must be at least 1.");
        }
    }

    /// <summary>The prepared release; null when no EnvironmentId is set (not prepared). Models and code default under the datastore.</summary>
    public static AzureMlRelease? Release(AzureMlOptions options, AzureMlFoundation foundation)
    {
        var pointers = options.Release;
        if (string.IsNullOrWhiteSpace(pointers.EnvironmentId))
        {
            return null;
        }

        var environmentId = pointers.EnvironmentId.Trim();
        var version = VersionOf(environmentId);
        var models = string.IsNullOrWhiteSpace(pointers.ModelsRef) ? foundation.DatastorePath("models/wan/") : pointers.ModelsRef.Trim();
        var code = string.IsNullOrWhiteSpace(pointers.CodeUri) ? foundation.DatastorePath($"code/{version}-wan/") : pointers.CodeUri.Trim();
        return Check(foundation, new AzureMlRelease(environmentId, models, models.StartsWith("azureml://", StringComparison.Ordinal) ? "data" : "model", code, version));
    }

    private static string Uuid(string? value, string key) =>
        value is not null && Guid.TryParseExact(value, "D", out var guid) && guid.ToString("D") == value.ToLowerInvariant()
            ? value.ToLowerInvariant()
            : throw new InvalidOperationException($"{AzureMlOptions.Section}:{key} must be a canonical UUID.");

    private static string Name(string? value, string key) =>
        value is not null && NameRule().IsMatch(value) ? value : throw new InvalidOperationException($"{AzureMlOptions.Section}:{key} is not a valid resource name.");

    private static string VersionOf(string environmentId) => environmentId[(environmentId.LastIndexOfAny([':', '/']) + 1)..];

    private static AzureMlRelease Check(AzureMlFoundation foundation, AzureMlRelease release)
    {
        var armEnvironment = $"{foundation.WorkspaceArmId}/environments/";
        var environmentOk = AssetRule().IsMatch(release.EnvironmentId) ||
            (release.EnvironmentId.StartsWith(armEnvironment, StringComparison.OrdinalIgnoreCase) && release.EnvironmentId[armEnvironment.Length..].Split('/') is [{ Length: > 0 }, "versions", { Length: > 0 }]);
        if (!environmentOk || release.Version.Length == 0)
        {
            throw new InvalidOperationException("AzureMl:Release:EnvironmentId must be azureml:<name>:<version> or this workspace's environment version id.");
        }

        var modelsOk = release.ModelsRef.StartsWith("azureml://", StringComparison.Ordinal)
            ? AzureMlGuard.IsDatastorePath(release.ModelsRef, foundation.DatastoreName, string.Empty)
            : AssetRule().IsMatch(release.ModelsRef);
        if (!modelsOk || !AzureMlGuard.IsDatastorePath(release.CodeUri, foundation.DatastoreName, string.Empty))
        {
            throw new InvalidOperationException("AzureMl:Release:ModelsRef/CodeUri must be this studio datastore paths or a registered azureml:<name>:<version> asset.");
        }

        return release;
    }
}

/// <summary>Whether generation is armed: the live <see cref="AzureMlOptions.Armed"/> value, so Stop halts a batch between submissions.</summary>
public sealed class AzureMlGate(IOptionsMonitor<AzureMlOptions> options)
{
    public bool IsArmed(AzureMlRelease? release) => release is not null && options.CurrentValue.Armed;
}
