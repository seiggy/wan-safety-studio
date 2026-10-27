using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

/// <summary>A small in-memory ARM that behaves like the pieces of Azure the lifecycle depends on.</summary>
internal sealed class SimArm : IStudioArm
{
    public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<JsonNode>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Writes { get; } = [];
    public List<string?> IfMatches { get; } = [];
    public Func<string, string, bool>? Fail { get; set; }
    public bool CancelFails { get; set; }
    public bool JobsStayActive { get; set; }
    public bool ProvisionFails { get; set; }

    private IEnumerable<JsonNode> Jobs() => Lists.Where(pair => pair.Key.EndsWith("/jobs", StringComparison.OrdinalIgnoreCase)).SelectMany(pair => pair.Value);

    private static string Path(string id) => id.Contains('?') ? id[..id.IndexOf('?')] : id;

    public Task<JsonNode?> GetAsync(string id, string api, CancellationToken ct)
    {
        if (Fail?.Invoke("GET", id) == true) throw new HttpRequestException("ARM GET failed (HTTP 500).");
        return Task.FromResult(Resources.TryGetValue(id, out var node) ? node.DeepClone() : null);
    }

    public Task<IReadOnlyList<JsonNode>> ListAsync(string id, string api, CancellationToken ct)
    {
        if (Fail?.Invoke("LIST", id) == true) throw new HttpRequestException("ARM list failed (HTTP 500).");
        return Task.FromResult<IReadOnlyList<JsonNode>>(Lists.TryGetValue(Path(id), out var items) ? [.. items.Select(item => item.DeepClone())] : []);
    }

    public Task<JsonNode?> SendAsync(string method, string id, string api, JsonNode? body, string? ifMatch, CancellationToken ct)
    {
        var path = Path(id);
        if (Fail?.Invoke(method, path) == true) throw new HttpRequestException($"ARM {method} failed (HTTP 500).");
        Writes.Add($"{method} {id}");
        IfMatches.Add(ifMatch);
        if (method == "DELETE")
        {
            Resources.Remove(path);
            if (path.EndsWith("/computes/wan-gpu", StringComparison.OrdinalIgnoreCase) && !JobsStayActive)
            {
                foreach (var job in Jobs()) job["properties"]!["status"] = "Canceled";
            }
        }
        else if (method == "POST" && path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            if (CancelFails) throw new HttpRequestException("ARM POST failed (HTTP 500).");
            foreach (var job in Jobs().Where(job => path.StartsWith(job["id"]!.GetValue<string>(), StringComparison.Ordinal)))
            {
                job["properties"]!["status"] = "Canceled";
            }
        }
        else if (method == "PUT" && Resources.TryGetValue(path, out var existing) && existing["etag"] is not null)
        {
            var properties = (JsonObject)body!["properties"]!.DeepClone();
            existing["properties"] = properties;
            existing["etag"] = $"etag-{Writes.Count}";
            foreach (var resource in Resources.Values.Where(value => value["properties"]?["subnets"] is not null).ToList())
            {
                if (properties["natGateway"] is null) resource["properties"]!.AsObject().Remove("subnets");
            }

            if (properties["natGateway"]?["id"]?.GetValue<string>() is { } nat && Resources.TryGetValue(nat, out var natResource))
            {
                natResource["properties"]!["subnets"] = new JsonArray(new JsonObject { ["id"] = path });
            }
        }
        else if (method == "PUT")
        {
            var resource = (JsonObject)body!.DeepClone();
            resource["id"] = path;
            resource["name"] = path[(path.LastIndexOf('/') + 1)..];
            var properties = resource["properties"] as JsonObject ?? [];
            resource["properties"] = properties;
            properties["provisioningState"] = ProvisionFails && path.EndsWith("/computes/wan-gpu", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Succeeded";
            if (path.EndsWith("/computes/wan-gpu", StringComparison.OrdinalIgnoreCase))
            {
                properties["properties"]!["nodeStateCounts"] = new JsonObject { ["idleNodeCount"] = 0, ["runningNodeCount"] = 0 };
            }

            if (properties["publicIpAddresses"] is JsonArray addresses && addresses[0]?["id"]?.GetValue<string>() is { } pip && Resources.TryGetValue(pip, out var pipResource))
            {
                pipResource["properties"]!["natGateway"] = new JsonObject { ["id"] = path };
            }

            Resources[path] = resource;
        }

        return Task.FromResult<JsonNode?>(null);
    }
}

internal sealed class SimStore : IStudioControlStore, IStudioReleaseReader
{
    private int version;
    public string? Json { get; set; }
    public bool Down { get; set; }
    public Dictionary<string, string> Audit { get; } = [];
    public string? SettingsJson { get; set; }
    public Action? BeforeSettingsWrite { get; set; }
    public void BumpSettings() => settingsVersion++;
    private int settingsVersion;

    public Task<StoredState?> ReadSettingsAsync(CancellationToken ct) =>
        Down ? throw new IOException("Control store is unreachable.") : Task.FromResult(SettingsJson is null ? null : new StoredState(SettingsJson, settingsVersion.ToString()));

    public Task<string> WriteSettingsAsync(string json, string? etag, CancellationToken ct)
    {
        BeforeSettingsWrite?.Invoke();
        if ((etag is null && SettingsJson is not null) || (etag is not null && etag != settingsVersion.ToString())) throw new StudioStateConflictException();
        SettingsJson = json;
        return Task.FromResult((++settingsVersion).ToString());
    }

