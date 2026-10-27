using System.Net;
using System.Text.RegularExpressions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.Operations;

namespace VideoSafetyStudio.Server.Health;

/// <summary>Starts a worker only when the configuration is ready; its dependencies are not resolved otherwise.</summary>
public static class GuardedWorkers
{
    public static IServiceCollection AddGuardedWorker<T>(this IServiceCollection services) where T : BackgroundService =>
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ConfigReport>().Ready ? ActivatorUtilities.CreateInstance<T>(sp) : new IdleWorker());

    private sealed class IdleWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}

/// <summary>One missing or invalid setting. It names the App Service setting and shows a static example; it never carries a configured value.</summary>
public sealed record ConfigIssue(string Setting, string Severity, string Problem, string Example, string Where);

public sealed record ConfigReport(string Mode, bool Ready, IReadOnlyList<ConfigIssue> Issues)
{
    public string Status => Ready ? (Issues.Count == 0 ? "ready" : "ready-with-warnings") : "misconfigured";
}

/// <summary>
/// Checks the configuration without failing the host. A misconfigured site still starts, serves the health page and refuses everything else,
/// so an operator can read what to fix instead of finding a crash loop.
/// </summary>
public static partial class ConfigHealth
{
    private const string Sub = "11111111-1111-1111-1111-111111111111";
    private const string Rg = $"/subscriptions/{Sub}/resourceGroups/rg-wan-demo";

    public static string EnvName(string path) => path.Replace(":", "__", StringComparison.Ordinal);

    public static ConfigReport Evaluate(IConfiguration configuration, IHostEnvironment environment)
    {
        var issues = new List<ConfigIssue>();
        var provider = (configuration["Generation:Provider"] ?? "azureml").Trim().ToLowerInvariant();
        if (provider is not ("azureml" or "comfyui"))
        {
            Add(issues, "Generation:Provider", "Must be azureml or comfyui.", "azureml", "App Service > Settings > Environment variables > App settings.");
            provider = "azureml";
        }

        CheckAuth(configuration, environment, issues);

        var studio = configuration.GetSection(StudioOptions.Section);
        var hosted = studio.GetSection("Regions").GetChildren().Any();
        string mode;
        if (provider == "comfyui")
        {
            mode = "local-comfyui";
            if (hosted) Add(issues, "Generation:Provider", "The Studio regions are set but the provider is comfyui, so they are ignored.", "azureml", "Set the provider to azureml to use the Azure ML spokes.", warning: true);
        }
        else if (hosted)
        {
            mode = "hosted-multi-region";
            CheckStudio(studio.Get<StudioOptions>() ?? new StudioOptions(), studio, issues);
        }
        else
        {
            mode = "single-workspace";
            CheckFlat(configuration, issues);
        }

        return new ConfigReport(mode, !issues.Any(issue => issue.Severity == "error"), issues);
    }

