using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

public sealed record RegionPolicyView(int MaxNodes, int MaxJobMinutes);

/// <summary>The effective settings with the deployed ceilings they must stay within; <see cref="Settings"/>.Version is the update token.</summary>
public sealed record StudioSettingsView(
    StudioSettings Settings, bool Stored, bool StoredInvalid, Dictionary<string, RegionPolicyView> Policy, int AbsoluteMaxJobMinutes);

public sealed partial class StudioLifecycle
{
    private async Task<(StudioSettings Settings, string? ETag, bool Stored, bool Invalid)> LoadSettingsAsync(CancellationToken ct)
    {
        if (await store.ReadSettingsAsync(ct) is not { } stored)
        {
            return (StudioSettingsSource.Defaults(foundation), null, false, false);
        }

        try
        {
            return (StudioContracts.ReadSettings(stored.Json, foundation), stored.ETag, true, false);
        }
        catch (ValidationException)
        {
            // The router keeps its last valid document; an administrator may replace this one, never silently.
            return (StudioSettingsSource.Defaults(foundation), stored.ETag, true, true);
        }
    }

    public async Task<StudioSettingsView> SettingsAsync(CancellationToken ct)
    {
        var (settings, _, stored, invalid) = await LoadSettingsAsync(ct);
        return new StudioSettingsView(settings, stored, invalid,
            foundation.Regions.ToDictionary(pair => pair.Key, pair => new RegionPolicyView(pair.Value.Policy.MaxNodes, pair.Value.Policy.MaxJobMinutes), StringComparer.Ordinal),
            StudioContracts.MaxJobMinutes);
    }

    /// <summary>
    /// Replaces the settings when <paramref name="expectedVersion"/> is still current. Anything above a deployed ceiling is rejected
    /// with the exact limit, never clamped, and every outcome is audited with the old and requested values.
    /// </summary>
    public async Task<StudioSettings> UpdateSettingsAsync(string actor, long expectedVersion, StudioSettings requested, CancellationToken ct)
    {
        var (current, etag, _, _) = await LoadSettingsAsync(ct);
        try
        {
            if (expectedVersion != current.Version)
            {
                throw new ConflictException($"The settings changed (now version {current.Version}). Reload and apply your change again.");
            }

            var next = requested with { SchemaVersion = 1, Version = current.Version + 1, ScopeFingerprint = StudioContracts.ScopeFingerprint(foundation) };
            CheckSettings(next);
            var json = StudioContracts.Write(next);
            _ = StudioContracts.ReadSettings(json, foundation);
            try
            {
                await store.WriteSettingsAsync(json, etag, ct);
            }
            catch (StudioStateConflictException)
            {
                throw new ConflictException("The settings changed concurrently. Reload and apply your change again.");
            }

            await AuditAsync("settings.update", actor, [.. next.Regions.Keys], "ok", new { before = current, after = next }, CancellationToken.None);
            return next;
        }
        catch (Exception error) when (error is ValidationException or ConflictException)
        {
            await AuditAsync("settings.update", actor, [], "rejected", new { before = current, requested = requested.Regions is null ? null : requested, reason = error.Message }, CancellationToken.None);
            throw;
        }
    }

    private void CheckSettings(StudioSettings value)
    {
        if (value.Regions is null)
        {
            throw new ValidationException("Regional settings are required.");
        }

        var unknown = value.Regions.Keys.Except(foundation.Regions.Keys).ToList();
        var missing = foundation.Regions.Keys.Except(value.Regions.Keys).ToList();
        if (unknown.Count > 0 || missing.Count > 0)
        {
            throw new ValidationException($"Settings must cover exactly the deployed regions ({string.Join(", ", foundation.Regions.Keys.Order(StringComparer.Ordinal))}).");
        }

        var capacity = 0;
        foreach (var (key, region) in value.Regions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var policy = foundation.Regions[key].Policy;
            if (region is null || region.MaxConcurrentJobs < 1 || region.MaxConcurrentJobs > policy.MaxNodes)
            {
                throw new ValidationException($"{key}: concurrent jobs must be 1 to {policy.MaxNodes} (the deployed node limit).");
            }

            if (region.MaxJobMinutes < 1 || region.MaxJobMinutes > policy.MaxJobMinutes)
            {
                throw new ValidationException($"{key}: job timeout must be 1 to {policy.MaxJobMinutes} minutes (the deployed limit, never above {StudioContracts.MaxJobMinutes}).");
            }

            if (region.Priority < 0)
            {
                throw new ValidationException($"{key}: priority must be 0 or higher (lower is tried first).");
            }

            capacity += region.MaxConcurrentJobs;
        }

        if (value.MaxConcurrentJobs < 1 || value.MaxConcurrentJobs > capacity)
        {
            throw new ValidationException($"Global concurrent jobs must be 1 to {capacity} (the sum of the regional limits).");
        }

        if (value.QueueMoveMinutes is < 1 or > 120)
        {
            throw new ValidationException("Queue wait before moving must be 1 to 120 minutes.");
        }
    }
}
