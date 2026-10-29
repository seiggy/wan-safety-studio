using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Domain;
using static VideoSafetyStudio.Application.Operations.StudioPolicy;

namespace VideoSafetyStudio.Application.Operations;

public sealed record StudioWaits(TimeSpan Poll, TimeSpan Compute, TimeSpan Jobs, TimeSpan Network)
{
    public static StudioWaits Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
}

public sealed record StartApproval(bool GpuSpend, bool CustomerNsgRules);

public sealed record RegionStatus(
    string Region, RegionPhase Phase, string? SelectedArtifactId, string? ArmedArtifactId, string? Detail, string Live, int? Nodes,
    string[] ActiveJobs, bool NatPresent, bool PublicIpPresent, string[] Blockers, string? Error);

public sealed record StudioStatus(long Version, RegionStatus[] Regions);

public sealed record RegionStopResult(string Region, bool Verified, string? Error, string[] Warnings);

public sealed record StopResult(bool Verified, RegionStopResult[] Regions);

/// <summary>
/// Hosted Start/Stop/status/release selection over native Azure APIs. Every mutation is preceded by a durable fence and
/// validated against the cached scope; a failure never reports success without fresh proof of release.
/// </summary>
public sealed partial class StudioLifecycle(
    StudioFoundation foundation, IStudioArm arm, IStudioControlStore store, IStudioReleaseReader releases, IStudioPrices prices,
    IStudioConnectivity network, StudioArmedGate gate, TimeProvider time, StudioWaits waits, ILogger<StudioLifecycle> logger)
{
    private static readonly ActivitySource Source = new("VideoSafetyStudio.Operations");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new(StringComparer.Ordinal);

    // Regions whose release was verified (or failed) but whose durable state could not be written. Until it is, the stale
    // durable "armed" is never trusted for submissions.
    private readonly ConcurrentDictionary<string, RegionPhase> unrecorded = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^azureml://subscriptions/[^/]+/resource[gG]roups/[^/]+/workspaces/[^/]+/datastores/")]
    private static partial Regex FullDatastore();

    private RegionalFoundation Region(string key) =>
        foundation.Regions.TryGetValue(key ?? "", out var region) ? region : throw new ValidationException("Unknown region.");

    private SemaphoreSlim Lock(string key) => locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private DateTimeOffset Now => time.GetUtcNow();

    // ---- durable state -------------------------------------------------------------------------------------------------

    private async Task<(StudioOperationalState State, string ETag)> LoadAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await store.ReadStateAsync(ct) is { } stored)
            {
                var state = StudioContracts.ReadState(stored.Json, foundation);
                gate.Update(Mask(state));
                return (state, stored.ETag);
            }

            try
            {
                await store.WriteStateAsync(StudioContracts.Write(StudioOperationalState.Initial(foundation, Now)), null, ct);
            }
            catch (StudioStateConflictException)
            {
                // Another instance initialized it; read that one.
            }
        }

        throw new ConflictException("Operational state could not be initialized.");
    }

    private async Task<StudioOperationalState> TransitionAsync(string region, Func<RegionOperation, RegionOperation> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (state, etag) = await LoadAsync(ct);
            var next = state.With(region, change(state.Regions[region]));
            var json = StudioContracts.Write(next);
            _ = StudioContracts.ReadState(json, foundation); // never persist a state this host would later reject
            try
            {
                await store.WriteStateAsync(json, etag, ct);
            }
            catch (StudioStateConflictException)
            {
                continue;
            }

            gate.Update(Mask(next));
            return next;
        }

        throw new ConflictException("Operational state changed concurrently; retry.");
    }

    private StudioOperationalState Mask(StudioOperationalState state) => unrecorded.IsEmpty ? state : state with
    {
        Regions = state.Regions.ToDictionary(pair => pair.Key, pair => unrecorded.ContainsKey(pair.Key) && pair.Value.Phase == RegionPhase.Armed
            ? pair.Value with { Phase = RegionPhase.Unknown } : pair.Value, StringComparer.Ordinal),
    };

    /// <summary>Re-reads the durable fence and retries any release result that could not be recorded.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        foreach (var (key, target) in unrecorded.ToArray())
        {
            await TransitionAsync(key, current => current with
            {
                Phase = target, OperationId = "", ArmedArtifactId = null, UpdatedAt = Now,
                Detail = target == RegionPhase.Off ? null : "Release was not recorded durably; run Stop to prove it.",
            }, ct);
            unrecorded.TryRemove(key, out _);
        }
    }

    /// <summary>At host start: an interrupted Start/Stop is not trusted, so the region stays fenced as Unknown until Stop proves release.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        gate.Bind(foundation);
        var (state, _) = await LoadAsync(ct);
        foreach (var (region, operation) in state.Regions.Where(pair => pair.Value.Phase is RegionPhase.Starting or RegionPhase.Stopping))
        {
            await TransitionAsync(region, current => current.Phase != operation.Phase || current.OperationId != operation.OperationId ? current
                : current with { Phase = RegionPhase.Unknown, UpdatedAt = Now, Detail = $"The host restarted during {operation.Phase}; run Stop to prove release." }, ct);
            logger.LogWarning("Region {Region} was {Phase} at host restart; submissions remain fenced until Stop verifies release.", region, operation.Phase);
        }

        // An armed record is only trusted while its compute is live and still satisfies the contract: an emergency release,
        // manual deletion or drift while the host was down must not leave submissions open.
        var (fresh, _) = await LoadAsync(ct);
        foreach (var (region, operation) in fresh.Regions.Where(pair => pair.Value.Phase == RegionPhase.Armed))
        {
            string? problem = null;
            try
            {
                Cluster(await arm.GetAsync(foundation.Regions[region].ComputeId, MlApi, ct), foundation.Regions[region]);
            }
            catch (ConflictException error)
            {
                problem = error.Message;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                problem = "Live compute could not be read.";
                logger.LogWarning(error, "Armed region {Region} could not be verified at host restart.", region);
            }

            if (problem is null) continue;
            await TransitionAsync(region, current => current.Phase != RegionPhase.Armed || current.OperationId != operation.OperationId ? current
                : current with { Phase = RegionPhase.Unknown, ArmedArtifactId = null, UpdatedAt = Now, Detail = $"Armed state is not backed by a valid live compute: {problem} Run Stop to prove release." }, ct);
            logger.LogWarning("Region {Region} was armed but its live compute failed verification; it is fenced.", region);
        }
    }

    // ---- status --------------------------------------------------------------------------------------------------------

    public async Task<StudioStatus> StatusAsync(CancellationToken ct)
    {
        var (state, _) = await LoadAsync(ct);
        var regions = await Task.WhenAll(foundation.Regions.Keys.Order(StringComparer.Ordinal).Select(key => ReadRegionAsync(key, state.Regions[key], ct)));
        return new StudioStatus(state.Version, regions);
    }

    private async Task<RegionStatus> ReadRegionAsync(string key, RegionOperation operation, CancellationToken ct)
    {
        var region = foundation.Regions[key];
        var blockers = new List<string>();
        if (operation.SelectedArtifactId is null) blockers.Add("No approved release is selected.");
        if (operation.Phase != RegionPhase.Off && operation.Phase != RegionPhase.Armed) blockers.Add($"Region is {operation.Phase}; run Stop and verify release.");
        try
        {
            var workspace = await arm.GetAsync(region.WorkspaceId, MlApi, ct);
            var compute = await arm.GetAsync(region.ComputeId, MlApi, ct);
            var nat = region.Policy.ManageComputeEgress ? await arm.GetAsync(region.NatGatewayId, NetworkApi, ct) : null; // the host neither reads nor assumes egress it does not manage
            var pip = region.Policy.ManageComputeEgress ? await arm.GetAsync(region.PublicIpId, NetworkApi, ct) : null;
            var active = workspace is null ? [] : OnCompute(await arm.ListAsync($"{region.WorkspaceId}/jobs", MlApi, ct), foundation, region)
                .Where(job => !Terminal(job)).Select(job => $"{Text(job["name"])}:{Text(job["properties"]?["status"])}").ToArray();
            var live = "OFF";
            int? nodes = null;
            if (compute is not null)
            {
                nodes = NodeCount(compute);
                live = nodes is null || Text(compute["properties"]?["provisioningState"]) != "Succeeded" ? "PARTIAL" : nodes > 0 ? "RUNNING" : "IDLE";
            }
            else if (nat is not null || pip is not null || active.Length > 0)
            {
                live = "PARTIAL";
            }

            return new RegionStatus(key, operation.Phase, operation.SelectedArtifactId, operation.ArmedArtifactId, operation.Detail, live, nodes, active,
                nat is not null, pip is not null, [.. blockers], null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Live status for {Region} is unreadable.", key);
            return new RegionStatus(key, operation.Phase, operation.SelectedArtifactId, operation.ArmedArtifactId, operation.Detail, "UNREADABLE", null, [], false, false,
                [.. blockers, "Live Azure state could not be read."], $"Live Azure state could not be read; the region is not shown as off. {ReadFailure(error)}");
        }
    }

    /// <summary>Only the status Azure returned and the exception type: provider error text can carry identifiers, so it stays in the host log.</summary>
    private static string ReadFailure(Exception error) => error is HttpRequestException { StatusCode: { } code }
        ? $"Cause: Azure answered HTTP {(int)code} to a read of the workspace, compute or jobs ({(code is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized ? "the signed-in or host identity lacks read access" : "see the host log")})."
        : $"Cause: {error.GetType().Name} (see the host log).";

    private static int? NodeCount(JsonNode compute)
    {
        if (compute["properties"]?["properties"]?["nodeStateCounts"] is not JsonObject counts) return null;
        var total = 0;
        foreach (var value in counts.Select(pair => Long(pair.Value)))
        {
            if (value is not >= 0) return null;
            total += (int)value;
        }

        return total;
    }

    // ---- release selection (read/verify only) ----------------------------------------------------------------------------

    public async Task SelectReleaseAsync(string actor, string key, string artifactId, CancellationToken ct)
    {
        var region = Region(key);
        if (artifactId is null || !Sha256Rule().IsMatch(artifactId)) throw new ValidationException("Release artifact identity must be a 64-character SHA-256 value.");
        using var activity = Source.StartActivity("studio.release.select");
        activity?.SetTag("studio.region", key);
        await Lock(key).WaitAsync(ct);
        try
        {
            await VerifyReleaseAsync(key, region, artifactId, ct);
            await TransitionAsync(key, current => current.Phase == RegionPhase.Off
                ? current with { SelectedArtifactId = artifactId, UpdatedAt = Now }
                : throw new ConflictException($"Region {key} is {current.Phase}; Stop and verify release before changing the approved release."), ct);
            await AuditAsync("release.select", actor, [key], "ok", null, ct);
        }
        finally
        {
            Lock(key).Release();
        }
    }

    [GeneratedRegex(@"^[a-f0-9]{64}\z")]
    private static partial Regex Sha256Rule();

    private async Task<(GpuBuildManifest Build, RegionalRelease Binding)> VerifyReleaseAsync(string key, RegionalFoundation region, string artifactId, CancellationToken ct)
    {
        var buildJson = await releases.ReadAsync(key, $"builds/{artifactId}/manifest.json", ct) ?? throw new ConflictException($"Release {artifactId[..12]} has not been promoted to {key}.");
        var build = StudioContracts.ReadBuild(buildJson);
        Require(build.ArtifactId == artifactId, "The release manifest does not match its requested identity.");
        var bindingJson = await releases.ReadAsync(key, $"releases/{artifactId}/{StudioContracts.RegionFingerprint(foundation, key)}.json", ct)
            ?? throw new ConflictException($"Release {artifactId[..12]} has no verified binding for {key}.");
        var binding = StudioContracts.ReadRelease(bindingJson, foundation, build);
        Require(binding.Region == key, "The regional binding belongs to a different region.");

        var environment = await arm.GetAsync(binding.EnvironmentId, MlApi, ct);
        Require(environment is not null && Same(Text(environment["id"]), binding.EnvironmentId) &&
            Text(environment["properties"]?["image"]) == binding.Image && environment["properties"]?["isArchived"]?.GetValueKind() != System.Text.Json.JsonValueKind.True,
            "The Azure ML environment differs from the approved regional binding.");
        var parts = binding.ModelsRef.Split(':');
        var model = await arm.GetAsync($"{region.WorkspaceId}/models/{parts[1]}/versions/{parts[2]}", MlApi, ct);
        var modelUri = FullDatastore().Replace(Text(model?["properties"]?["modelUri"]) ?? "", "azureml://datastores/").TrimEnd('/');
        Require(model is not null && modelUri == binding.ModelsUri.TrimEnd('/'), "The Azure ML model differs from the approved regional binding.");
        return (build, binding);
    }

    // ---- start ---------------------------------------------------------------------------------------------------------

    public async Task StartAsync(string actor, string key, StartApproval approval, CancellationToken ct)
    {
        var region = Region(key);
        if (!approval.GpuSpend) throw new ValidationException("Start requires explicit approval of variable Spot GPU spend (one node per job).");
        if (region.LandingZone.ExistingGpuNsgId is not null && !approval.CustomerNsgRules)
            throw new ValidationException("The customer-managed NSG must be reviewed by the security owner; approve that acknowledgment explicitly.");
        using var activity = Source.StartActivity("studio.start");
        activity?.SetTag("studio.region", key);
        if (!await Lock(key).WaitAsync(0, CancellationToken.None)) throw new ConflictException($"A lifecycle operation is already running for {key}.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        running[key] = cancellation;
        var operationId = Guid.NewGuid().ToString("D");
        var mutated = false;
        string? artifact = null;
        try
        {
            await TransitionAsync(key, current =>
            {
                if (current.Phase != RegionPhase.Off) throw new ConflictException($"Region {key} is {current.Phase}; run Stop and verify release before Start.");
                artifact = current.SelectedArtifactId ?? throw new ConflictException("Select an approved release before Start.");
                return current with { Phase = RegionPhase.Starting, OperationId = operationId, ArmedArtifactId = null, UpdatedAt = Now, Detail = null };
            }, cancellation.Token);
        }
        catch
        {
            running.TryRemove(key, out _);
            Lock(key).Release();
            throw;
        }

        try
        {
            var token = cancellation.Token;
            await VerifyReleaseAsync(key, region, artifact!, token);
            Owned(await arm.GetAsync(region.WorkspaceId, MlApi, token), region.WorkspaceId, region.OwnershipTags, ephemeral: false);
            var subnet = await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, token);
            Subnet(subnet, region);
            SubnetDefaults(subnet!);
            foreach (var host in PrivateHosts(region)) await network.AssertPrivateAsync(host, token);
            Require(!OnCompute(await arm.ListAsync($"{region.WorkspaceId}/jobs", MlApi, token), foundation, region).Any(job => !Terminal(job)),
                "Queued or running jobs could resume on Start. Stop and verify release first.");
            await AssertLiveCostAsync(key, region, token);
            mutated = true;
            if (region.Policy.ManageComputeEgress) await EnsureEgressAsync(key, region, token);
            await EnsureComputeAsync(key, region, token);
            await TransitionAsync(key, current => current.Phase == RegionPhase.Starting && current.OperationId == operationId
                ? current with { Phase = RegionPhase.Armed, ArmedArtifactId = artifact, UpdatedAt = Now, Detail = null }
                : throw new ConflictException("Start was superseded; the region is being released."), token);
            await AuditAsync("start", actor, [key], "armed", new { artifact, approvals = new { gpuSpend = true, customerNsg = approval.CustomerNsgRules } }, CancellationToken.None);
        }
        catch (Exception error)
        {
            gate.Fence();
            var released = true;
            if (mutated)
            {
                try
                {
                    await ReleaseAsync(key, [], CancellationToken.None);
                }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    released = false;
                    logger.LogError(cleanup, "Release after failed Start did not verify for {Region}.", key);
                }
            }

            await SettleAsync(key, operationId, released, error is ConflictException or ValidationException ? error.Message : "Start failed; see the server log.");
            await AuditAsync("start", actor, [key], released ? "failed-released" : "failed-unverified", null, CancellationToken.None);
            if (!released) throw new ConflictException($"Start failed and release is NOT verified; {key} stays fenced. Run Stop. {(error is ConflictException ? error.Message : "")}".Trim());
            throw;
        }
        finally
        {
            running.TryRemove(key, out _);
            Lock(key).Release();
        }
    }

    private async Task SettleAsync(string key, string operationId, bool released, string detail)
    {
        try
        {
            await TransitionAsync(key, current => current.OperationId != operationId ? current : current with
            {
                Phase = released ? RegionPhase.Off : RegionPhase.Unknown, UpdatedAt = Now, ArmedArtifactId = null,
                Detail = detail.Length > 400 ? detail[..400] : detail,
            }, CancellationToken.None);
            unrecorded.TryRemove(key, out _);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            unrecorded[key] = released ? RegionPhase.Off : RegionPhase.Unknown;
            logger.LogError(error, "Durable state for {Region} could not be settled; submissions remain fenced in this host.", key);
        }
    }

    private async Task AssertLiveCostAsync(string key, RegionalFoundation region, CancellationToken ct)
    {
        var price = PaygRate(await prices.ListAsync(region.Policy.VmSize, key, ct), region, key);
        var scope = $"/subscriptions/{foundation.Scope.SubscriptionId}";
        Sku(await arm.ListAsync($"{scope}/providers/Microsoft.Compute/skus?$filter={Uri.EscapeDataString($"location eq '{key}'")}", SkuApi, ct), region);
        var allocated = 0;
        if (await arm.GetAsync(region.ComputeId, MlApi, ct) is { } compute)
        {
            Cluster(compute, region);
            allocated = (NodeCount(compute) ?? throw new ConflictException("Cannot establish the current node allocation.")) * CoresPerNode;
        }

        Quota(await arm.ListAsync($"{scope}/providers/Microsoft.MachineLearningServices/locations/{key}/usages", MlApi, ct), allocated);
        logger.LogInformation("Live Linux pay-as-you-go check {Price} USD/node-hour is within the approved threshold for {Region}; this is not a Spot bid, billing cap or capacity guarantee.", price, key);
    }

    private async Task EnsureEgressAsync(string key, RegionalFoundation region, CancellationToken ct)
    {
        await EnsureAsync(region.PublicIpId, NetworkApi, PublicIpBody(region, key), region, ct);
        await EnsureAsync(region.NatGatewayId, NetworkApi, NatBody(region, key), region, ct);
        var subnet = await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, ct);
        Subnet(subnet, region);
        if (subnet!["properties"]?["natGateway"] is null)
        {
            await arm.SendAsync("PUT", region.LandingZone.GpuSubnetId, NetworkApi, SubnetWrite(subnet, region.NatGatewayId),
                Text(subnet["etag"]) ?? throw new ConflictException("The subnet has no ETag; refusing an unconditional write."), ct);
        }

        await WaitAsync(async () => Same(Text((await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, ct))?["properties"]?["natGateway"]?["id"]), region.NatGatewayId),
            waits.Network, "NAT association was not verified.");
    }

    private async Task EnsureAsync(string id, string api, JsonObject body, RegionalFoundation region, CancellationToken ct)
    {
        var existing = await arm.GetAsync(id, api, ct);
        if (existing is null) await arm.SendAsync("PUT", id, api, body, null, ct);
        else Owned(existing, id, region.OwnershipTags, ephemeral: true);
        await WaitAsync(async () =>
        {
            var current = await arm.GetAsync(id, api, ct) ?? throw new ConflictException("A created resource disappeared before it became ready.");
            Owned(current, id, region.OwnershipTags, ephemeral: true);
            var state = Text(current["properties"]?["provisioningState"]);
            if (state is "Failed" or "Canceled") Refuse("A created resource failed to provision.");
            return state == "Succeeded";
        }, waits.Network, "A created resource did not become ready.");
    }

    private async Task EnsureComputeAsync(string key, RegionalFoundation region, CancellationToken ct)
    {
        var existing = await arm.GetAsync(region.ComputeId, MlApi, ct);
        if (existing is null) await arm.SendAsync("PUT", region.ComputeId, MlApi, ComputeBody(region, key), null, ct);
        await WaitAsync(async () =>
        {
            var current = await arm.GetAsync(region.ComputeId, MlApi, ct) ?? throw new ConflictException("Compute disappeared while it was provisioning.");
            Owned(current, region.ComputeId, region.OwnershipTags, ephemeral: true);
            var state = Text(current["properties"]?["provisioningState"]);
            if (state is "Failed" or "Canceled") Refuse("Compute failed to provision.");
            return state == "Succeeded";
        }, waits.Compute, "Compute did not become ready.");
        var compute = await arm.GetAsync(region.ComputeId, MlApi, ct);
        Cluster(compute, region);
        if (region.Policy.ManageComputeEgress)
        {
            Egress(await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, ct), await arm.GetAsync(region.NatGatewayId, NetworkApi, ct),
                await arm.GetAsync(region.PublicIpId, NetworkApi, ct), region);
        }
    }

    // ---- stop / release --------------------------------------------------------------------------------------------------

    /// <summary>Releases every configured region even when another fails. Success is claimed only with fresh proof for all of them.</summary>
    public async Task<StopResult> StopAsync(string actor)
    {
        using var activity = Source.StartActivity("studio.stop");
        gate.Fence();
        var results = new List<RegionStopResult>();
        foreach (var key in foundation.Regions.Keys.Order(StringComparer.Ordinal))
        {
            if (running.TryGetValue(key, out var start)) start.Cancel();
            await Lock(key).WaitAsync(CancellationToken.None);
            var warnings = new List<string>();
            var operationId = Guid.NewGuid().ToString("D");
            try
            {
                try
                {
                    await TransitionAsync(key, current => current with
                    {
                        Phase = RegionPhase.Stopping, OperationId = operationId, ArmedArtifactId = null, UpdatedAt = Now, Detail = null,
                    }, CancellationToken.None);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    warnings.Add("The durable fence could not be written; this host remains fenced and Azure release continues.");
                    logger.LogWarning(error, "Durable fence for {Region} was unavailable during Stop.", key);
                }

                await ReleaseAsync(key, warnings, CancellationToken.None);
                string? error2 = null;
                try
                {
                    await TransitionAsync(key, current => current with { Phase = RegionPhase.Off, OperationId = "", ArmedArtifactId = null, UpdatedAt = Now, Detail = null }, CancellationToken.None);
                    unrecorded.TryRemove(key, out _);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    unrecorded[key] = RegionPhase.Off;
                    error2 = "Azure release was verified, but the durable state could not be updated; submissions stay fenced.";
                    logger.LogError(error, "Durable Off state for {Region} was not written.", key);
                }

                results.Add(new RegionStopResult(key, error2 is null, error2, [.. warnings]));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await SettleAsync(key, operationId, released: false, error is ConflictException ? error.Message : "Release is not verified; see the server log.");
                logger.LogError(error, "Release for {Region} is not verified.", key);
                results.Add(new RegionStopResult(key, false, error is ConflictException ? error.Message : "Release is not verified; see the server log.", [.. warnings]));
            }
            finally
            {
                Lock(key).Release();
            }
        }

        var result = new StopResult(results.All(item => item.Verified), [.. results]);
        await AuditAsync("stop", actor, [.. results.Select(item => item.Region)], result.Verified ? "verified-off" : "not-verified", null, CancellationToken.None);
        return result;
    }

    private async Task ReleaseAsync(string key, List<string> warnings, CancellationToken ct)
    {
        var region = foundation.Regions[key];
        var cancelErrors = new List<string>();
        var workspace = await arm.GetAsync(region.WorkspaceId, MlApi, ct);
        if (workspace is not null)
        {
            Owned(workspace, region.WorkspaceId, region.OwnershipTags, ephemeral: false);
            try
            {
                foreach (var job in OnCompute(await arm.ListAsync($"{region.WorkspaceId}/jobs", MlApi, ct), foundation, region).Where(job => !Terminal(job)))
                {
                    try
                    {
                        await arm.SendAsync("POST", $"{Text(job["id"])}/cancel", MlApi, null, null, ct);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        cancelErrors.Add($"Cancel {Text(job["name"])} failed.");
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                cancelErrors.Add("Job enumeration failed.");
            }
        }

        if (await arm.GetAsync(region.ComputeId, MlApi, ct) is { } compute)
        {
            Owned(compute, region.ComputeId, region.OwnershipTags, ephemeral: true);
            await arm.SendAsync("DELETE", $"{region.ComputeId}?underlyingResourceAction=Delete", MlApi, null, null, ct);
        }

        await WaitAsync(async () => await arm.GetAsync(region.ComputeId, MlApi, ct) is null, waits.Compute,
            $"GPU release is NOT verified for {key}; NAT and public IP are preserved until compute is absent.");
        if (workspace is not null)
        {
            try
            {
                await WaitAsync(async () => !OnCompute(await arm.ListAsync($"{region.WorkspaceId}/jobs", MlApi, ct), foundation, region).Any(job => !Terminal(job)),
                    waits.Jobs, "Relevant jobs are not verified terminal.");
            }
            catch (ConflictException error)
            {
                throw new ConflictException(string.Join(" ", [error.Message, .. cancelErrors]));
            }

            warnings.AddRange(cancelErrors.Select(problem => $"{problem} Fresh reads later proved all relevant jobs terminal."));
        }

        await ReleaseEgressAsync(region, ct);
    }

    private async Task ReleaseEgressAsync(RegionalFoundation region, CancellationToken ct)
    {
        if (!region.Policy.ManageComputeEgress) return;
        var nat = await arm.GetAsync(region.NatGatewayId, NetworkApi, ct);
        var pip = await arm.GetAsync(region.PublicIpId, NetworkApi, ct);

        if (nat is not null)
        {
            Owned(nat, region.NatGatewayId, region.OwnershipTags, ephemeral: true);
            foreach (var attached in nat["properties"]?["subnets"] as JsonArray ?? new JsonArray())
            {
                Require(Same(Text(attached?["id"]), region.LandingZone.GpuSubnetId), "The owned NAT has an unexpected subnet attachment.");
            }
        }

        if (pip is not null) Owned(pip, region.PublicIpId, region.OwnershipTags, ephemeral: true);
        var subnet = await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, ct);
        Subnet(subnet, region);
        if (subnet!["properties"]?["natGateway"] is not null)
        {
            Require(nat is not null, "A NAT association exists without its owned NAT; refusing to detach it.");
            await arm.SendAsync("PUT", region.LandingZone.GpuSubnetId, NetworkApi, SubnetWrite(subnet, null),
                Text(subnet["etag"]) ?? throw new ConflictException("The subnet has no ETag; refusing an unconditional write."), ct);
            await WaitAsync(async () =>
            {
                var fresh = await arm.GetAsync(region.LandingZone.GpuSubnetId, NetworkApi, ct);
                Subnet(fresh, region);
                return fresh!["properties"]?["natGateway"] is null;
            }, waits.Network, "Compute is absent but NAT detachment was not verified; network billing may continue.");
        }

        foreach (var id in new[] { region.NatGatewayId, region.PublicIpId })
        {
            if (await arm.GetAsync(id, NetworkApi, ct) is { } resource)
            {
                Owned(resource, id, region.OwnershipTags, ephemeral: true);
                await arm.SendAsync("DELETE", id, NetworkApi, null, null, ct);
            }

            await WaitAsync(async () => await arm.GetAsync(id, NetworkApi, ct) is null, waits.Network, "Owned egress deletion was not verified.");
        }
    }

    private async Task WaitAsync(Func<Task<bool>> done, TimeSpan limit, string failure)
    {
        var deadline = Now + limit;
        while (!await done())
        {
            if (Now >= deadline) throw new ConflictException(failure);
            await Task.Delay(waits.Poll, time, CancellationToken.None);
        }
    }

    // ---- audit ---------------------------------------------------------------------------------------------------------

    private async Task AuditAsync(string action, string actor, string[] regions, string result, object? detail, CancellationToken ct)
    {
        var now = Now;
        Activity.Current?.AddEvent(new ActivityEvent("studio.audit", tags: new ActivityTagsCollection { ["action"] = action, ["result"] = result }));
        try
        {
            var id = Guid.NewGuid().ToString("D");
            await store.AppendAuditAsync($"audit/{now:yyyyMMdd}/{now:HHmmssfff}-{action}-{id}.json", StudioContracts.Write(new
            {
                schemaVersion = 1, id, at = now, action, actor, scope = foundation.Scope, regions, result, detail,
            }), ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "The {Action} audit record could not be stored.", action);
        }
    }
}
