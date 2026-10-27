using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

public sealed record StudioScope(string TenantId, string SubscriptionId, string DeploymentName);

public sealed record LandingZone(
    string GpuSubnetId, string GpuSubnetCidr, bool GpuSubnetDedicated, string? ExistingGpuNsgId);

public sealed record ComputePolicy(
    string VmSize, string VmPriority, int MinNodes, int MaxNodes, int IdleSeconds, int InstanceCount,
    int MaxJobMinutes, decimal MaxPaygHourlyUsd, bool ManageComputeEgress,
    Dictionary<string, string> EgressPublicIpTags);

public sealed record RegionalFoundation(
    string WorkspaceId, string ComputeId, string ComputeIdentityId,
    string ComputeIdentityClientId, string StorageAccountId, string RegistryId,
    string NetworkSecurityGroupId, string NatGatewayId, string PublicIpId,
    string Container, string Datastore, string ReleaseContainer, string ReleaseDatastore, string ControlContainer,
    LandingZone LandingZone, ComputePolicy Policy, Dictionary<string, string> OwnershipTags, string[] PrivateConnectivityHosts);

/// <summary>The validated runtime model built from <see cref="StudioOptions"/> (never parsed from a Terraform receipt).</summary>
public sealed record StudioFoundation(StudioScope Scope, string ControlRegion, Dictionary<string, RegionalFoundation> Regions);

public sealed record ArtifactFile(string Path, long Bytes, string Sha256);

public sealed record ModelArtifact(
    string Path, long Bytes, string Sha256, string Source, string Repository, string Revision, string License);

public sealed record GpuBuildManifest(
    int SchemaVersion, string ArtifactId, string Profile, string SourceSha, string BaseImage,
    string DependencyLockSha256, string ImageDigest, ArtifactFile[] Code, ModelArtifact[] Models, ArtifactFile[] Licenses,
    ArtifactFile[] BuildInputs);

public sealed record RegionalRelease(
    int SchemaVersion, string Region, string WorkspaceId, string ScopeFingerprint, string ArtifactId,
    string EnvironmentId, string ModelsRef, string CodeUri, string ModelsUri, string Image,
    DateTimeOffset VerifiedAt, string VerifiedBy);

/// <summary>
/// A trainer image the pipeline promoted into one region, with the metadata the server shows. The server runs only this exact
/// digest through its fixed command form (<c>python -m Module all --preset ...</c>); it never builds, installs or fetches anything.
/// </summary>
public sealed record TrainerBinding(
    int SchemaVersion, string Region, string WorkspaceId, string ScopeFingerprint, string ArtifactId, string Name, string Version,
    string Module, string[] Presets, string DefaultPreset, string LicenseCode, string LicenseWeights, string SourceSha,
    string ImageDigest, string Image, string EnvironmentId, DateTimeOffset VerifiedAt, string VerifiedBy);

public sealed record RegionSettings(int MaxConcurrentJobs, int MaxJobMinutes, int Priority);

public sealed record StudioSettings(
    int SchemaVersion, long Version, string ScopeFingerprint, int MaxConcurrentJobs, int QueueMoveMinutes,
    Dictionary<string, RegionSettings> Regions);

public enum AttemptPhase { Intent, Accepted, Queued, Running, CancelRequested, Unknown, Completed, Failed, Canceled }

public sealed record JobAttempt(
    int Number, string Region, string WorkspaceId, string RemoteName, string ArtifactId, int TimeoutSeconds,
    AttemptPhase Phase, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? TerminalVerifiedAt, string OutputUri);

public sealed record StudioJobRecord(
    int SchemaVersion, long Version, StudioScope Scope, string Name, JobAttempt[] Attempts);

/// <summary>Strict JSON at build/deployment/recovery boundaries; API JSON remains independently backwards-compatible.</summary>
public static class StudioContracts
{
    public const string VmSize = "Standard_NC24ads_A100_v4";
    public const int MaxJobMinutes = 120;
    public const string RunContainer = "wan-studio";
    public const string RunDatastore = "wan_blob";
    public const string ReleaseContainer = "wan-release";
    public const string ReleaseDatastore = "wan_release";
    public const string ControlContainer = "wan-control";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter<AttemptPhase>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<RegionPhase>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static GpuBuildManifest ReadBuild(string json)
    {
        var manifest = Read<GpuBuildManifest>(json);
        StudioContractRules.Build(manifest);
        return manifest;
    }