    private static void CheckStudio(StudioOptions options, IConfigurationSection section, List<ConfigIssue> issues)
    {
        foreach (var unknown in StudioOptionsKeys.Unknown(section))
        {
            Add(issues, unknown, "Not a Studio setting: it is misspelled or no longer exists.", "", "Check the spelling against docs/configuration.md.");
        }

        if (!Guid.TryParseExact(options.TenantId, "D", out _)) Add(issues, "Studio:TenantId", Missing(options.TenantId, "a GUID"), Sub, "Azure portal > Microsoft Entra ID > Overview > Tenant ID.");
        var subscriptionOk = Guid.TryParseExact(options.SubscriptionId, "D", out _);
        if (!subscriptionOk) Add(issues, "Studio:SubscriptionId", Missing(options.SubscriptionId, "a GUID"), Sub, "Azure portal > Subscriptions > your subscription > Subscription ID.");
        if (options.DeploymentName is null || !DeploymentName().IsMatch(options.DeploymentName))
        {
            Add(issues, "Studio:DeploymentName", Missing(options.DeploymentName, "a short lowercase name (letters, digits, single hyphens)"), "wan-demo", "Your choice. Use the deployment name from your Terraform variables, if you used them.");
        }

        if (options.ControlRegion is null || !options.Regions.ContainsKey(options.ControlRegion))
        {
            Add(issues, "Studio:ControlRegion", "Must name one of the regions under Studio:Regions:<region>.", options.Regions.Keys.FirstOrDefault() ?? "eastus2", "The region whose storage keeps operational state, settings and audit.");
        }

        foreach (var (key, region) in options.Regions)
        {
            var at = $"Studio:Regions:{key}";
            if (!RegionKey().IsMatch(key)) Add(issues, at, "The region key must be lowercase letters and digits.", "eastus2", "Your choice, for example the Azure region name.");
            if (region is null) continue;
            void Arm(string path, string? value, string provider, string type, string example)
            {
                if (!IsArm(value, subscriptionOk ? options.SubscriptionId : null, provider, type))
                {
                    Add(issues, $"{at}:{path}", Missing(value, $"the {type} resource ID" + (subscriptionOk ? " in the studio subscription" : "")), example,
                        "Azure portal > open the resource > Overview > JSON View > Resource ID. If you used the Terraform in infra/, run terraform output regions.");
                }
            }

            Arm("Workspace:Id", region.Workspace.Id, "Microsoft.MachineLearningServices", "workspaces", $"{Rg}/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo");
            if (region.Compute.Id is null || region.Workspace.Id is null || !region.Compute.Id.StartsWith(region.Workspace.Id + "/computes/", StringComparison.OrdinalIgnoreCase))
            {
                Add(issues, $"{at}:Compute:Id", Missing(region.Compute.Id, "the workspace ID followed by /computes/<name>"), $"{Rg}/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo/computes/wan-gpu",
                    "Not an existing resource: the studio creates the cluster with this name at Start and deletes it at Stop. Use the workspace ID, /computes/ and a name of your choice.");
            }

            Arm("Compute:IdentityId", region.Compute.IdentityId, "Microsoft.ManagedIdentity", "userAssignedIdentities", $"{Rg}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-wan-demo-gpu");
            if (!Guid.TryParseExact(region.Compute.IdentityClientId, "D", out _)) Add(issues, $"{at}:Compute:IdentityClientId", Missing(region.Compute.IdentityClientId, "a GUID"), "77777777-7777-7777-7777-777777777777", "Azure portal > the GPU user-assigned managed identity > Overview > Client ID.");
            Arm("Storage:AccountId", region.Storage.AccountId, "Microsoft.Storage", "storageAccounts", $"{Rg}/providers/Microsoft.Storage/storageAccounts/stwandemo");
            Arm("Platform:RegistryId", region.Platform.RegistryId, "Microsoft.ContainerRegistry", "registries", $"{Rg}/providers/Microsoft.ContainerRegistry/registries/crwandemo");
            Arm("Network:GpuSubnetId", region.Network.GpuSubnetId, "Microsoft.Network", "virtualNetworks", $"/subscriptions/{Sub}/resourceGroups/customer-network/providers/Microsoft.Network/virtualNetworks/spoke/subnets/wan-gpu");
            if (region.Network.GpuSubnetCidr is null || !IPNetwork.TryParse(region.Network.GpuSubnetCidr, out _)) Add(issues, $"{at}:Network:GpuSubnetCidr", Missing(region.Network.GpuSubnetCidr, "an IPv4 network"), "10.42.1.0/26", "Azure portal > the virtual network > Subnets > the GPU subnet > Address range.");
            Arm("Network:GpuNsgId", region.Network.GpuNsgId, "Microsoft.Network", "networkSecurityGroups", $"{Rg}/providers/Microsoft.Network/networkSecurityGroups/nsg-wan-demo-gpu");
            if (region.Compute.ManageEgress)
            {
                Arm("Network:NatGatewayId", region.Network.NatGatewayId, "Microsoft.Network", "natGateways", $"{Rg}/providers/Microsoft.Network/natGateways/nat-wan-demo");
                Arm("Network:PublicIpId", region.Network.PublicIpId, "Microsoft.Network", "publicIPAddresses", $"{Rg}/providers/Microsoft.Network/publicIPAddresses/pip-wan-demo-egress");
            }

            if (region.Network.PrivateConnectivityHosts.Length < 5 || !region.Network.PrivateConnectivityHosts.All(host => Uri.CheckHostName(host) == UriHostNameType.Dns && host == host.ToLowerInvariant()))
            {
                Add(issues, $"{at}:Network:PrivateConnectivityHosts:0", "At least five lowercase host names are needed, one setting per index (0, 1, 2, ...): storage blob and file, key vault, registry, and the Azure ML API and notebooks.",
                    "stwandemo.blob.core.windows.net", "Azure portal > each private endpoint > DNS configuration > FQDN. Start refuses to run unless these resolve privately.");
            }

            if (region.Compute.MaxNodes < 1) Add(issues, $"{at}:Compute:MaxNodes", "Must be at least 1.", "1", "Your choice: the ceiling for concurrent nodes.");
            if (region.Compute.MaxJobMinutes is < 1 or > StudioContracts.MaxJobMinutes) Add(issues, $"{at}:Compute:MaxJobMinutes", $"Must be 1 to {StudioContracts.MaxJobMinutes}.", "120", "Your choice: the ceiling for one job.");
            if (region.Compute.MaxPaygHourlyUsd <= 0) Add(issues, $"{at}:Compute:MaxPaygHourlyUsd", "Must be above 0.", "4", "Your choice: the pay-as-you-go price check at Start. It is not a billing cap.");
        }

        // The cross-setting rules (shared workspace or subnet, customer NSG, tags) only once the individual settings are sound.
        if (issues.Any(issue => issue.Severity == "error" && issue.Setting.StartsWith("Studio", StringComparison.Ordinal))) return;
        try
        {
            StudioOptionsMapper.ToFoundation(options);
        }
        catch (ValidationException error)
        {
            Add(issues, "Studio", Trim(error.Message), "", "The setting named in the message; see docs/configuration.md.");
        }
    }