    public Dictionary<(string Region, string Path), string> Release { get; } = [];

    public Task<StoredState?> ReadStateAsync(CancellationToken ct) =>
        Down ? throw new IOException("Control store is unreachable.") : Task.FromResult(Json is null ? null : new StoredState(Json, version.ToString()));

    public Task<string> WriteStateAsync(string json, string? etag, CancellationToken ct)
    {
        if (Down) throw new IOException("Control store is unreachable.");
        if ((etag is null && Json is not null) || (etag is not null && etag != version.ToString())) throw new StudioStateConflictException();
        Json = json;
        return Task.FromResult((++version).ToString());
    }

    public Task AppendAuditAsync(string name, string json, CancellationToken ct)
    {
        if (!Audit.TryAdd(name, json)) throw new StudioStateConflictException();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string region, string prefix, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([.. Release.Keys.Where(key => key.Region == region && key.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(key => key.Path)]);

    public Task<string?> ReadAsync(string region, string path, CancellationToken ct) =>
        Task.FromResult(Release.TryGetValue((region, path), out var json) ? json : null);
}

internal sealed class SimPrices : IStudioPrices
{
    public decimal Price { get; set; } = 3.5m;
    public int Duplicates { get; set; } = 1;

    public Task<IReadOnlyList<JsonNode>> ListAsync(string vmSize, string region, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonNode>>(
        [.. Enumerable.Range(0, Duplicates).Select(_ => (JsonNode)new JsonObject
        {
            ["armSkuName"] = vmSize, ["armRegionName"] = region, ["type"] = "Consumption", ["currencyCode"] = "USD", ["unitOfMeasure"] = "1 Hour",
            ["isPrimaryMeterRegion"] = true, ["serviceName"] = "Virtual Machines", ["productName"] = "Virtual Machines NCads A100 v4 Series",
            ["skuName"] = "NC24ads A100 v4", ["meterName"] = "NC24ads A100 v4", ["retailPrice"] = Price,
        })]);
}

internal sealed class SimNetwork : IStudioConnectivity
{
    public bool Reachable { get; set; } = true;
    public List<string> Hosts { get; } = [];

    public Task AssertPrivateAsync(string host, CancellationToken ct)
    {
        Hosts.Add(host);
        return Reachable ? Task.CompletedTask : throw new ConflictException($"Private endpoint {host} is not reachable on TCP 443.");
    }
}

internal sealed class StudioRig
{
    public const string Actor = "entra:33333333-3333-3333-3333-333333333333";
    private static readonly StudioWaits Waits = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60));

    public required StudioFoundation Foundation { get; init; }
    public required GpuBuildManifest Build { get; init; }
    public SimArm Arm { get; } = new();
    public SimStore Store { get; } = new();
    public SimPrices Prices { get; } = new();
    public SimNetwork Network { get; } = new();
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public StudioArmedGate Gate { get; private set; } = null!;
    public StudioLifecycle Lifecycle { get; private set; } = null!;

    public string ArtifactId => Build.ArtifactId;
    public RegionalFoundation Region(string key = "eastus2") => Foundation.Regions[key];
    public StudioOperationalState State => StudioContracts.ReadState(Store.Json!, Foundation);

    public static async Task<StudioRig> CreateAsync(bool managedEgress = false, TimeProvider? time = null)
    {
        var foundation = StudioSettingsFixture.Foundation(options =>
        {
            if (managedEgress) options.Regions["eastus2"].Compute.ManageEgress = true;
        });
        var build = StudioContracts.ReadBuild(JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "contracts", "fixtures", "studio.json")))!["build"]!.ToJsonString());
        var rig = new StudioRig { Foundation = foundation, Build = build, Time = time ?? TimeProvider.System };        rig.Seed();
        await rig.RestartAsync();
        return rig;
    }

    /// <summary>A host restart: a new lifecycle and gate over the same Azure and durable state, as App Service would start.</summary>
    public async Task RestartAsync()
    {
        Gate = new StudioArmedGate(Time, TimeSpan.FromSeconds(30));
        Lifecycle = new StudioLifecycle(Foundation, Arm, Store, Store, Prices, Network, Gate, Time, Waits, NullLogger<StudioLifecycle>.Instance);
        await Lifecycle.RecoverAsync(default);
    }

    private void Seed()
    {
        foreach (var (key, region) in Foundation.Regions)
        {
            Arm.Resources[region.WorkspaceId] = Resource(region.WorkspaceId, region.OwnershipTags);
            Arm.Resources[region.LandingZone.GpuSubnetId] = new JsonObject
            {
                ["id"] = region.LandingZone.GpuSubnetId, ["name"] = region.LandingZone.GpuSubnetId[(region.LandingZone.GpuSubnetId.LastIndexOf('/') + 1)..],
                ["etag"] = "etag-0",
                ["properties"] = new JsonObject
                {
                    ["addressPrefix"] = region.LandingZone.GpuSubnetCidr, ["defaultOutboundAccess"] = false, ["delegations"] = new JsonArray(),
                    ["networkSecurityGroup"] = new JsonObject { ["id"] = region.NetworkSecurityGroupId },
                    ["routeTable"] = new JsonObject { ["id"] = "preserve-route" }, ["serviceEndpoints"] = new JsonArray(new JsonObject { ["service"] = "fixture" }),
                },
            };
            Arm.Lists[$"{region.WorkspaceId}/jobs"] = [];
            var scope = $"/subscriptions/{Foundation.Scope.SubscriptionId}";
            Arm.Lists[$"{scope}/providers/Microsoft.Compute/skus"] =
                [new JsonObject { ["name"] = region.Policy.VmSize, ["resourceType"] = "virtualMachines", ["restrictions"] = new JsonArray() }];
            Arm.Lists[$"{scope}/providers/Microsoft.MachineLearningServices/locations/{key}/usages"] =
            [
                new JsonObject { ["name"] = new JsonObject { ["value"] = "TotalLowPriorityCores" }, ["currentValue"] = 0, ["limit"] = 100, ["type"] = "x" },
                new JsonObject
                {
                    ["name"] = new JsonObject { ["value"] = "standardNCADSA100v4Family" }, ["currentValue"] = 0, ["limit"] = -1,
                    ["type"] = "Microsoft.MachineLearningServices/vmFamily/lowPriorityCores/usages",
                },
            ];
            PublishRelease(key, region);
        }
    }

    private void PublishRelease(string key, RegionalFoundation region)
    {
        var fingerprint = StudioContracts.RegionFingerprint(Foundation, key);
        var registry = StudioResourceNames.NameOf(region.RegistryId);
        var codeHash = new string('c', 64);
        var modelsHash = new string('d', 64);
        var binding = new RegionalRelease(1, key, region.WorkspaceId, fingerprint, ArtifactId, $"{region.WorkspaceId}/environments/wan-{ArtifactId}/versions/1",
            $"azureml:wan-models-{modelsHash}:1", $"azureml://datastores/{region.ReleaseDatastore}/paths/code/{codeHash}/",
            $"azureml://datastores/{region.ReleaseDatastore}/paths/models/{modelsHash}/", $"{registry}.azurecr.io/wan/runtime@{Build.ImageDigest}",
            DateTimeOffset.UnixEpoch.AddYears(56), "33333333-3333-3333-3333-333333333333");
        Store.Release[(key, $"builds/{ArtifactId}/manifest.json")] = StudioContracts.Write(Build);
        Store.Release[(key, $"releases/{ArtifactId}/{fingerprint}.json")] = StudioContracts.Write(binding);
        Arm.Resources[binding.EnvironmentId] = new JsonObject
        {
            ["id"] = binding.EnvironmentId, ["properties"] = new JsonObject { ["image"] = binding.Image, ["provisioningState"] = "Succeeded" },
        };
        Arm.Resources[$"{region.WorkspaceId}/models/wan-models-{modelsHash}/versions/1"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["modelUri"] = $"azureml://subscriptions/{Foundation.Scope.SubscriptionId}/resourcegroups/rg/workspaces/ws/datastores/{region.ReleaseDatastore}/paths/models/{modelsHash}/",
            },
        };
    }

    public static JsonObject Resource(string id, Dictionary<string, string> tags) => new()
    {
        ["id"] = id, ["name"] = id[(id.LastIndexOf('/') + 1)..],
        ["tags"] = new JsonObject(tags.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
    };

    /// <summary>A compute that satisfies the live contract, owned by the studio unless a different owner is requested.</summary>
    public void SeedCompute(string key = "eastus2", string managedBy = "studio", int nodes = 0)
    {
        var region = Region(key);
        var tags = StudioPolicyTags(region, managedBy);
        var compute = Resource(region.ComputeId, tags);
        compute["identity"] = new JsonObject { ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [region.ComputeIdentityId] = new JsonObject() } };
        compute["properties"] = new JsonObject
        {
            ["computeType"] = "AmlCompute", ["provisioningState"] = "Succeeded", ["disableLocalAuth"] = true,
            ["properties"] = new JsonObject
            {
                ["vmSize"] = region.Policy.VmSize, ["vmPriority"] = "LowPriority", ["osType"] = "Linux", ["enableNodePublicIp"] = false,
                ["remoteLoginPortPublicAccess"] = "Disabled", ["subnet"] = new JsonObject { ["id"] = region.LandingZone.GpuSubnetId },
                ["scaleSettings"] = new JsonObject { ["minNodeCount"] = 0, ["maxNodeCount"] = region.Policy.MaxNodes, ["nodeIdleTimeBeforeScaleDown"] = "PT120S" },
                ["nodeStateCounts"] = new JsonObject { ["idleNodeCount"] = nodes, ["runningNodeCount"] = 0 },
            },
        };
        Arm.Resources[region.ComputeId] = compute;
    }

    public static Dictionary<string, string> StudioPolicyTags(RegionalFoundation region, string managedBy) =>
        region.OwnershipTags.ToDictionary(pair => pair.Key, pair => pair.Key == "managedBy" ? managedBy : pair.Value);

    public void AddJob(string key, string status, string? compute = null)
    {
        var region = Region(key);
        var name = $"job-{Arm.Lists[$"{region.WorkspaceId}/jobs"].Count}";
        Arm.Lists[$"{region.WorkspaceId}/jobs"].Add(new JsonObject
        {
            ["id"] = $"{region.WorkspaceId}/jobs/{name}", ["name"] = name,
            ["properties"] = new JsonObject { ["computeId"] = compute ?? region.ComputeId, ["status"] = status },
        });
    }

    public Task SelectAsync(string key = "eastus2") => Lifecycle.SelectReleaseAsync(Actor, key, ArtifactId, default);

    public Task StartAsync(string key = "eastus2") => Lifecycle.StartAsync(Actor, key, new StartApproval(true, false), default);

    public async Task<StudioRig> ArmedAsync(string key = "eastus2")
    {
        await SelectAsync(key);
        await StartAsync(key);
        return this;
    }

    public string Writes => string.Join("\n", Arm.Writes);
}

