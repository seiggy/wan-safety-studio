using Microsoft.Extensions.Logging;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Application.Operations;

/// <summary>Global and per-region concurrency limits for attempts that hold (or are about to hold) a remote job.</summary>
public sealed class RegionalAdmission
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> active = new(StringComparer.Ordinal);
    private int total;

    public int Total { get { lock (gate) return total; } }

    public int Active(string region) { lock (gate) return active.GetValueOrDefault(region); }

    /// <summary>Slots left right now: bounded by both the regional and the global limit.</summary>
    public int Headroom(string region, int regionLimit, int globalLimit)
    {
        lock (gate) return Math.Max(0, Math.Min(regionLimit - active.GetValueOrDefault(region), globalLimit - total));
    }

    public bool TryAcquire(string region, int regionLimit, int globalLimit)
    {
        lock (gate)
        {
            if (Math.Min(regionLimit - active.GetValueOrDefault(region), globalLimit - total) <= 0) return false;
            Add(region);
            return true;
        }
    }

    /// <summary>Existing remote work is counted unconditionally: lowering a limit never terminates or hides it.</summary>
    public void Force(string region)
    {
        lock (gate) Add(region);
    }

    public void Release(string region)
    {
        lock (gate)
        {
            if (active.GetValueOrDefault(region) <= 0) return;
            active[region]--;
            total--;
        }
    }

    private void Add(string region)
    {
        active[region] = active.GetValueOrDefault(region) + 1;
        total++;
    }
}

public sealed record RegionCandidate(string Region, int Priority, int Headroom);

/// <summary>Why each region is or is not usable right now. Busy regions are eligible but full; nothing is submitted to them.</summary>
public sealed record RegionPlan(IReadOnlyList<RegionCandidate> Candidates, IReadOnlyList<string> Busy, IReadOnlyDictionary<string, string> Ineligible)
{
    public bool AnyEligible => Candidates.Count > 0 || Busy.Count > 0;

    public string Explain() => Ineligible.Count == 0 ? "No region is configured." :
        string.Join(" ", Ineligible.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"));
}

/// <summary>
/// Ranks regions. Armed + prepared + allowed is eligibility, not capacity: quota/SKU/price do not say a Spot node will be granted.
/// A lower priority number is tried first; among equals the region with more headroom wins, then the name.
/// </summary>
public static class RegionalPlanner
{
    public static RegionPlan Plan(
        StudioFoundation foundation, StudioSettings settings, Func<string, bool> armed, Func<string, bool> prepared,
        RegionalAdmission admission, IReadOnlySet<string> visited)
    {
        var candidates = new List<RegionCandidate>();
        var busy = new List<string>();
        var ineligible = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var region in foundation.Regions.Keys.Order(StringComparer.Ordinal))
        {
            var regional = settings.Regions[region];
            if (visited.Contains(region)) ineligible[region] = "already tried for this job.";
            else if (!armed(region)) ineligible[region] = "not armed.";
            else if (!prepared(region)) ineligible[region] = "no verified release.";
            else if (regional.MaxConcurrentJobs < 1) ineligible[region] = "disabled by settings.";
            else
            {
                var headroom = admission.Headroom(region, regional.MaxConcurrentJobs, settings.MaxConcurrentJobs);
                if (headroom > 0) candidates.Add(new RegionCandidate(region, regional.Priority, headroom));
                else busy.Add(region);
            }
        }

        return new RegionPlan(
            [.. candidates.OrderBy(item => item.Priority).ThenByDescending(item => item.Headroom).ThenBy(item => item.Region, StringComparer.Ordinal)],
            busy, ineligible);
    }
}

/// <summary>The settings the router uses: the durable document when valid, otherwise defaults derived from the deployed policy.</summary>
public sealed class StudioSettingsSource(StudioFoundation foundation, IStudioControlStore store, ILogger<StudioSettingsSource> logger)
{
    public const int DefaultQueueMoveMinutes = 10;

    private volatile StudioSettings current = Defaults(foundation);

    public StudioSettings Current => current;

    /// <summary>Within every deployed ceiling by construction; the control region is preferred, the rest ordered by name.</summary>
    public static StudioSettings Defaults(StudioFoundation foundation)
    {
        var order = foundation.Regions.Keys.OrderBy(key => key == foundation.ControlRegion ? 0 : 1).ThenBy(key => key, StringComparer.Ordinal).ToList();
        var regions = order.Select((key, index) => (key, new RegionSettings(foundation.Regions[key].Policy.MaxNodes, foundation.Regions[key].Policy.MaxJobMinutes, index)))
            .ToDictionary(pair => pair.key, pair => pair.Item2, StringComparer.Ordinal);
        return new StudioSettings(1, 1, StudioContracts.ScopeFingerprint(foundation), regions.Values.Sum(item => item.MaxConcurrentJobs), DefaultQueueMoveMinutes, regions);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var stored = await store.ReadSettingsAsync(ct);
        if (stored is null)
        {
            current = Defaults(foundation);
            return;
        }

        try
        {
            current = StudioContracts.ReadSettings(stored.Json, foundation);
        }
        catch (ValidationException error)
        {
            // Keep the last valid document: an invalid edit can never raise a ceiling or widen a timeout.
            logger.LogError(error, "The stored studio settings are invalid; the previous valid settings stay in effect.");
        }
    }
}

/// <summary>Durable per-job attempt history. One writer per job (its worker); every save is validated against the cached scope first.</summary>
public sealed class AttemptLedger(IObjectStore store, StudioFoundation foundation)
{
    public const string Prefix = "video-library-attempts/";

    public async Task<StudioJobRecord?> LoadAsync(string name, CancellationToken ct)
    {
        var data = await store.ReadAsync($"{Prefix}{name}.json", ct);
        return data is null ? null : StudioContracts.ReadJob(System.Text.Encoding.UTF8.GetString(data), foundation);
    }

    public async Task<StudioJobRecord> SaveAsync(StudioJobRecord record, CancellationToken ct)
    {
        var next = record with { Version = record.Version + 1 };
        var json = StudioContracts.Write(next);
        _ = StudioContracts.ReadJob(json, foundation); // never persist a history this host would later refuse
        await store.WriteAsync($"{Prefix}{record.Name}.json", System.Text.Encoding.UTF8.GetBytes(json), true, ct);
        return next;
    }
}