    private static void CheckFlat(IConfiguration configuration, List<ConfigIssue> issues)
    {
        try
        {
            var options = configuration.GetSection(AzureMlOptions.Section).Get<AzureMlOptions>() ?? new AzureMlOptions();
            AzureMlConfig.Release(options, AzureMlConfig.Foundation(options));
        }
        catch (Exception error) when (error is ValidationException or InvalidOperationException or ArgumentException)
        {
            Add(issues, "Studio:Regions:<region>:Workspace:Id",
                "No Studio regions are configured, so the single-workspace AzureMl settings are used, and they are incomplete: " + Trim(error.Message),
                $"{Rg}/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan-demo",
                "Set the Studio settings (see docs/configuration.md and the sample), or complete the AzureMl settings named in the message.");
        }
    }

    private static void CheckAuth(IConfiguration configuration, IHostEnvironment environment, List<ConfigIssue> issues)
    {
        var mode = configuration["Auth:Mode"]?.Trim();
        if (!string.IsNullOrEmpty(mode) && !mode.Equals("None", StringComparison.OrdinalIgnoreCase) && !mode.Equals("Entra", StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "Auth:Mode", "Must be None or Entra.", "Entra", "App Service > Settings > Environment variables > App settings.");
            return;
        }

        var entra = string.IsNullOrEmpty(mode) ? !environment.IsDevelopment() : mode.Equals("Entra", StringComparison.OrdinalIgnoreCase);
        if (!entra)
        {
            if (!environment.IsDevelopment()) Add(issues, "Auth:Mode", "Sign-in is off: anyone who can reach this site can submit GPU jobs, and the administrator role is never granted.", "Entra", "Use None only behind a gateway that authenticates.", warning: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:ClientId"]))
        {
            Add(issues, "Auth:ClientId", "Missing: the Entra app registration's client ID.", "99999999-9999-9999-9999-999999999999", "Microsoft Entra admin center > App registrations > your app > Overview > Application (client) ID.");
        }

        if (!OriginPattern().IsMatch(configuration["Auth:PublicOrigin"]?.Trim() ?? ""))
        {
            Add(issues, "Auth:PublicOrigin", "Missing or not an https://host origin.", "https://app-wan-demo.azurewebsites.net", "App Service > Overview > Default domain (or your custom domain). No path, no trailing slash.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:TenantId"]) && string.IsNullOrWhiteSpace(configuration["Studio:TenantId"]) && string.IsNullOrWhiteSpace(configuration["AzureMl:TenantId"]))
        {
            Add(issues, "Auth:TenantId", "Missing (Studio:TenantId is used when this is not set).", Sub, "Azure portal > Microsoft Entra ID > Overview > Tenant ID.");
        }

        if (string.IsNullOrWhiteSpace(configuration["Auth:ClientSecret"]) && string.IsNullOrWhiteSpace(configuration["AZURE_CLIENT_ID"]))
        {
            Add(issues, "AZURE_CLIENT_ID", "Missing: with no Auth:ClientSecret the site's managed identity signs the sign-in, and its client ID is needed.", "77777777-7777-7777-7777-777777777777",
                "App Service > Settings > Identity > the user-assigned identity > Client ID. Prefer this over a client secret.");
        }
    }

    private static void Add(List<ConfigIssue> issues, string setting, string problem, string example, string where, bool warning = false) =>
        issues.Add(new ConfigIssue(EnvName(setting), warning ? "warning" : "error", problem, example, where));

    private static string Missing(string? value, string expected) => string.IsNullOrWhiteSpace(value) ? $"Missing. Expected {expected}." : $"Not valid. Expected {expected}.";

    private static string Trim(string message) => message.Length <= 300 ? message : message[..300];

    private static bool IsArm(string? id, string? subscription, string provider, string type) =>
        id is not null && !id.Any(char.IsControl) && Regex.IsMatch(id,
            $"^/subscriptions/{(subscription is null ? "[0-9a-fA-F-]{36}" : Regex.Escape(subscription))}/resourceGroups/[^/?#%\\\\]+/providers/{Regex.Escape(provider)}/{type}/[^/?#%\\\\]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,14}[a-z0-9]\z")]
    private static partial Regex DeploymentName();

    [GeneratedRegex(@"^[a-z][a-z0-9]+\z")]
    private static partial Regex RegionKey();

    [GeneratedRegex(@"^(https://[a-z0-9-]+(\.[a-z0-9-]+)+|http://(localhost|127\.0\.0\.1)(:\d+)?)$")]
    private static partial Regex OriginPattern();
}
