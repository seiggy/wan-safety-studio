using System.Text.Json;
using System.Text.Json.Nodes;
using VideoSafetyStudio.Application.Operations;

if (args.Length is < 1 or > 3)
    throw new ArgumentException("Usage: Contracts <studio-settings.json> [build-manifest [regional-binding]] | Contracts <studio-settings.json> --trainer <trainer-binding>");

// The settings document is shaped like appsettings.json: { "Studio": { ... } }. Unknown keys are rejected here so a typo cannot pass in CI.
var parsed = JsonSerializer.Deserialize<StudioSettingsDocument>(File.ReadAllText(args[0]),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
    ?? throw new ArgumentException("The studio settings document is empty.");
var foundation = StudioOptionsMapper.ToFoundation(parsed.Studio ?? throw new ArgumentException("The document needs a Studio section."));
// Pipeline-only values (not read by the web host): the identity allowed to publish builds, the shared App Service the server image is deployed to,
// and per region the workspace identity and key vault that publishing verifies the workspace is wired to.
if (parsed.Pipeline is { } pipeline)
{
    foreach (var (region, item) in pipeline.Regions ?? [])
    {
        if (!foundation.Regions.ContainsKey(region)) throw new ArgumentException($"Pipeline:Regions:{region} is not a region under Studio:Regions.");
        if (item.WorkspaceIdentityId is not null && !item.WorkspaceIdentityId.Contains("/providers/Microsoft.ManagedIdentity/userAssignedIdentities/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pipeline:Regions:{region}:WorkspaceIdentityId must be a user-assigned identity ARM ID.");
        if (item.KeyVaultId is not null && !item.KeyVaultId.Contains("/providers/Microsoft.KeyVault/vaults/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Pipeline:Regions:{region}:KeyVaultId must be a key vault ARM ID.");
    }

    if (pipeline.PublisherPrincipalId is not null && !Guid.TryParseExact(pipeline.PublisherPrincipalId, "D", out _))
        throw new ArgumentException("Pipeline:PublisherPrincipalId must be a canonical GUID.");
    if (pipeline.SiteId is not null && !System.Text.RegularExpressions.Regex.IsMatch(pipeline.SiteId, @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Web/sites/[^/]+$"))
        throw new ArgumentException("Pipeline:SiteId must be an App Service (Microsoft.Web/sites) ARM ID.");
}
var trainerMode = args.Length == 3 && args[1] == "--trainer";
var build = args.Length >= 2 && !trainerMode ? StudioContracts.ReadBuild(File.ReadAllText(args[1])) : null;
var release = args.Length == 3 && !trainerMode ? StudioContracts.ReadRelease(File.ReadAllText(args[2]), foundation, build!) : null;
var trainer = trainerMode ? StudioContracts.ReadTrainer(File.ReadAllText(args[2]), foundation) : null;
var output = JsonNode.Parse(StudioContracts.Write(new
{
    foundation,
    build,
    release,
    trainer,
    scopeFingerprints = foundation.Regions.ToDictionary(p => p.Key, p => StudioContracts.RegionFingerprint(foundation, p.Key)),
}))!;
// Publishing needs them; they stay absent (and the publish stage names the missing Pipeline setting) when not supplied.
foreach (var (region, item) in parsed.Pipeline?.Regions ?? [])
{
    var target = output["foundation"]!["regions"]![region]!.AsObject();
    if (item.WorkspaceIdentityId is not null) target["workspaceIdentityId"] = item.WorkspaceIdentityId;
    if (item.KeyVaultId is not null) target["keyVaultId"] = item.KeyVaultId;
}

Console.WriteLine(output.ToJsonString(StudioContracts.Options));

internal sealed record StudioSettingsDocument(StudioOptions? Studio, PipelineSettings? Pipeline = null);

internal sealed record PipelineSettings(string? PublisherPrincipalId, string? SiteId, Dictionary<string, PipelineRegion>? Regions = null);

internal sealed record PipelineRegion(string? WorkspaceIdentityId, string? KeyVaultId);