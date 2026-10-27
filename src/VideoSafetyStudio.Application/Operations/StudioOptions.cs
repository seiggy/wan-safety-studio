using System.Text.RegularExpressions;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

/// <summary>
/// The studio's deployment topology, bound from configuration section <c>Studio</c> (environment variables <c>Studio__...</c>, App Service application
/// settings, <c>appsettings.json</c>). Nothing is read from Terraform outputs: an operator lists the regions and the exact resource IDs the host may
/// manage. Live limits that administrators change while running (concurrency, timeouts, priority, queue wait) are not here; they are edited in the
/// Administration page and are always bounded by the policy ceilings below.
/// </summary>
public sealed class StudioOptions
{
    public const string Section = "Studio";

    /// <summary>Entra tenant of the deployment. Every resource must be in <see cref="SubscriptionId"/> of this tenant.</summary>
    public string? TenantId { get; set; }

    public string? SubscriptionId { get; set; }

    /// <summary>A short lowercase name (letters, digits, single hyphens) that identifies this deployment in tags and audit records.</summary>
    public string? DeploymentName { get; set; }

    /// <summary>The region key whose storage holds operational state, settings, audit and job records.</summary>
    public string? ControlRegion { get; set; }

    /// <summary>One entry per Azure ML spoke, keyed by a lowercase region name such as <c>eastus2</c>. Empty means no hosted multi-region operation.</summary>
    public Dictionary<string, StudioRegionOptions> Regions { get; set; } = new(StringComparer.Ordinal);

    public bool Configured => Regions.Count > 0;
}

public sealed class StudioRegionOptions
{
    public WorkspaceOptions Workspace { get; set; } = new();

    public ComputeOptions Compute { get; set; } = new();

    public StudioStorageOptions Storage { get; set; } = new();

    public PlatformOptions Platform { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();

    /// <summary>Tags the studio's foundation resources carry (the host refuses to act on a resource without them). Defaults to application, deployment, managedBy=terraform and region.</summary>
    public Dictionary<string, string> OwnershipTags { get; set; } = new(StringComparer.Ordinal);
}

public sealed class WorkspaceOptions
{
    /// <summary>ARM ID of the Azure ML workspace.</summary>
    public string? Id { get; set; }
}

public sealed class ComputeOptions
{
    /// <summary>ARM ID the cluster has (or will have) under the workspace: <c>{workspace id}/computes/{name}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>ARM ID and client ID of the identity jobs run as.</summary>
    public string? IdentityId { get; set; }

    public string? IdentityClientId { get; set; }

    /// <summary>Hard ceilings. Live settings can only be set at or below them.</summary>
    public int MaxNodes { get; set; } = 1;

    public int MaxJobMinutes { get; set; } = StudioContracts.MaxJobMinutes;

    /// <summary>Safety threshold for the exact Linux pay-as-you-go price at Start. It is not a billing cap.</summary>
    public decimal MaxPaygHourlyUsd { get; set; } = 4;

    /// <summary>True when the studio creates and removes its own NAT gateway and public IP for the compute (see <see cref="NetworkOptions.NatGatewayId"/>).</summary>
    public bool ManageEgress { get; set; }
}

public sealed class StudioStorageOptions
{
    /// <summary>ARM ID of the region's private storage account (containers <c>wan-studio</c>, <c>wan-release</c> and <c>wan-control</c>).</summary>
    public string? AccountId { get; set; }
}

public sealed class PlatformOptions
{
    /// <summary>ARM ID of the region's Premium container registry: the host accepts only release and trainer images from it.</summary>
    public string? RegistryId { get; set; }
}

public sealed class NetworkOptions
{
    /// <summary>The dedicated GPU subnet. It must not be shared with another workload.</summary>
    public string? GpuSubnetId { get; set; }

    public string? GpuSubnetCidr { get; set; }

    public string? GpuNsgId { get; set; }

    /// <summary>True when the GPU subnet's NSG belongs to the customer (Start then needs explicit approval of its rules).</summary>
    public bool CustomerManagedNsg { get; set; }

    /// <summary>Required only with <see cref="ComputeOptions.ManageEgress"/>.</summary>
    public string? NatGatewayId { get; set; }

    public string? PublicIpId { get; set; }

    public Dictionary<string, string> PublicIpTags { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Host names Start must see resolve privately and answer on TCP 443 (storage, vault, registry, Azure ML API and notebooks); at least five.</summary>
    public string[] PrivateConnectivityHosts { get; set; } = [];
}

/// <summary>Maps the bound options to the runtime model and validates them. Every problem is reported with the setting's path.</summary>
public static class StudioOptionsMapper
{
    public static StudioFoundation ToFoundation(StudioOptions options)
    {
        var scope = new StudioScope(options.TenantId ?? "", options.SubscriptionId ?? "", options.DeploymentName ?? "");
        var regions = new Dictionary<string, RegionalFoundation>(StringComparer.Ordinal);
        foreach (var (key, region) in options.Regions)
        {
            if (region is null) throw new ValidationException($"{StudioOptions.Section}:Regions:{key} is empty.");
            var network = region.Network;
            var tags = region.OwnershipTags is { Count: > 0 } supplied ? new Dictionary<string, string>(supplied, StringComparer.Ordinal) : StudioResourceNames.Tags(scope, key);
            regions[key] = new RegionalFoundation(
                region.Workspace.Id ?? "", region.Compute.Id ?? "", region.Compute.IdentityId ?? "",
                region.Compute.IdentityClientId ?? "", region.Storage.AccountId ?? "", region.Platform.RegistryId ?? "",
                network.GpuNsgId ?? "", network.NatGatewayId ?? "", network.PublicIpId ?? "",
                StudioContracts.RunContainer, StudioContracts.RunDatastore, StudioContracts.ReleaseContainer, StudioContracts.ReleaseDatastore, StudioContracts.ControlContainer,
                new LandingZone(network.GpuSubnetId ?? "", network.GpuSubnetCidr ?? "", true, network.CustomerManagedNsg ? network.GpuNsgId : null),
                new ComputePolicy(StudioContracts.VmSize, "LowPriority", 0, region.Compute.MaxNodes, 120, 1, region.Compute.MaxJobMinutes,
                    region.Compute.MaxPaygHourlyUsd, region.Compute.ManageEgress, new Dictionary<string, string>(network.PublicIpTags, StringComparer.Ordinal)),
                tags, [.. network.PrivateConnectivityHosts]);
        }

        var foundation = new StudioFoundation(scope, options.ControlRegion ?? "", regions);
        StudioContractRules.Foundation(foundation);
        return foundation;
    }
}
