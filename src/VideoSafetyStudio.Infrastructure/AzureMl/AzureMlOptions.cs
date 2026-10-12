namespace VideoSafetyStudio.Infrastructure.AzureMl;

/// <summary>The `AzureMl` configuration section: workspace identity and resources, release pointers and behaviour.</summary>
public sealed class AzureMlOptions
{
    public const string Section = "AzureMl";

    public string? SubscriptionId { get; set; }

    public string? TenantId { get; set; }

    public string? ResourceGroup { get; set; }

    public string? Workspace { get; set; }

    public string? Compute { get; set; }

    /// <summary>Client id of the user-assigned identity the job runs as.</summary>
    public string? ComputeIdentityClientId { get; set; }

    public string? StorageAccount { get; set; }

    public string? Container { get; set; }

    public string Datastore { get; set; } = "workspaceblobstore";

    public AzureMlReleaseOptions Release { get; set; } = new();

    /// <summary>Whether generation may submit jobs (Start sets it, Stop clears it); read through IOptionsMonitor on every submit.</summary>
    public bool Armed { get; set; }

    public double PollSeconds { get; set; } = 5;

    /// <summary>Jobs submitted at once; Azure ML queues them on the single node.</summary>
    public int MaxConcurrency { get; set; } = 25;

    public int MaxJobMinutes { get; set; } = 120;

    public int MaxUploadMb { get; set; } = 64;
}

/// <summary>Pointers to the prepared environment, models and code (set by Publish). An empty EnvironmentId means not prepared.</summary>
public sealed class AzureMlReleaseOptions
{
    public string? EnvironmentId { get; set; }

    public string? ModelsRef { get; set; }

    public string? CodeUri { get; set; }
}
