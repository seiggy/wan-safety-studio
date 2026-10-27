using System.Globalization;
using System.Text.Json.Nodes;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

/// <summary>Live-resource contracts and eligibility checks ported from the operator script; every mismatch refuses rather than adapts.</summary>
internal static class StudioPolicy
{
    public const string MlApi = "2024-10-01";
    public const string NetworkApi = "2024-05-01";
    public const string SkuApi = "2021-07-01";
    public const int CoresPerNode = 24;

    private static readonly string[] ReadOnlySubnetProperties =
        ["natGateway", "provisioningState", "ipConfigurations", "resourceNavigationLinks", "serviceAssociationLinks", "privateEndpoints", "purpose"];

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool Same(string? left, string? right) => left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static void Refuse(string message) => throw new ConflictException(message);

    public static void Require(bool condition, string message)
    {
        if (!condition) Refuse(message);
    }

    /// <summary>Exact id and every ownership tag; ephemeral resources are owned by the studio after the verified-OFF handoff.</summary>
    public static void Owned(JsonNode? resource, string id, Dictionary<string, string> tags, bool ephemeral)
    {
        Require(resource is not null && Same(Text(resource["id"]), id), $"Unexpected or missing resource {id[(id.LastIndexOf('/') + 1)..]}.");
        foreach (var (key, value) in tags)
        {
            var expected = ephemeral && key == "managedBy" ? "studio" : value;
            Require(Text(resource!["tags"]?[key]) == expected, $"Ownership tags missing on {id[(id.LastIndexOf('/') + 1)..]}; refusing mutation.");
        }
    }

    public static Dictionary<string, string> EphemeralTags(Dictionary<string, string> ownership) =>
        ownership.ToDictionary(pair => pair.Key, pair => pair.Key == "managedBy" ? "studio" : pair.Value, StringComparer.Ordinal);

    public static JsonObject ComputeBody(RegionalFoundation region, string location) => new()
    {
        ["location"] = location,
        ["tags"] = TagsNode(EphemeralTags(region.OwnershipTags)),
        ["identity"] = new JsonObject
        {
            ["type"] = "UserAssigned",
            ["userAssignedIdentities"] = new JsonObject { [region.ComputeIdentityId] = new JsonObject() },
        },
        ["properties"] = new JsonObject
        {
            ["computeType"] = "AmlCompute",
            ["computeLocation"] = location,
            ["disableLocalAuth"] = true,
            ["description"] = "Single Spot A100; no dedicated/serverless fallback.",
            ["properties"] = new JsonObject
            {
                ["vmSize"] = region.Policy.VmSize,
                ["vmPriority"] = region.Policy.VmPriority,
                ["osType"] = "Linux",
                ["enableNodePublicIp"] = false,
                ["remoteLoginPortPublicAccess"] = "Disabled",
                ["subnet"] = new JsonObject { ["id"] = region.LandingZone.GpuSubnetId },
                ["scaleSettings"] = new JsonObject
                {
                    ["minNodeCount"] = 0,
                    ["maxNodeCount"] = region.Policy.MaxNodes,
                    ["nodeIdleTimeBeforeScaleDown"] = "PT120S",
                },
            },
        },
    };

