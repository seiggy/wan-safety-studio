using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>The Terraform foundation receipt: the only scope this server may spend in.</summary>
public sealed record AzureMlFoundation(
    string SubscriptionId, string TenantId, string ResourceGroupName, string WorkspaceName, string ComputeName, string ComputeId,
    string ComputeIdentityClientId, string StorageAccountName, string ContainerName, string DatastoreName, string? PortalHostname)
{
    public string WorkspaceArmId => $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroupName}/providers/Microsoft.MachineLearningServices/workspaces/{WorkspaceName}";

    public string DatastorePath(string folder) => $"azureml://datastores/{DatastoreName}/paths/{folder}";
}

/// <summary>Pointers to the prepared environment, models and code in the workspace, and the version they were prepared as.</summary>
public sealed record AzureMlRelease(string EnvironmentId, string ModelsRef, string ModelsKind, string CodeUri, string Version);

public static partial class AzureMlConfig
{
    public const string Profile = "wan";
    public const string UpstreamSha = "dc0d29031b73a5ba7376d910adb7a58c206f32b1";

    private static readonly string[] RuntimeFoundation =
        ["subscriptionId", "tenantId", "resourceGroupName", "workspaceName", "computeName", "computeId", "computeIdentityClientId", "storageAccountName", "containerName", "datastoreName"];

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^azureml:[A-Za-z0-9][A-Za-z0-9_.-]*:[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex AssetRule();

    private static JsonElement ReadSelected(IConfiguration configuration, string variable)
    {
        var inline = configuration[variable + "_JSON"];
        var path = configuration[variable];
        if (!string.IsNullOrWhiteSpace(inline) && !string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Set {variable} or {variable}_JSON, not both.");
        }

        string text;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            text = inline;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            if (!Path.IsPathRooted(path))
            {
                throw new InvalidOperationException($"{variable} must select an absolute JSON path.");
            }

            text = File.ReadAllText(path);
        }
        else
        {
            throw new InvalidOperationException($"{variable} (a JSON file path) or {variable}_JSON is required.");
        }

        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true });
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Clone()
            : throw new InvalidOperationException($"{variable} must contain a JSON object.");
    }

    private static string? Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static string Uuid(string? value, string key) =>
        value is not null && Guid.TryParseExact(value, "D", out var guid) && guid.ToString("D") == value.ToLowerInvariant()
            ? value.ToLowerInvariant()
            : throw new InvalidOperationException($"Foundation {key} must be a canonical UUID.");

    private static string Name(string? value) =>
        value is not null && NameRule().IsMatch(value) ? value : throw new InvalidOperationException("Foundation contains an invalid resource name.");

    public static AzureMlFoundation LoadFoundation(IConfiguration configuration)
    {
        var value = ReadSelected(configuration, "WAN_STUDIO_FOUNDATION");
        var missing = RuntimeFoundation.Where(key => Text(value, key) is null).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Foundation JSON is missing {string.Join(", ", missing)}. Keys are case-sensitive camelCase and must be at the top level.");
        }

        var foundation = new AzureMlFoundation(
            Uuid(Text(value, "subscriptionId"), "subscriptionId"), Uuid(Text(value, "tenantId"), "tenantId"),
            Name(Text(value, "resourceGroupName")), Name(Text(value, "workspaceName")), Name(Text(value, "computeName")),
            Text(value, "computeId")!, Uuid(Text(value, "computeIdentityClientId"), "computeIdentityClientId"),
            Name(Text(value, "storageAccountName")), Name(Text(value, "containerName")), Name(Text(value, "datastoreName")),
            value.TryGetProperty("portal", out var portal) && portal.ValueKind == JsonValueKind.Object ? Text(portal, "hostname") : null);
        if (!string.Equals(foundation.ComputeId, $"{foundation.WorkspaceArmId}/computes/{foundation.ComputeName}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Foundation resource IDs differ from the selected workspace scope.");
        }

        return foundation;
    }

    /// <summary>Binds a prepared release to the runtime foundation fields.</summary>
    public static string ScopeFingerprint(AzureMlFoundation foundation)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["subscriptionId"] = foundation.SubscriptionId, ["tenantId"] = foundation.TenantId, ["resourceGroupName"] = foundation.ResourceGroupName,
            ["workspaceName"] = foundation.WorkspaceName, ["computeName"] = foundation.ComputeName, ["computeId"] = foundation.ComputeId,
            ["computeIdentityClientId"] = foundation.ComputeIdentityClientId, ["storageAccountName"] = foundation.StorageAccountName,
            ["containerName"] = foundation.ContainerName, ["datastoreName"] = foundation.DatastoreName,
        };
        var payload = JsonSerializer.Serialize(fields, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>The release from WAN_STUDIO_RELEASE_JSON (hosted) or WAN_STUDIO_CONTRACT (operator machine); null when neither is set (not prepared).</summary>
    public static AzureMlRelease? LoadRelease(IConfiguration configuration, AzureMlFoundation foundation)
    {
        if (!string.IsNullOrWhiteSpace(configuration["WAN_STUDIO_RELEASE_JSON"]))
        {
            using var document = JsonDocument.Parse(configuration["WAN_STUDIO_RELEASE_JSON"]!);
            var pointers = document.RootElement;
            if (pointers.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("WAN_STUDIO_RELEASE_JSON must contain a JSON object.");
            }

            var environmentId = Text(pointers, "environmentId") ?? throw new InvalidOperationException("WAN_STUDIO_RELEASE_JSON needs environmentId.");
            var models = Text(pointers, "modelsRef") is { Length: > 0 } given ? given : foundation.DatastorePath("models/wan/");
            var version = VersionOf(environmentId);
            return Check(foundation, new AzureMlRelease(environmentId, models, models.StartsWith("azureml://", StringComparison.Ordinal) ? "data" : "model",
                Text(pointers, "codeUri") is { Length: > 0 } code ? code : foundation.DatastorePath($"code/{version}-wan/"), version));
        }

        if (string.IsNullOrWhiteSpace(configuration["WAN_STUDIO_CONTRACT"]))
        {
            return null;
        }

        var manifest = ReadSelected(configuration, "WAN_STUDIO_CONTRACT");
        if (Text(manifest, "profile") != Profile || Text(manifest, "sourceSha") != UpstreamSha ||
            Text(manifest, "scopeFingerprint") != ScopeFingerprint(foundation) ||
            Text(manifest, "computeIdentityClientId")?.ToLowerInvariant() != foundation.ComputeIdentityClientId)
        {
            throw new InvalidOperationException("The prepared release does not match this studio; run Prepare again.");
        }

        var env = Text(manifest, "environmentId") ?? throw new InvalidOperationException("The prepared release has no environmentId.");
        var modelsRef = Text(manifest, "modelsRef") ?? throw new InvalidOperationException("The prepared release has no modelsRef.");
        return Check(foundation, new AzureMlRelease(env, modelsRef, Text(manifest, "modelsKind") ?? (modelsRef.StartsWith("azureml://", StringComparison.Ordinal) ? "data" : "model"),
            Text(manifest, "codeUri") ?? throw new InvalidOperationException("The prepared release has no codeUri."), Text(manifest, "version") ?? VersionOf(env)));
    }

    private static string VersionOf(string environmentId) => environmentId[(environmentId.LastIndexOfAny([':', '/']) + 1)..];

    private static AzureMlRelease Check(AzureMlFoundation foundation, AzureMlRelease release)
    {
        var armEnvironment = $"{foundation.WorkspaceArmId}/environments/";
        var environmentOk = AssetRule().IsMatch(release.EnvironmentId) ||
            (release.EnvironmentId.StartsWith(armEnvironment, StringComparison.OrdinalIgnoreCase) && release.EnvironmentId[armEnvironment.Length..].Split('/') is [{ Length: > 0 }, "versions", { Length: > 0 }]);
        if (!environmentOk || release.Version.Length == 0)
        {
            throw new InvalidOperationException("Release environmentId must be azureml:<name>:<version> or this workspace's environment version id.");
        }

        var modelsOk = release.ModelsRef.StartsWith("azureml://", StringComparison.Ordinal)
            ? AzureMlGuard.IsDatastorePath(release.ModelsRef, foundation.DatastoreName, string.Empty)
            : AssetRule().IsMatch(release.ModelsRef);
        if (!modelsOk || !AzureMlGuard.IsDatastorePath(release.CodeUri, foundation.DatastoreName, string.Empty))
        {
            throw new InvalidOperationException("Release modelsRef/codeUri must be this studio datastore paths or a registered azureml:<name>:<version> asset.");
        }

        return release;
    }
}

/// <summary>Whether generation is armed (Start ran): re-read on every call, so Stop halts a batch between submissions.</summary>
public sealed class AzureMlGate(IConfiguration configuration)
{
    public bool IsArmed(AzureMlFoundation foundation, AzureMlRelease? release)
    {
        if (release is null)
        {
            return false;
        }

        string? text;
        try
        {
            var path = configuration["WAN_STUDIO_GATE"];
            text = !string.IsNullOrWhiteSpace(path) ? (File.Exists(path) ? File.ReadAllText(path) : null) : configuration["WAN_STUDIO_ARMED"];
        }
        catch (IOException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var gate = document.RootElement;
            return gate.ValueKind == JsonValueKind.Object &&
                Text(gate, "compute") == foundation.ComputeName &&
                Text(gate, "profile") == AzureMlConfig.Profile &&
                Text(gate, "version") == release.Version;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
