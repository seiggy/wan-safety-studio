using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

internal static partial class StudioContractRules
{
    private static readonly string[] RuntimeHelpers =
        ["azureml/run_workflow_job.py", "azureml/aml_project_layout.py", "azureml/workflow_profiles.py", "azureml/workflow_utils.py", "azureml/extra_model_paths.yaml"];

    private static readonly Dictionary<string, string> DnsZones = new(StringComparer.Ordinal)
    {
        ["blob"] = "privatelink.blob.core.windows.net", ["file"] = "privatelink.file.core.windows.net",
        ["vault"] = "privatelink.vaultcore.azure.net", ["registry"] = "privatelink.azurecr.io",
        ["api"] = "privatelink.api.azureml.ms", ["notebooks"] = "privatelink.notebooks.azure.net",
    };

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,14}[a-z0-9]\z")]
    private static partial Regex Slug();
    [GeneratedRegex(@"^[a-z][a-z0-9]+\z")]
    private static partial Regex Region();
    [GeneratedRegex(@"^[a-f0-9]{64}\z")]
    private static partial Regex Sha256();
    [GeneratedRegex(@"^[a-f0-9]{40}\z")]
    private static partial Regex Revision();
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,254}\z")]
    private static partial Regex JobName();

    internal static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new ValidationException(message);
    }

    private static bool Uuid(string? value) => value is not null && Guid.TryParseExact(value, "D", out var parsed) &&
        parsed != Guid.Empty && value == parsed.ToString("D");

    private static void Scope(StudioScope? scope)
    {
        Require(scope is not null && Uuid(scope.TenantId) && Uuid(scope.SubscriptionId) &&
            scope.DeploymentName is not null && Slug().IsMatch(scope.DeploymentName) &&
            !scope.DeploymentName.Contains("--", StringComparison.Ordinal), "Invalid explicit deployment scope.");
    }

    public static void Foundation(StudioFoundation value)
    {
        Require(value is not null, "Studio configuration is missing.");
        Require(Uuid(value.Scope?.TenantId) && Uuid(value.Scope.SubscriptionId) && value.Scope.DeploymentName is not null &&
            Slug().IsMatch(value.Scope.DeploymentName) && !value.Scope.DeploymentName.Contains("--", StringComparison.Ordinal),
            "Studio:TenantId and Studio:SubscriptionId must be GUIDs and Studio:DeploymentName a short lowercase name (letters, digits, single hyphens).");
        Require(value.Regions is { Count: > 0 } && value.ControlRegion is not null && value.Regions.ContainsKey(value.ControlRegion),
            "Studio:ControlRegion must name one of the entries under Studio:Regions.");
        var subscription = value.Scope.SubscriptionId;
        var workspaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gpuSubnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (region, item) in value.Regions)
        {
            var at = $"Studio:Regions:{region}";
            Require(Region().IsMatch(region) && item is not null, $"{at}: the region key must be lowercase letters and digits (for example eastus2).");
            Require(StudioId(item.WorkspaceId, subscription, "Microsoft.MachineLearningServices", "workspaces"), $"{at}:Workspace:Id must be the Azure ML workspace's ARM ID in the studio subscription.");
            Require(item.ComputeId.StartsWith(item.WorkspaceId + "/computes/", StringComparison.OrdinalIgnoreCase) &&
                item.ComputeId.IndexOf('/', item.WorkspaceId.Length + "/computes/".Length) < 0 && item.ComputeId.Length > item.WorkspaceId.Length + "/computes/".Length,
                $"{at}:Compute:Id must be '<workspace id>/computes/<name>'.");
            Require(StudioId(item.ComputeIdentityId, subscription, "Microsoft.ManagedIdentity", "userAssignedIdentities"), $"{at}:Compute:IdentityId must be a user-assigned identity ARM ID.");
            Require(Uuid(item.ComputeIdentityClientId), $"{at}:Compute:IdentityClientId must be a canonical GUID.");
            Require(StudioId(item.StorageAccountId, subscription, "Microsoft.Storage", "storageAccounts"), $"{at}:Storage:AccountId must be a storage account ARM ID.");
            Require(StudioId(item.RegistryId, subscription, "Microsoft.ContainerRegistry", "registries"), $"{at}:Platform:RegistryId must be a container registry ARM ID.");
            Require(StudioId(item.NetworkSecurityGroupId, subscription, "Microsoft.Network", "networkSecurityGroups"), $"{at}:Network:GpuNsgId must be a network security group ARM ID.");
            if (item.Policy.ManageComputeEgress)
            {
                Require(StudioId(item.NatGatewayId, subscription, "Microsoft.Network", "natGateways") &&
                    StudioId(item.PublicIpId, subscription, "Microsoft.Network", "publicIPAddresses"),
                    $"{at}:Network:NatGatewayId and PublicIpId are required (and must be ARM IDs) when Compute:ManageEgress is true.");
            }

            Require(workspaces.Add(item.WorkspaceId) && gpuSubnets.Add(item.LandingZone.GpuSubnetId), $"{at}: regions must not share a workspace or a GPU subnet.");
            Require(item.LandingZone.ExistingGpuNsgId is null || string.Equals(item.LandingZone.ExistingGpuNsgId, item.NetworkSecurityGroupId, StringComparison.OrdinalIgnoreCase),
                $"{at}:Network: a customer-managed NSG must be the GPU NSG.");
            Landing(item.LandingZone, subscription, at);
            Policy(item.Policy, at);
            Require(item.OwnershipTags is { Count: > 0 } && item.OwnershipTags.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) &&
                item.OwnershipTags.ContainsKey("managedBy"), $"{at}:OwnershipTags must be non-empty and include managedBy.");
            Require(item.PrivateConnectivityHosts is { Length: >= 5 } && item.PrivateConnectivityHosts.All(PrivateHost),
                $"{at}:Network:PrivateConnectivityHosts needs at least five lowercase host names (storage, vault, registry, Azure ML API and notebooks).");
        }
    }

    private static bool StudioId(string? id, string subscription, string provider, string kind) =>
        id is not null && !id.Any(char.IsControl) && Regex.IsMatch(id,
            $"^/subscriptions/{Regex.Escape(subscription)}/resourceGroups/[^/?#%\\\\]+/providers/{Regex.Escape(provider)}/{kind}/[^/?#%\\\\]+\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool SameMap(Dictionary<string, string>? actual, Dictionary<string, string>? expected) =>
        actual is not null && expected is not null && actual.Count == expected.Count &&
        actual.All(pair => pair.Value is not null && expected.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void Landing(LandingZone? zone, string subscription, string at = "Studio")
    {
        Require(zone is not null && zone.GpuSubnetDedicated, $"{at}:Network: the GPU subnet must be dedicated to the studio.");
        Resource(zone.GpuSubnetId, subscription, "Microsoft.Network", "virtualNetworks", "/subnets");
        if (zone.ExistingGpuNsgId is { } nsg) Resource(nsg, subscription, "Microsoft.Network", "networkSecurityGroups");
        Require(zone.GpuSubnetCidr is not null && IPNetwork.TryParse(zone.GpuSubnetCidr, out var network) &&
            network.BaseAddress.AddressFamily == AddressFamily.InterNetwork && network.ToString() == zone.GpuSubnetCidr,
            $"{at}:Network:GpuSubnetCidr must be a canonical IPv4 network.");
    }

    private static void Resource(string? id, string subscription, string provider, string kind, string child = "") =>
        Require(id is not null && !id.Any(char.IsControl) && Regex.IsMatch(id,
            $"^/subscriptions/{Regex.Escape(subscription)}/resourceGroups/[^/?#%\\\\]+/providers/{Regex.Escape(provider)}/{kind}/[^/?#%\\\\]+" +
            (child.Length == 0 ? "" : $"{child}/[^/?#%\\\\]+") + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "Landing-zone resource ID is outside the selected subscription or resource type.");

    private static bool PrivateHost(string? host) => host is not null &&
        Uri.CheckHostName(host) == UriHostNameType.Dns && host == host.ToLowerInvariant() &&
        !host.Contains('*') && !host.EndsWith('.');

    internal static void Policy(ComputePolicy? value, string at = "Studio") => Require(
        value is not null && value.VmSize == StudioContracts.VmSize && value.VmPriority == "LowPriority" &&
        value.MinNodes == 0 && value.MaxNodes >= 1 && value.IdleSeconds == 120 && value.InstanceCount == 1 &&
        value.MaxJobMinutes is >= 1 and <= StudioContracts.MaxJobMinutes && value.MaxPaygHourlyUsd > 0 &&
        value.EgressPublicIpTags is not null &&
        value.EgressPublicIpTags.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
        $"{at}:Compute: MaxNodes must be at least 1, MaxJobMinutes 1 to {StudioContracts.MaxJobMinutes}, MaxPaygHourlyUsd above 0, and PublicIpTags non-empty pairs. The SKU, Spot priority, zero minimum, 120-second idle and single instance are fixed.");

    public static void Build(GpuBuildManifest value)
    {
        Require(value.SchemaVersion == 1 && value.ArtifactId is not null && Sha256().IsMatch(value.ArtifactId) &&
            value.Profile is "wan" or "ltx" or "ltx_i2v" or "minimax_h3" &&
            value.SourceSha is not null && Revision().IsMatch(value.SourceSha) &&
            value.DependencyLockSha256 is not null && Sha256().IsMatch(value.DependencyLockSha256) &&
            Digest(value.ImageDigest) && value.BaseImage is not null &&
            Regex.IsMatch(value.BaseImage, @"^[a-zA-Z0-9./:_-]+@sha256:[a-f0-9]{64}\z"),
            "Build manifest requires versioned, content-addressed source, dependency and image identities.");
        Files(value.Code);
        Files(value.Licenses);
        Files(value.BuildInputs);
        Require(value.Licenses.All(file => file.Bytes > 0) && value.BuildInputs.All(file => file.Bytes > 0) &&
            value.Code.Where(file => file.Path is "LICENSE" or "NOTICE.txt").All(file => file.Bytes > 0),
            "Licence, attribution and build-input files cannot be empty.");
        Require(value.BuildInputs.Any(file => file.Path == "requirements-runtime.lock" && file.Sha256 == value.DependencyLockSha256) &&
            value.BuildInputs.Any(file => file.Path == "profiles.json") &&
            value.BuildInputs.Any(file => file.Path == "Dockerfile") &&
            value.BuildInputs.Any(file => file.Path == "runtime-ltx.patch"),
            "Build inputs must retain the reviewed lock, source/profile pins, Dockerfile and runtime patch identity.");
        Require(value.Code.All(file => CodePath(file.Path, value.Profile)) &&
            RuntimeHelpers.All(path => value.Code.Any(file => file.Path == path)) &&
            (value.Profile is not ("ltx" or "ltx_i2v") || value.Code.Any(file => file.Path == "azureml/bootstrap_ltx_and_run.py")) &&
            value.Code.Any(file => file.Path == "src/main.py") && value.Code.Any(file => file.Path == "LICENSE"),
            "GPU code must contain the runner closure, ComfyUI and attribution, never operator/submission files.");
        Require(value.Models is { Length: > 0 } && value.Models.All(file => file is not null) &&
            value.Models.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == value.Models.Length,
            "Build must contain unique, verified model files.");
        foreach (var file in value.Models)
        {
            File(file.Path, file.Bytes, file.Sha256);
            Require(file.Bytes > 0 && file.Repository is not null && Regex.IsMatch(file.Repository, @"^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+\z") &&
                file.Revision is not null && Revision().IsMatch(file.Revision) &&
                file.Source is not null && file.Source.StartsWith($"https://huggingface.co/{file.Repository}/resolve/{file.Revision}/", StringComparison.Ordinal) &&
                Uri.TryCreate(file.Source, UriKind.Absolute, out var uri) && uri.Host == "huggingface.co" &&
                uri.AbsoluteUri == file.Source &&
                uri.AbsolutePath.StartsWith($"/{file.Repository}/resolve/{file.Revision}/", StringComparison.Ordinal) &&
                uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
                !string.IsNullOrWhiteSpace(file.License), "Model origins must retain immutable source and licence provenance.");
        }
        Require(value.ArtifactId == StudioContracts.ArtifactId(value), "Build artifact ID differs from its canonical content manifest.");
    }

    private static bool Digest(string? value) => value is not null && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        Sha256().IsMatch(value[7..]);

    private static void Files(ArtifactFile[]? files)
    {
        Require(files is { Length: > 0 } && files.All(file => file is not null) &&
            files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == files.Length,
            "Artifact file records must be present and unique.");
        foreach (var file in files) File(file.Path, file.Bytes, file.Sha256);
    }

    private static void File(string? path, long bytes, string? hash) => Require(
        path is not null && Path(path) && bytes >= 0 && hash is not null && Sha256().IsMatch(hash),
        "Artifact file needs a relative non-traversing path, nonnegative size and SHA-256.");

    private static bool Path(string path) => path.Length > 0 &&
        !path.Any(character => char.IsControl(character) || character is '\\' or '?' or '#' or '%' or ':') &&
        path.Split('/').All(part => part.Length > 0 && part is not "." and not ".." &&
            !part.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
            part.ToLowerInvariant() is not (".git" or ".venv" or ".cache" or ".pytest_cache" or ".mypy_cache" or "__pycache__" or "node_modules"));

    private static bool CodePath(string path, string profile)
    {
        if (RuntimeHelpers.Contains(path, StringComparer.Ordinal) || path is "LICENSE" or "NOTICE.txt") return true;
        if (profile is "ltx" or "ltx_i2v" && path == "azureml/bootstrap_ltx_and_run.py") return true;
        if (!path.StartsWith("src/", StringComparison.Ordinal)) return false;
        var parts = path.Split('/');
        return parts[1].ToLowerInvariant() is not ("models" or "input" or "output" or "outputs" or "user") &&
            System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".safetensors" or ".ckpt" or ".pt" or ".pth" or ".onnx" or ".gguf");
    }

    public static void Release(RegionalRelease value, StudioFoundation foundation, GpuBuildManifest build)
    {
        Build(build);
        Require(build.Profile == "wan", "Hosted regional release bindings currently support the WAN runtime only.");
        Require(value.SchemaVersion == 1 && value.Region is not null && foundation.Regions.ContainsKey(value.Region),
            "Release region/version is not part of the selected foundation.");
        var region = foundation.Regions[value.Region];
        Require(value.WorkspaceId == region.WorkspaceId && value.ScopeFingerprint == StudioContracts.RegionFingerprint(foundation, value.Region) &&
            value.ArtifactId == build.ArtifactId, "Release is bound to a different workspace, identity, store or build.");
        var prefix = $"{region.WorkspaceId}/environments/";
        Require(value.EnvironmentId is not null && value.EnvironmentId.StartsWith(prefix, StringComparison.Ordinal) &&
            Regex.IsMatch(value.EnvironmentId[prefix.Length..], @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*/versions/[a-zA-Z0-9][a-zA-Z0-9_.-]*\z"),
            "Environment must be an exact version in the selected workspace.");
        Require(value.ModelsRef is not null && Regex.IsMatch(value.ModelsRef, @"^azureml:[a-zA-Z0-9][a-zA-Z0-9_.-]*:[1-9][0-9]*\z"),
            "Registered models require a positive integer version.");
        Require(DatastorePath(value.CodeUri, region.ReleaseDatastore, "code/") &&
            DatastorePath(value.ModelsUri, region.ReleaseDatastore, "models/"), "Release code/models must use the private release datastore.");
        var registry = StudioResourceNames.NameOf(region.RegistryId) + ".azurecr.io/";
        Require(value.Image is not null && value.Image.StartsWith(registry, StringComparison.Ordinal) &&
            Regex.IsMatch(value.Image[registry.Length..], @"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*@sha256:[a-f0-9]{64}\z") &&
            value.Image.EndsWith("@" + build.ImageDigest, StringComparison.Ordinal), "Release image must use this registry and the build's immutable digest.");
        Utc(value.VerifiedAt);
        Require(value.VerifiedAt != default && Uuid(value.VerifiedBy), "Regional verification needs a UTC timestamp and publisher principal.");
    }

    internal static bool DatastorePath(string? uri, string datastore, string prefix)
    {
        var start = $"azureml://datastores/{datastore}/paths/{prefix}";
        return uri is not null && uri.StartsWith(start, StringComparison.Ordinal) &&
            uri.EndsWith('/') && Path(uri[start.Length..^1]);
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,40}\z")]
    private static partial Regex TrainerName();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,30}\z")]
    private static partial Regex Preset();
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*(\.[a-z_][a-z0-9_]*)*\z")]
    private static partial Regex PythonModule();
    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._+-]{0,39}\z")]
    private static partial Regex TrainerVersion();

    public static void Trainer(TrainerBinding value, StudioFoundation foundation)
    {
        Require(value.SchemaVersion == 1 && value.Region is not null && foundation.Regions.ContainsKey(value.Region),
            "Trainer binding region/version is not part of the selected foundation.");
        var region = foundation.Regions[value.Region];
        Require(value.WorkspaceId == region.WorkspaceId && value.ScopeFingerprint == StudioContracts.RegionFingerprint(foundation, value.Region) &&
            value.ArtifactId is not null && Sha256().IsMatch(value.ArtifactId), "Trainer binding is bound to a different workspace, scope or build.");
        Require(value.Name is not null && TrainerName().IsMatch(value.Name) && value.Version is not null && TrainerVersion().IsMatch(value.Version) &&
            value.Module is not null && value.Module.Length <= 120 && PythonModule().IsMatch(value.Module),
            "Trainer name, version or module is invalid.");
        Require(value.Presets is { Length: > 0 and <= 16 } && value.Presets.All(item => item is not null && Preset().IsMatch(item)) &&
            value.Presets.Distinct(StringComparer.Ordinal).Count() == value.Presets.Length && value.DefaultPreset is not null && value.Presets.Contains(value.DefaultPreset),
            "Trainer presets are invalid or do not include the default.");
        Require(Text(value.LicenseCode) && Text(value.LicenseWeights), "Trainer licence text must be 1-300 printable characters.");
        Require(value.SourceSha is not null && Revision().IsMatch(value.SourceSha) && Digest(value.ImageDigest), "Trainer source revision or image digest is invalid.");
        var prefix = $"{region.WorkspaceId}/environments/";
        Require(value.EnvironmentId is not null && value.EnvironmentId.StartsWith(prefix, StringComparison.Ordinal) &&
            Regex.IsMatch(value.EnvironmentId[prefix.Length..], @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*/versions/[a-zA-Z0-9][a-zA-Z0-9_.-]*\z"),
            "Trainer environment must be an exact version in the selected workspace.");
        var registry = StudioResourceNames.NameOf(region.RegistryId) + ".azurecr.io/";
        Require(value.Image is not null && value.Image.StartsWith(registry, StringComparison.Ordinal) &&
            Regex.IsMatch(value.Image[registry.Length..], @"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*@sha256:[a-f0-9]{64}\z") &&
            value.Image.EndsWith("@" + value.ImageDigest, StringComparison.Ordinal), "Trainer image must use this registry and its immutable digest.");
        Utc(value.VerifiedAt);
        Require(value.VerifiedAt != default && Uuid(value.VerifiedBy), "Trainer verification needs a UTC timestamp and publisher principal.");
    }

    private static bool Text(string? value) => value is { Length: > 0 and <= 300 } && value.All(character => !char.IsControl(character));

    public static void Settings(StudioSettings value, StudioFoundation foundation)
    {
        Require(value.SchemaVersion == 1 && value.Version >= 1 && value.ScopeFingerprint == StudioContracts.ScopeFingerprint(foundation),
            "Settings version/scope differs from this foundation.");
        Require(value.Regions is { Count: > 0 } &&
            value.Regions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(foundation.Regions.Keys),
            "Settings must cover exactly the configured regions.");
        long capacity = 0;
        foreach (var (region, settings) in value.Regions)
        {
            var policy = foundation.Regions[region].Policy;
            Require(settings is not null && settings.MaxConcurrentJobs >= 1 && settings.MaxConcurrentJobs <= policy.MaxNodes &&
                settings.MaxJobMinutes >= 1 && settings.MaxJobMinutes <= policy.MaxJobMinutes && settings.Priority >= 0,
                "Regional settings exceed the deployment's node or timeout policy.");
            capacity += settings.MaxConcurrentJobs;
        }
        Require(value.MaxConcurrentJobs >= 1 && value.MaxConcurrentJobs <= capacity &&
            value.QueueMoveMinutes is >= 1 and <= 120, "Global admission or queue-wait policy is outside its allowed range.");
    }

    public static void Job(StudioJobRecord value, StudioFoundation foundation)
    {
        Require(value.SchemaVersion == 1 && value.Version >= 1 && value.Scope == foundation.Scope &&
            value.Name is not null && JobName().IsMatch(value.Name) && value.Attempts is { Length: > 0 } &&
            value.Attempts.All(attempt => attempt is not null),
            "Invalid scoped, versioned job attempt record.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < value.Attempts.Length; index++)
        {
            var attempt = value.Attempts[index];
            Require(attempt is not null && attempt.Number == index + 1 && attempt.Region is not null &&
                foundation.Regions.ContainsKey(attempt.Region) && visited.Add(attempt.Region),
                "Attempts require ordered identities and may visit each configured region only once.");
            var region = foundation.Regions[attempt.Region];
            Require(attempt.WorkspaceId == region.WorkspaceId && attempt.RemoteName is not null &&
                JobName().IsMatch(attempt.RemoteName) && identities.Add($"{attempt.WorkspaceId}/{attempt.RemoteName}") &&
                attempt.ArtifactId is not null && Sha256().IsMatch(attempt.ArtifactId) &&
                attempt.TimeoutSeconds >= 1 && attempt.TimeoutSeconds <= region.Policy.MaxJobMinutes * 60 &&
                Enum.IsDefined(attempt.Phase), "Attempt workspace, remote identity, artifact or timeout is invalid.");
            Require(attempt.OutputUri == $"azureml://datastores/{region.Datastore}/paths/video-library/{attempt.RemoteName}/",
                "Attempt output must be the remote job's exact regional private output folder.");
            Utc(attempt.CreatedAt);
            Utc(attempt.UpdatedAt);
            Require(attempt.CreatedAt != default && attempt.UpdatedAt >= attempt.CreatedAt, "Attempt timestamps must be ordered UTC values.");
            if (attempt.StartedAt is { } started)
            {
                Utc(started);
                Require(started >= attempt.CreatedAt && started <= attempt.UpdatedAt, "Execution start must be within the recorded attempt lifetime.");
            }
            var terminal = attempt.Phase is AttemptPhase.Completed or AttemptPhase.Failed or AttemptPhase.Canceled;
            Require(terminal == (attempt.TerminalVerifiedAt is not null), "Terminal attempts require a fresh verification timestamp; live attempts cannot claim termination.");
            if (attempt.TerminalVerifiedAt is { } verified)
            {
                Utc(verified);
                Require(verified >= attempt.CreatedAt && verified <= attempt.UpdatedAt, "Terminal verification must be within the recorded attempt lifetime.");
            }
            if (index < value.Attempts.Length - 1)
            {
                Require(attempt.Phase == AttemptPhase.Canceled && attempt.StartedAt is null &&
                    attempt.TerminalVerifiedAt <= value.Attempts[index + 1].CreatedAt,
                    "Only a verified, canceled, unstarted attempt may precede a queue move; completed/unknown/running work must not be replayed.");
            }
        }
    }

    public static void State(StudioOperationalState value, StudioFoundation foundation)
    {
        Require(value.SchemaVersion == 1 && value.Version >= 1 && value.ScopeFingerprint == StudioContracts.ScopeFingerprint(foundation) &&
            value.Regions is not null && value.Regions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(foundation.Regions.Keys),
            "Operational state is bound to a different scope or region set.");
        foreach (var operation in value.Regions.Values)
        {
            Require(operation is not null && Enum.IsDefined(operation.Phase) && operation.OperationId is not null &&
                (operation.OperationId.Length == 0 || Uuid(operation.OperationId)) &&
                (operation.SelectedArtifactId is null || Sha256().IsMatch(operation.SelectedArtifactId)) &&
                (operation.ArmedArtifactId is null || Sha256().IsMatch(operation.ArmedArtifactId)) &&
                (operation.Detail is null || operation.Detail.Length <= 500), "Invalid regional operation record.");
            Utc(operation.UpdatedAt);
            var active = operation.Phase is RegionPhase.Starting or RegionPhase.Stopping or RegionPhase.Armed;
            Require(!active || operation.OperationId.Length > 0, "An active operational phase needs its operation identity.");
            Require((operation.Phase == RegionPhase.Armed) == (operation.ArmedArtifactId is not null),
                "Only an armed region records the release it was armed with.");
        }
    }

    private static void Utc(DateTimeOffset value) => Require(value.Offset == TimeSpan.Zero, "Contract timestamps must be UTC.");
}