    public static JsonObject PublicIpBody(RegionalFoundation region, string location) => new()
    {
        ["location"] = location,
        ["sku"] = new JsonObject { ["name"] = "Standard" },
        ["tags"] = TagsNode(EphemeralTags(region.OwnershipTags)),
        ["properties"] = new JsonObject
        {
            ["publicIPAllocationMethod"] = "Static",
            ["publicIPAddressVersion"] = "IPv4",
            ["ipTags"] = new JsonArray([.. region.Policy.EgressPublicIpTags.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode)new JsonObject { ["ipTagType"] = pair.Key, ["tag"] = pair.Value })]),
        },
    };

    public static JsonObject NatBody(RegionalFoundation region, string location) => new()
    {
        ["location"] = location,
        ["sku"] = new JsonObject { ["name"] = "Standard" },
        ["tags"] = TagsNode(EphemeralTags(region.OwnershipTags)),
        ["properties"] = new JsonObject
        {
            ["idleTimeoutInMinutes"] = 4,
            ["publicIpAddresses"] = new JsonArray(new JsonObject { ["id"] = region.PublicIpId }),
        },
    };

    private static JsonNode TagsNode(Dictionary<string, string> tags) =>
        new JsonObject(tags.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));

    /// <summary>The dedicated customer subnet may only carry this region's expected NSG and, while on, this region's NAT.</summary>
    public static void Subnet(JsonNode? subnet, RegionalFoundation region)
    {
        var id = region.LandingZone.GpuSubnetId;
        Require(subnet is not null && Same(Text(subnet["id"]), id) && Text(subnet["name"]) == id[(id.LastIndexOf('/') + 1)..] &&
            region.LandingZone.GpuSubnetDedicated, "Refusing mutation of an unexpected or shared customer subnet.");
        var properties = subnet!["properties"];
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        if (Text(properties?["addressPrefix"]) is { } single) prefixes.Add(single);
        if (properties?["addressPrefixes"] is JsonArray many) foreach (var prefix in many.Select(Text).OfType<string>()) prefixes.Add(prefix);
        Require(prefixes.Count == 1 && prefixes.Single() == region.LandingZone.GpuSubnetCidr, "GPU subnet CIDR differs from the explicit dedicated reservation.");
        if (properties?["networkSecurityGroup"] is JsonObject nsg)
        {
            Require(Same(Text(nsg["id"]), region.NetworkSecurityGroupId), "The GPU subnet has an unrelated NSG; refusing to replace its association.");
        }

        if (properties?["natGateway"] is JsonObject nat)
        {
            Require(region.Policy.ManageComputeEgress && Same(Text(nat["id"]), region.NatGatewayId),
                "The GPU subnet has an unrelated NAT; refusing to replace or detach its association.");
        }
    }

    public static void SubnetDefaults(JsonNode subnet) => Require(
        subnet["properties"]?["defaultOutboundAccess"] is JsonValue value && value.TryGetValue<bool>(out var allowed) && !allowed &&
        (subnet["properties"]?["delegations"] as JsonArray)?.Count is null or 0,
        "The GPU subnet must have default outbound access disabled and no delegation.");

    /// <summary>The same live subnet properties with this region's NAT set (or removed), preserving unrelated customer settings.</summary>
    public static JsonObject SubnetWrite(JsonNode subnet, string? natId)
    {
        var properties = (JsonObject)subnet["properties"]!.DeepClone();
        foreach (var key in ReadOnlySubnetProperties) properties.Remove(key);
        if (natId is not null) properties["natGateway"] = new JsonObject { ["id"] = natId };
        return new JsonObject { ["properties"] = properties };
    }

    public static void Cluster(JsonNode? compute, RegionalFoundation region)
    {
        Owned(compute, region.ComputeId, region.OwnershipTags, ephemeral: true);
        var p = compute!["properties"];
        var c = p?["properties"];
        var identities = (compute["identity"]?["userAssignedIdentities"] as JsonObject)?.Select(pair => pair.Key).ToList() ?? [];
        Require(Text(p?["computeType"]) == "AmlCompute" && Text(p?["provisioningState"]) == "Succeeded" &&
            p?["disableLocalAuth"]?.GetValueKind() == System.Text.Json.JsonValueKind.True &&
            Text(c?["vmSize"]) == region.Policy.VmSize && Text(c?["vmPriority"]) == region.Policy.VmPriority && Text(c?["osType"]) == "Linux" &&
            Long(c?["scaleSettings"]?["minNodeCount"]) == 0 && Long(c?["scaleSettings"]?["maxNodeCount"]) == region.Policy.MaxNodes &&
            Text(c?["scaleSettings"]?["nodeIdleTimeBeforeScaleDown"]) is "PT120S" or "PT2M" &&
            c?["enableNodePublicIp"]?.GetValueKind() == System.Text.Json.JsonValueKind.False &&
            Text(c?["remoteLoginPortPublicAccess"]) == "Disabled" && Same(Text(c?["subnet"]?["id"]), region.LandingZone.GpuSubnetId) &&
            Text(compute["identity"]?["type"]) == "UserAssigned" && identities.Count == 1 && Same(identities[0], region.ComputeIdentityId),
            "Live compute violates the one-node-per-job private Spot, identity and idle-120 contract.");
    }

    public static void Egress(JsonNode? subnet, JsonNode? nat, JsonNode? pip, RegionalFoundation region)
    {
        Owned(nat, region.NatGatewayId, region.OwnershipTags, ephemeral: true);
        Owned(pip, region.PublicIpId, region.OwnershipTags, ephemeral: true);
        var addresses = nat!["properties"]?["publicIpAddresses"] as JsonArray;
        Require(Same(Text(subnet?["properties"]?["natGateway"]?["id"]), region.NatGatewayId) &&
            Text(nat["properties"]?["provisioningState"]) == "Succeeded" && addresses is { Count: 1 } &&
            Same(Text(addresses[0]?["id"]), region.PublicIpId) && Text(pip!["properties"]?["provisioningState"]) == "Succeeded" &&
            Same(Text(pip["properties"]?["natGateway"]?["id"]), region.NatGatewayId),
            "Owned NAT and public-IP linkage is not ready; submissions remain blocked.");
        var tags = (pip!["properties"]?["ipTags"] as JsonArray)?.Where(tag => tag is not null).ToList() ?? [];
        Require(tags.Count == region.Policy.EgressPublicIpTags.Count && region.Policy.EgressPublicIpTags.All(pair =>
            tags.Count(tag => Text(tag!["ipTagType"]) == pair.Key && Text(tag["tag"]) == pair.Value) == 1),
            "Live public-IP policy tags differ from the declared customer policy.");
    }

    public static long? Long(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
        long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static decimal? Decimal(JsonNode? node) => node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
        decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static decimal PaygRate(IEnumerable<JsonNode> items, RegionalFoundation region, string location)
    {
        var rates = items.Where(item => Text(item["armSkuName"]) == region.Policy.VmSize && Text(item["armRegionName"]) == location &&
            Text(item["type"]) == "Consumption" && Text(item["currencyCode"]) == "USD" && Text(item["unitOfMeasure"]) == "1 Hour" &&
            item["isPrimaryMeterRegion"]?.GetValueKind() == System.Text.Json.JsonValueKind.True && Text(item["serviceName"]) == "Virtual Machines" &&
            Text(item["productName"]) is "Virtual Machines NCads A100 v4 Series" or "NCads A100 v4 Series Linux" &&
            !SpotLike(Text(item["skuName"])) && !SpotLike(Text(item["meterName"]))).ToList();
        Require(rates.Count == 1, "Missing or ambiguous exact Linux pay-as-you-go rate; Start is blocked.");
        var price = Decimal(rates[0]["retailPrice"]);
        Require(price is > 0 && price <= region.Policy.MaxPaygHourlyUsd,
            $"Published pay-as-you-go price is invalid or exceeds the approved {region.Policy.MaxPaygHourlyUsd.ToString(CultureInfo.InvariantCulture)} USD/node-hour threshold.");
        return price!.Value;
    }

    private static bool SpotLike(string? text) => text is not null &&
        (text.Contains("Spot", StringComparison.OrdinalIgnoreCase) || text.Contains("Low Priority", StringComparison.OrdinalIgnoreCase));

    public static void Sku(IReadOnlyList<JsonNode> skus, RegionalFoundation region)
    {
        var matches = skus.Where(sku => Text(sku["name"]) == region.Policy.VmSize && Text(sku["resourceType"]) == "virtualMachines").ToList();
        Require(matches.Count == 1 && matches[0]["restrictions"] is JsonArray { Count: 0 },
            "The A100 SKU is absent, ambiguous or restricted in this region; no fallback is allowed.");
    }

    public static void Quota(IReadOnlyList<JsonNode> usages, int allocatedCores)
    {
        Require(allocatedCores is 0 or CoresPerNode, "Unexpected current allocation.");
        var total = usages.Where(usage => Text(usage["name"]?["value"]) == "TotalLowPriorityCores").ToList();
        var family = usages.Where(usage => Text(usage["name"]?["value"]) == "standardNCADSA100v4Family" &&
            Text(usage["type"]) == "Microsoft.MachineLearningServices/vmFamily/lowPriorityCores/usages").ToList();
        Require(total.Count == 1 && family.Count == 1, "Missing or ambiguous Azure ML Spot or A100 family quota.");
        foreach (var quota in new[] { total[0], family[0] })
        {
            Require(Long(quota["currentValue"]) is >= 0 && Long(quota["limit"]) is not null, "Malformed Azure ML quota.");
        }

        Require(Long(total[0]["limit"]) >= CoresPerNode && Long(total[0]["currentValue"]) >= allocatedCores &&
            Long(total[0]["limit"]) - Long(total[0]["currentValue"]) + allocatedCores >= CoresPerNode,
            "Insufficient TotalLowPriorityCores for one A100 node.");
        var limit = Long(family[0]["limit"]);
        Require(limit == -1 || (limit >= CoresPerNode && Long(family[0]["currentValue"]) >= allocatedCores &&
            limit - Long(family[0]["currentValue"]) + allocatedCores >= CoresPerNode),
            "Insufficient A100 Spot family quota (-1 is a shared pool, not unavailable).");
    }

    public static readonly string[] TerminalStatuses = ["Completed", "Failed", "Canceled", "Cancelled"];

    public static bool Terminal(JsonNode job) => Text(job["properties"]?["status"]) is { } status && TerminalStatuses.Contains(status);

    /// <summary>Jobs that can use this compute. A job whose compute or status is unreadable cannot be proven unrelated, so it blocks.</summary>
    public static IEnumerable<JsonNode> OnCompute(IEnumerable<JsonNode> jobs, StudioFoundation foundation, RegionalFoundation region)
    {
        foreach (var job in jobs)
        {
            var compute = Text(job["properties"]?["computeId"]);
            Require(!string.IsNullOrWhiteSpace(compute) && Text(job["id"]) is { } id && id.StartsWith(region.WorkspaceId + "/jobs/", StringComparison.OrdinalIgnoreCase) &&
                Text(job["name"]) is not null && Text(job["properties"]?["status"]) is not null, "A workspace job has an unknown compute or status; refusing to assume it is unrelated.");
            if (Same(compute, region.ComputeId) || Same(compute, StudioResourceNames.NameOf(region.ComputeId)))
            {
                yield return job;
            }
        }
    }

    public static string[] PrivateHosts(RegionalFoundation region) =>
    [
        .. region.PrivateConnectivityHosts.Where(host => !host.StartsWith("*.", StringComparison.Ordinal)).Select(host => host.ToLowerInvariant()
            .Replace(".privatelink.vaultcore.azure.net", ".vault.azure.net", StringComparison.Ordinal)
            .Replace(".privatelink.", ".", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal),
    ];
}
