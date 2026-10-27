using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace VideoSafetyStudio.Application.Operations;

/// <summary>Structural schemas come from the same strict .NET types; contextual scope/state rules remain in the readers.</summary>
public static class StudioContractSchemas
{
    public static IReadOnlyDictionary<string, Type> Types { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["build"] = typeof(GpuBuildManifest),
        ["release"] = typeof(RegionalRelease),
        ["trainer"] = typeof(TrainerBinding),
        ["settings"] = typeof(StudioSettings),
        ["job"] = typeof(StudioJobRecord),
    };

    public static JsonObject Export(string contract)
    {
        var type = Types.TryGetValue(contract, out var found)
            ? found : throw new ArgumentException("Unknown studio contract.", nameof(contract));
        var schema = StudioContracts.Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject value) return node;
                if (context.PropertyInfo is { } property)
                {
                    switch (property.Name)
                    {
                        case "artifactId" or "scopeFingerprint" or "sha256" or "dependencyLockSha256":
                            value["pattern"] = "^[a-f0-9]{64}$";
                            break;
                        case "sourceSha" or "revision":
                            value["pattern"] = "^[a-f0-9]{40}$";
                            break;
                        case "module":
                            value["pattern"] = "^[a-z_][a-z0-9_]*(\\.[a-z_][a-z0-9_]*)*$";
                            break;
                        case "imageDigest":
                            value["pattern"] = "^sha256:[a-f0-9]{64}$";
                            break;
                        case "profile":
                            value["enum"] = new JsonArray("wan", "ltx", "ltx_i2v", "minimax_h3");
                            break;
                        case "bytes":
                            value["minimum"] = context.Path.ToArray().Contains("models") ? 1 : 0;
                            break;
                        case "version" or "number" or "maxNodes" or "maxConcurrentJobs":
                            value["minimum"] = 1;
                            break;
                        case "maxJobMinutes" or "queueMoveMinutes":
                            value["minimum"] = 1;
                            value["maximum"] = StudioContracts.MaxJobMinutes;
                            break;
                        case "timeoutSeconds":
                            value["minimum"] = 1;
                            value["maximum"] = StudioContracts.MaxJobMinutes * 60;
                            break;
                        case "priority":
                            value["minimum"] = 0;
                            break;
                        case "maxPaygHourlyUsd":
                            value["exclusiveMinimum"] = 0;
                            break;
                        case "vmSize":
                            value["const"] = StudioContracts.VmSize;
                            break;
                        case "vmPriority":
                            value["const"] = "LowPriority";
                            break;
                        case "minNodes":
                            value["const"] = 0;
                            break;
                        case "idleSeconds":
                            value["const"] = 120;
                            break;
                        case "instanceCount":
                            value["const"] = 1;
                            break;
                        case "gpuSubnetDedicated":
                            value["const"] = true;
                            break;
                        case "regions":
                            value["minProperties"] = 1;
                            break;
                        case "privateConnectivityHosts":
                            value["minItems"] = 5;
                            break;
                    }
                }
                if (value["properties"] is JsonObject properties && properties["schemaVersion"] is JsonObject version)
                {
                    version["const"] = context.TypeInfo.Type == typeof(StudioFoundation) ? 2 : 1;
                }
                if (context.TypeInfo.Type.IsArray && !value.ContainsKey("minItems")) value["minItems"] = 1;
                return value;
            },
        }).AsObject();
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = $"urn:wan-safety-studio:{contract}:v{(type == typeof(StudioFoundation) ? 2 : 1)}";
        return schema;
    }
}
