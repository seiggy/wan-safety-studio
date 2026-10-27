namespace VideoSafetyStudio.Application.Operations;

/// <summary>Durable per-region submission fence. Only <see cref="Armed"/> permits generation; everything else is fenced.</summary>
public enum RegionPhase { Off, Starting, Armed, Stopping, Unknown }

public sealed record RegionOperation(
    RegionPhase Phase, string OperationId, string? SelectedArtifactId, string? ArmedArtifactId, DateTimeOffset UpdatedAt, string? Detail);

public sealed record StudioOperationalState(
    int SchemaVersion, long Version, string ScopeFingerprint, Dictionary<string, RegionOperation> Regions)
{
    public static StudioOperationalState Initial(StudioFoundation foundation, DateTimeOffset now) => new(
        1, 1, StudioContracts.ScopeFingerprint(foundation),
        foundation.Regions.Keys.ToDictionary(region => region, _ => new RegionOperation(RegionPhase.Off, "", null, null, now, null), StringComparer.Ordinal));

    public StudioOperationalState With(string region, RegionOperation operation) => this with
    {
        Version = Version + 1,
        Regions = new Dictionary<string, RegionOperation>(Regions, StringComparer.Ordinal) { [region] = operation },
    };
}