public sealed class StudioLifecycleTests
{
    [Fact]
    public async Task Start_arms_only_after_verified_release_and_creates_one_private_spot_cluster()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        Assert.False(rig.Gate.IsArmed("eastus2"));
        await rig.StartAsync();

        var region = rig.Region();
        Assert.True(rig.Gate.IsArmed("eastus2"));
        Assert.True(rig.Gate.IsWorkspaceArmed(region.WorkspaceId));
        Assert.False(rig.Gate.IsArmed("westeurope"));
        Assert.Equal(RegionPhase.Armed, rig.State.Regions["eastus2"].Phase);
        Assert.Equal(rig.ArtifactId, rig.State.Regions["eastus2"].ArmedArtifactId);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["westeurope"].Phase);
        Assert.Equal([$"PUT {region.ComputeId}"], rig.Arm.Writes);
        var cluster = rig.Arm.Resources[region.ComputeId]["properties"]!["properties"]!;
        Assert.Equal("LowPriority", cluster["vmPriority"]!.GetValue<string>());
        Assert.False(cluster["enableNodePublicIp"]!.GetValue<bool>());
        Assert.Equal("Disabled", cluster["remoteLoginPortPublicAccess"]!.GetValue<string>());
        Assert.Equal(0, cluster["scaleSettings"]!["minNodeCount"]!.GetValue<int>());
        Assert.Equal(region.Policy.MaxNodes, cluster["scaleSettings"]!["maxNodeCount"]!.GetValue<int>());
        Assert.Equal("PT120S", cluster["scaleSettings"]!["nodeIdleTimeBeforeScaleDown"]!.GetValue<string>());
        Assert.Contains(rig.Network.Hosts, host => host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal));
        var audit = Assert.Single(rig.Store.Audit, item => item.Key.Contains("-start-", StringComparison.Ordinal)).Value;
        Assert.Contains(StudioRig.Actor, audit);
        Assert.DoesNotContain("Bearer", audit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_without_approval_or_selected_release_changes_nothing()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        await Assert.ThrowsAsync<ValidationException>(() => rig.Lifecycle.StartAsync(StudioRig.Actor, "eastus2", new StartApproval(false, false), default));
        await Assert.ThrowsAsync<ValidationException>(() => rig.Lifecycle.StartAsync(StudioRig.Actor, "unconfigured", new StartApproval(true, false), default));
        var fresh = await StudioRig.CreateAsync();
        await Assert.ThrowsAsync<ConflictException>(() => fresh.StartAsync());
        Assert.Empty(rig.Arm.Writes);
        Assert.Empty(fresh.Arm.Writes);
        Assert.Equal(RegionPhase.Off, fresh.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Theory]
    [InlineData("price-too-high")]
    [InlineData("price-ambiguous")]
    [InlineData("sku-restricted")]
    [InlineData("quota-insufficient")]
    [InlineData("private-connectivity")]
    [InlineData("active-job")]
    [InlineData("job-unknown-compute")]
    [InlineData("foreign-compute")]
    [InlineData("subnet-foreign-nsg")]
    [InlineData("subnet-delegated")]
    [InlineData("workspace-tags")]
    public async Task Start_refuses_before_any_mutation_when_a_live_prerequisite_fails(string condition)
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        var region = rig.Region();
        var scope = $"/subscriptions/{rig.Foundation.Scope.SubscriptionId}";
        switch (condition)
        {
            case "price-too-high": rig.Prices.Price = 4.01m; break;
            case "price-ambiguous": rig.Prices.Duplicates = 2; break;
            case "sku-restricted": rig.Arm.Lists[$"{scope}/providers/Microsoft.Compute/skus"][0]["restrictions"] = new JsonArray(new JsonObject()); break;
            case "quota-insufficient": rig.Arm.Lists[$"{scope}/providers/Microsoft.MachineLearningServices/locations/eastus2/usages"][0]["limit"] = 23; break;
            case "private-connectivity": rig.Network.Reachable = false; break;
            case "active-job": rig.AddJob("eastus2", "Queued"); break;
            case "job-unknown-compute": rig.Arm.Lists[$"{region.WorkspaceId}/jobs"].Add(new JsonObject { ["id"] = $"{region.WorkspaceId}/jobs/x", ["name"] = "x", ["properties"] = new JsonObject { ["status"] = "Running" } }); break;
            case "foreign-compute": rig.SeedCompute(managedBy: "someone-else"); break;
            case "subnet-foreign-nsg": rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["networkSecurityGroup"] = new JsonObject { ["id"] = "/subscriptions/x/other" }; break;
            case "subnet-delegated": rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["delegations"] = new JsonArray(new JsonObject()); break;
            case "workspace-tags": rig.Arm.Resources[region.WorkspaceId]["tags"] = new JsonObject(); break;
        }

        await Assert.ThrowsAsync<ConflictException>(() => rig.StartAsync());
        Assert.Empty(rig.Arm.Writes);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Failed_start_releases_what_it_created_and_returns_to_off()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        rig.Arm.ProvisionFails = true;
        await Assert.ThrowsAsync<ConflictException>(() => rig.StartAsync());
        var region = rig.Region();
        Assert.Contains($"DELETE {region.ComputeId}?underlyingResourceAction=Delete", rig.Arm.Writes);
        Assert.False(rig.Arm.Resources.ContainsKey(region.ComputeId));
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Failed_start_with_unverified_release_stays_fenced_until_stop_proves_it()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        rig.Arm.ProvisionFails = true;
        rig.Arm.Fail = (method, _) => method == "DELETE";
        var failure = await Assert.ThrowsAsync<ConflictException>(() => rig.StartAsync());
        Assert.Contains("NOT verified", failure.Message);
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        var refused = await Assert.ThrowsAsync<ConflictException>(() => rig.StartAsync());
        Assert.Contains("run Stop", refused.Message, StringComparison.OrdinalIgnoreCase);

        rig.Arm.Fail = null;
        rig.Arm.ProvisionFails = false;
        var stop = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.True(stop.Verified);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
    }

    [Fact]
    public async Task Interrupted_start_is_recovered_as_unknown_and_never_armed()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        rig.SeedCompute();
        var interrupted = rig.State;
        rig.Store.Json = StudioContracts.Write(interrupted.With("eastus2", interrupted.Regions["eastus2"] with
        {
            Phase = RegionPhase.Starting, OperationId = Guid.NewGuid().ToString("D"),
        }));

        await rig.RestartAsync();
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
        Assert.Contains("restarted", rig.State.Regions["eastus2"].Detail);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        var status = await rig.Lifecycle.StatusAsync(default);
        var region = Assert.Single(status.Regions, item => item.Region == "eastus2");
        Assert.Equal("IDLE", region.Live);
        Assert.NotEmpty(region.Blockers);
        await Assert.ThrowsAsync<ConflictException>(() => rig.StartAsync());

        var stop = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.True(stop.Verified);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Arm.Resources.ContainsKey(rig.Region().ComputeId));
    }

    [Fact]
    public async Task Armed_state_survives_a_host_restart_only_while_its_live_compute_is_valid()
    {
        var rig = await (await StudioRig.CreateAsync()).ArmedAsync();
        await rig.RestartAsync();
        Assert.True(rig.Gate.IsArmed("eastus2"));

        // Emergency release (independent script) removed the compute while the host was stopped.
        rig.Arm.Resources.Remove(rig.Region().ComputeId);
        await rig.RestartAsync();
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
        Assert.Contains("not backed by a valid live compute", rig.State.Regions["eastus2"].Detail);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        Assert.True((await rig.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);

        // Drift to a compute outside the private one-node contract is fenced as well, and an unreadable one fails closed.
        var drift = await (await StudioRig.CreateAsync()).ArmedAsync();
        drift.Arm.Resources[drift.Region().ComputeId]["properties"]!["properties"]!["enableNodePublicIp"] = true;
        await drift.RestartAsync();
        Assert.Equal(RegionPhase.Unknown, drift.State.Regions["eastus2"].Phase);

        var unreadable = await (await StudioRig.CreateAsync()).ArmedAsync();
        unreadable.Arm.Fail = (method, id) => method == "GET" && id == unreadable.Region().ComputeId;
        await unreadable.RestartAsync();
        Assert.Equal(RegionPhase.Unknown, unreadable.State.Regions["eastus2"].Phase);
        Assert.False(unreadable.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Interrupted_stop_is_also_fenced_on_restart()
    {
        var rig = await (await StudioRig.CreateAsync()).ArmedAsync();
        var state = rig.State;
        rig.Store.Json = StudioContracts.Write(state.With("eastus2", state.Regions["eastus2"] with
        {
            Phase = RegionPhase.Stopping, OperationId = Guid.NewGuid().ToString("D"), ArmedArtifactId = null,
        }));
        await rig.RestartAsync();
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Stop_attempts_every_region_and_reports_failure_without_claiming_success()
    {
        var rig = await StudioRig.CreateAsync();
        rig.SeedCompute("eastus2");
        rig.SeedCompute("westeurope");
        var first = rig.Region("eastus2").ComputeId;
        rig.Arm.Fail = (method, id) => method == "DELETE" && id == first;

        var result = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(result.Verified);
        Assert.False(result.Regions.Single(item => item.Region == "eastus2").Verified);
        Assert.True(result.Regions.Single(item => item.Region == "westeurope").Verified);
        Assert.False(rig.Arm.Resources.ContainsKey(rig.Region("westeurope").ComputeId));
        Assert.True(rig.Arm.Resources.ContainsKey(first));
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["westeurope"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        Assert.Contains(rig.Store.Audit, item => item.Key.Contains("-stop-", StringComparison.Ordinal) && item.Value.Contains("not-verified", StringComparison.Ordinal));

        rig.Arm.Fail = null;
        var retry = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.True(retry.Verified);
        Assert.All(rig.State.Regions.Values, region => Assert.Equal(RegionPhase.Off, region.Phase));
    }

    [Fact]
    public async Task Repeated_stop_is_safe_and_idempotent()
    {
        var rig = await (await StudioRig.CreateAsync()).ArmedAsync();
        Assert.True((await rig.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        var writes = rig.Arm.Writes.Count;
        Assert.True((await rig.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        Assert.Equal(writes, rig.Arm.Writes.Count);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        Assert.All(rig.State.Regions.Values, region => Assert.Equal(RegionPhase.Off, region.Phase));
    }

    [Fact]
    public async Task Stop_never_deletes_or_adopts_a_foreign_compute()
    {
        var rig = await StudioRig.CreateAsync();
        rig.SeedCompute(managedBy: "terraform");
        var result = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(result.Verified);
        Assert.Contains("Ownership", result.Regions.Single(item => item.Region == "eastus2").Error);
        Assert.DoesNotContain(rig.Arm.Writes, write => write.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.True(rig.Arm.Resources.ContainsKey(rig.Region().ComputeId));
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);
    }

    [Fact]
    public async Task Cancellation_errors_are_warnings_only_after_fresh_reads_prove_every_job_terminal()
    {
        var rig = await StudioRig.CreateAsync();
        rig.SeedCompute();
        rig.AddJob("eastus2", "Running");
        rig.Arm.CancelFails = true;
        var verified = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.True(verified.Regions.Single(item => item.Region == "eastus2").Verified);
        Assert.Contains("Fresh reads", Assert.Single(verified.Regions.Single(item => item.Region == "eastus2").Warnings));

        var stuck = await StudioRig.CreateAsync();
        stuck.SeedCompute();
        stuck.AddJob("eastus2", "Running");
        stuck.Arm.CancelFails = true;
        stuck.Arm.JobsStayActive = true;
        var failed = await stuck.Lifecycle.StopAsync(StudioRig.Actor);
        var region = failed.Regions.Single(item => item.Region == "eastus2");
        Assert.False(region.Verified);
        Assert.Contains("terminal", region.Error);
        Assert.Contains("Cancel job-0 failed", region.Error);
        Assert.Equal(RegionPhase.Unknown, stuck.State.Regions["eastus2"].Phase);
    }

    [Fact]
    public async Task Unavailable_control_store_fails_closed_but_stop_still_releases_azure()
    {
        var clock = new ReliabilityTimeProvider();
        var rig = await (await StudioRig.CreateAsync(time: clock)).ArmedAsync();
        Assert.True(rig.Gate.IsArmed("eastus2"));
        rig.Store.Down = true;
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(rig.Gate.IsArmed("eastus2"));
        await Assert.ThrowsAsync<IOException>(() => rig.Lifecycle.StatusAsync(default));

        var result = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(result.Verified);
        Assert.Contains("durable state", result.Regions.Single(item => item.Region == "eastus2").Error);
        Assert.False(rig.Arm.Resources.ContainsKey(rig.Region().ComputeId));

        rig.Store.Down = false;
        await rig.Lifecycle.StatusAsync(default);
        Assert.False(rig.Gate.IsArmed("eastus2"));
        await rig.Lifecycle.RefreshAsync(default);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Gate_is_fenced_immediately_by_stop_and_by_a_missing_or_stale_snapshot()
    {
        var clock = new ReliabilityTimeProvider();
        var rig = await (await StudioRig.CreateAsync(time: clock)).ArmedAsync();
        Assert.True(rig.Gate.IsArmed("eastus2"));
        rig.Gate.Fence();
        Assert.False(rig.Gate.IsArmed("eastus2"));
        await rig.Lifecycle.RefreshAsync(default);
        Assert.True(rig.Gate.IsArmed("eastus2"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(rig.Gate.IsArmed("eastus2"));
    }

    [Fact]
    public async Task Release_selection_is_read_only_verified_and_locked_while_armed()
    {
        var rig = await StudioRig.CreateAsync();
        var region = rig.Region();
        await Assert.ThrowsAsync<ValidationException>(() => rig.Lifecycle.SelectReleaseAsync(StudioRig.Actor, "eastus2", "not-a-hash", default));
        await Assert.ThrowsAsync<ConflictException>(() => rig.Lifecycle.SelectReleaseAsync(StudioRig.Actor, "eastus2", new string('a', 64), default));

        var environment = rig.Arm.Resources.Single(pair => pair.Key.Contains("/environments/", StringComparison.Ordinal) && pair.Key.StartsWith(region.WorkspaceId, StringComparison.Ordinal));
        var image = environment.Value["properties"]!["image"]!.GetValue<string>();
        environment.Value["properties"]!["image"] = image.Replace("@sha256:", "@sha256:0", StringComparison.Ordinal);
        await Assert.ThrowsAsync<ConflictException>(() => rig.SelectAsync());
        environment.Value["properties"]!["image"] = image;

        var model = rig.Arm.Resources.Single(pair => pair.Key.Contains("/models/", StringComparison.Ordinal) && pair.Key.StartsWith(region.WorkspaceId, StringComparison.Ordinal));
        model.Value["properties"]!["modelUri"] = "azureml://datastores/wan_release/paths/models/other/";
        await Assert.ThrowsAsync<ConflictException>(() => rig.SelectAsync());
        model.Value["properties"]!["modelUri"] = $"azureml://subscriptions/{rig.Foundation.Scope.SubscriptionId}/resourcegroups/rg/workspaces/ws/datastores/{region.ReleaseDatastore}/paths/models/{new string('d', 64)}/";

        rig.Store.Release[("eastus2", $"releases/{rig.ArtifactId}/{StudioContracts.RegionFingerprint(rig.Foundation, "eastus2")}.json")] =
            rig.Store.Release[("westeurope", $"releases/{rig.ArtifactId}/{StudioContracts.RegionFingerprint(rig.Foundation, "westeurope")}.json")];
        await Assert.ThrowsAsync<ConflictException>(() => rig.SelectAsync());
        await Assert.ThrowsAsync<ConflictException>(async () =>
        {
            rig.Store.Release.Remove(("eastus2", $"builds/{rig.ArtifactId}/manifest.json"));
            await rig.SelectAsync();
        });
        Assert.Empty(rig.Arm.Writes);

        var armed = await (await StudioRig.CreateAsync()).ArmedAsync();
        await Assert.ThrowsAsync<ConflictException>(() => armed.SelectAsync());
    }

    [Fact]
    public async Task Managed_egress_is_owned_attached_only_to_the_dedicated_subnet_and_released_with_the_etag()
    {
        var rig = await StudioRig.CreateAsync(managedEgress: true);
        var region = rig.Region();
        await rig.SelectAsync();
        await rig.StartAsync();

        Assert.True(rig.Arm.Resources.ContainsKey(region.PublicIpId));
        Assert.True(rig.Arm.Resources.ContainsKey(region.NatGatewayId));
        var subnet = rig.Arm.Resources[region.LandingZone.GpuSubnetId];
        Assert.Equal(region.NatGatewayId, subnet["properties"]!["natGateway"]!["id"]!.GetValue<string>());
        Assert.Equal("preserve-route", subnet["properties"]!["routeTable"]!["id"]!.GetValue<string>());
        Assert.Equal("etag-0", rig.Arm.IfMatches[rig.Arm.Writes.FindIndex(write => write == $"PUT {region.LandingZone.GpuSubnetId}")]);

        Assert.True((await rig.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        Assert.Null(rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["natGateway"]);
        Assert.Equal("preserve-route", rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["routeTable"]!["id"]!.GetValue<string>());
        Assert.Single(rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["serviceEndpoints"]!.AsArray());
        Assert.False(rig.Arm.Resources.ContainsKey(region.NatGatewayId));
        Assert.False(rig.Arm.Resources.ContainsKey(region.PublicIpId));
        Assert.True(rig.Arm.Writes.FindIndex(write => write.StartsWith("DELETE " + region.ComputeId, StringComparison.Ordinal)) <
            rig.Arm.Writes.FindIndex(write => write == $"DELETE {region.NatGatewayId}"));
    }

    [Fact]
    public async Task Stop_never_detaches_or_deletes_a_foreign_nat()
    {
        var rig = await StudioRig.CreateAsync(managedEgress: true);
        var region = rig.Region();
        rig.Arm.Resources[region.LandingZone.GpuSubnetId]["properties"]!["natGateway"] = new JsonObject { ["id"] = region.NatGatewayId + "-customer" };
        var result = await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(result.Verified);
        Assert.Empty(rig.Arm.Writes);
        Assert.Equal(RegionPhase.Unknown, rig.State.Regions["eastus2"].Phase);

        var owned = await StudioRig.CreateAsync(managedEgress: true);
        owned.Arm.Resources[region.PublicIpId] = StudioRig.Resource(region.PublicIpId, StudioRig.StudioPolicyTags(region, "customer"));
        Assert.False((await owned.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        Assert.DoesNotContain(owned.Arm.Writes, write => write.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unmanaged_egress_is_neither_read_nor_assumed_by_the_host()
    {
        var rig = await StudioRig.CreateAsync();
        var region = rig.Region();
        rig.Arm.Fail = (method, id) => method == "GET" && (id == region.NatGatewayId || id == region.PublicIpId);
        Assert.True((await rig.Lifecycle.StopAsync(StudioRig.Actor)).Verified);
        Assert.Equal("OFF", (await rig.Lifecycle.StatusAsync(default)).Regions.Single(item => item.Region == "eastus2").Live);
    }

    [Fact]
    public async Task Status_reports_unreadable_live_state_instead_of_off()
    {
        var rig = await StudioRig.CreateAsync();
        rig.Arm.Fail = (method, id) => method == "GET" && id == rig.Region().ComputeId;
        var status = await rig.Lifecycle.StatusAsync(default);
        var region = status.Regions.Single(item => item.Region == "eastus2");
        Assert.Equal("UNREADABLE", region.Live);
        Assert.NotNull(region.Error);
        Assert.Equal("OFF", status.Regions.Single(item => item.Region == "westeurope").Live);
    }

    [Fact]
    public async Task Concurrent_start_is_serialized_per_region_and_stop_cancels_an_in_flight_start()
    {
        var rig = await StudioRig.CreateAsync();
        await rig.SelectAsync();
        var gate = new TaskCompletionSource();
        rig.Arm.Fail = null;
        var blocking = new BlockingArm(rig.Arm, gate.Task);
        var lifecycle = new StudioLifecycle(rig.Foundation, blocking, rig.Store, rig.Store, rig.Prices, rig.Network, rig.Gate, TimeProvider.System,
            new StudioWaits(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60)),
            NullLogger<StudioLifecycle>.Instance);
        var first = lifecycle.StartAsync(StudioRig.Actor, "eastus2", new StartApproval(true, false), default);
        await blocking.Entered.Task;
        await Assert.ThrowsAsync<ConflictException>(() => lifecycle.StartAsync(StudioRig.Actor, "eastus2", new StartApproval(true, false), default));
        Assert.Equal(RegionPhase.Starting, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Gate.IsArmed("eastus2"));

        var stop = lifecycle.StopAsync(StudioRig.Actor);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        gate.SetResult();
        Assert.True((await stop).Verified);
        Assert.Equal(RegionPhase.Off, rig.State.Regions["eastus2"].Phase);
        Assert.False(rig.Arm.Resources.ContainsKey(rig.Region().ComputeId));
    }

    private sealed class BlockingArm(SimArm inner, Task release) : IStudioArm
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonNode?> GetAsync(string id, string api, CancellationToken ct)
        {
            if (id.EndsWith("/computes/wan-gpu", StringComparison.Ordinal) && !Entered.Task.IsCompleted)
            {
                Entered.SetResult();
                await Task.WhenAny(release, Task.Delay(Timeout.Infinite, ct));
                ct.ThrowIfCancellationRequested();
            }

            return await inner.GetAsync(id, api, ct);
        }

        public Task<JsonNode?> SendAsync(string method, string id, string api, JsonNode? body, string? ifMatch, CancellationToken ct) => inner.SendAsync(method, id, api, body, ifMatch, ct);

        public Task<IReadOnlyList<JsonNode>> ListAsync(string id, string api, CancellationToken ct) => inner.ListAsync(id, api, ct);
    }

    [Fact]
    public async Task State_contract_rejects_foreign_scope_regions_phases_and_incomplete_armed_records()
    {
        var rig = await StudioRig.CreateAsync();
        var good = JsonNode.Parse(rig.Store.Json!)!;
        void Rejects(Action<JsonNode> tamper)
        {
            var copy = good.DeepClone();
            tamper(copy);
            Assert.Throws<ValidationException>(() => StudioContracts.ReadState(copy.ToJsonString(), rig.Foundation));
        }

        Rejects(node => node["scopeFingerprint"] = new string('0', 64));
        Rejects(node => node["schemaVersion"] = 2);
        Rejects(node => node["version"] = 0);
        Rejects(node => node["regions"]!.AsObject().Remove("westeurope"));
        Rejects(node => node["regions"]!["extra"] = node["regions"]!["eastus2"]!.DeepClone());
        Rejects(node => node["regions"]!["eastus2"]!["phase"] = "armed");
        Rejects(node => node["regions"]!["eastus2"]!["phase"] = "running");
        Rejects(node => node["regions"]!["eastus2"]!["phase"] = 1);
        Rejects(node => node["regions"]!["eastus2"]!["operationId"] = "not-a-guid");
        Rejects(node => node["regions"]!["eastus2"]!["selectedArtifactId"] = "short");
        Rejects(node => node["regions"]!["eastus2"]!["unexpected"] = true);
        Assert.Throws<ValidationException>(() => StudioContracts.ReadState(rig.Store.Json!.Replace("\"version\":1", "\"version\":1,\"version\":2"), rig.Foundation));
    }

    [Fact]
    public async Task Hosted_gate_ignores_the_legacy_armed_setting_and_follows_only_the_durable_fence()
    {
        var rig = await (await StudioRig.CreateAsync()).ArmedAsync();
        var workspace = rig.Region().WorkspaceId;
        var release = AzureMlSamples.Release();
        var monitor = new FixedMonitor(armed: true);
        var hosted = new VideoSafetyStudio.Infrastructure.AzureMl.AzureMlGate(monitor, rig.Gate, workspace);
        Assert.True(hosted.IsArmed(release));
        Assert.False(hosted.IsArmed(null));
        Assert.False(new VideoSafetyStudio.Infrastructure.AzureMl.AzureMlGate(monitor, rig.Gate, rig.Region("westeurope").WorkspaceId).IsArmed(release));
        Assert.False(new VideoSafetyStudio.Infrastructure.AzureMl.AzureMlGate(monitor, rig.Gate, "/subscriptions/x/unknown").IsArmed(release));

        await rig.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(hosted.IsArmed(release));
        Assert.False(new VideoSafetyStudio.Infrastructure.AzureMl.AzureMlGate(new FixedMonitor(armed: true), rig.Gate, workspace).IsArmed(release));
    }

    private sealed class FixedMonitor(bool armed) : Microsoft.Extensions.Options.IOptionsMonitor<VideoSafetyStudio.Infrastructure.AzureMl.AzureMlOptions>
    {
        public VideoSafetyStudio.Infrastructure.AzureMl.AzureMlOptions CurrentValue => new() { Armed = armed };

        public VideoSafetyStudio.Infrastructure.AzureMl.AzureMlOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<VideoSafetyStudio.Infrastructure.AzureMl.AzureMlOptions, string?> listener) => null;
    }
}