    public static RegionalRelease ReadRelease(string json, StudioFoundation foundation, GpuBuildManifest build)
    {
        var binding = Read<RegionalRelease>(json);
        StudioContractRules.Release(binding, foundation, build);
        return binding;
    }

    public static TrainerBinding ReadTrainer(string json, StudioFoundation foundation)
    {
        var binding = Read<TrainerBinding>(json);
        StudioContractRules.Trainer(binding, foundation);
        return binding;
    }

    public static StudioSettings ReadSettings(string json, StudioFoundation foundation)
    {
        var settings = Read<StudioSettings>(json);
        StudioContractRules.Settings(settings, foundation);
        return settings;
    }

    public static StudioJobRecord ReadJob(string json, StudioFoundation foundation)
    {
        var job = Read<StudioJobRecord>(json);
        StudioContractRules.Job(job, foundation);
        return job;
    }

    public static StudioOperationalState ReadState(string json, StudioFoundation foundation)
    {
        var state = Read<StudioOperationalState>(json);
        StudioContractRules.State(state, foundation);
        return state;
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);


    private static T Read<T>(string json) where T : class
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateKeys(document.RootElement);
            return document.Deserialize<T>(Options) ?? throw new ValidationException("Contract must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new ValidationException("Contract JSON is malformed or does not match its versioned field/type contract.");
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ValidationException("Contract JSON contains a duplicate property.");
                }
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) RejectDuplicateKeys(child);
        }
    }

    public static string ArtifactId(GpuBuildManifest manifest) => Hash(manifest with
    {
        ArtifactId = "",
        Code = [.. manifest.Code.OrderBy(file => file.Path, StringComparer.Ordinal)],
        Models = [.. manifest.Models.OrderBy(file => file.Path, StringComparer.Ordinal)],
        Licenses = [.. manifest.Licenses.OrderBy(file => file.Path, StringComparer.Ordinal)],
        BuildInputs = [.. manifest.BuildInputs.OrderBy(file => file.Path, StringComparer.Ordinal)],
    });

    public static string RegionFingerprint(StudioFoundation foundation, string region)
    {
        var value = foundation.Regions[region];
        return Hash(new
        {
            foundation.Scope,
            region,
            value.WorkspaceId,
            value.ComputeId,
            value.ComputeIdentityId,
            value.ComputeIdentityClientId,
            value.StorageAccountId,
            value.RegistryId,
            value.Container,
            value.Datastore,
            value.ReleaseContainer,
            value.ReleaseDatastore,
            value.ControlContainer,
        });
    }

    public static string ScopeFingerprint(StudioFoundation foundation) => Hash(new
    {
        foundation.Scope,
        foundation.ControlRegion,
        regions = foundation.Regions.Keys.Order(StringComparer.Ordinal)
            .ToDictionary(region => region, region => RegionFingerprint(foundation, region), StringComparer.Ordinal),
    });

    public static void RequireNextVersion(long current, long proposed)
    {
        if (current < 0 || current == long.MaxValue || proposed != current + 1)
        {
            throw new ConflictException("Operational state changed; reload it before saving.");
        }
    }

    private static string Hash<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        var json = new StringBuilder();
        Canonical(node, json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString())));
    }

    private static void Canonical(JsonNode? node, StringBuilder json)
    {
        if (node is JsonObject obj)
        {
            json.Append('{');
            var first = true;
            foreach (var (key, child) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first) json.Append(',');
                first = false;
                String(key, json);
                json.Append(':');
                Canonical(child, json);
            }
            json.Append('}');
        }
        else if (node is JsonArray array)
        {
            json.Append('[');
            for (var index = 0; index < array.Count; index++)
            {
                if (index > 0) json.Append(',');
                Canonical(array[index], json);
            }
            json.Append(']');
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            String(text, json);
        }
        else json.Append(node?.ToJsonString(Options) ?? "null");
    }

    private static void String(string text, StringBuilder json)
    {
        // JSON escaping is fixed across runtimes, including supplementary Unicode and control characters.
        json.Append('"');
        foreach (var character in text)
        {
            json.Append(character switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f",
                '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\u" + ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString(),
            });
        }
        json.Append('"');
    }
}
