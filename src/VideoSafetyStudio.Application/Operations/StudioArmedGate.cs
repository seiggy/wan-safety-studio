namespace VideoSafetyStudio.Application.Operations;

/// <summary>
/// What the submission guard reads. A region is armed only while a recent, validated durable snapshot says so; a missing,
/// stale or fenced snapshot means no generation, so an unavailable control store or host restart fails closed.
/// </summary>
public sealed class StudioArmedGate(TimeProvider time, TimeSpan maxAge)
{
    private sealed record Snapshot(StudioOperationalState State, DateTimeOffset ReadAt);

    private volatile Snapshot? snapshot;
    private volatile Dictionary<string, string> regionByWorkspace = new(StringComparer.OrdinalIgnoreCase);

    public void Bind(StudioFoundation foundation) =>
        regionByWorkspace = foundation.Regions.ToDictionary(pair => pair.Value.WorkspaceId, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    public void Update(StudioOperationalState state) => snapshot = new Snapshot(state, time.GetUtcNow());

    /// <summary>Immediately stops all submissions; the next durable read restores only what the store still proves armed.</summary>
    public void Fence() => snapshot = null;

    public bool IsArmed(string region) => snapshot is { } current && time.GetUtcNow() - current.ReadAt <= maxAge &&
        current.State.Regions.TryGetValue(region, out var operation) && operation.Phase == RegionPhase.Armed;

    /// <summary>The release a region is armed with; null unless it is currently armed by a fresh durable snapshot.</summary>
    public string? ArmedArtifact(string region) => IsArmed(region) ? snapshot!.State.Regions[region].ArmedArtifactId : null;

    /// <summary>True when the region has an approved release selected (whether or not it is armed).</summary>
    public bool HasSelection(string region) => snapshot is { } current && time.GetUtcNow() - current.ReadAt <= maxAge &&
        current.State.Regions.TryGetValue(region, out var operation) && operation.SelectedArtifactId is not null;

    public bool IsWorkspaceArmed(string workspaceId) => regionByWorkspace.TryGetValue(workspaceId, out var region) && IsArmed(region);
}
