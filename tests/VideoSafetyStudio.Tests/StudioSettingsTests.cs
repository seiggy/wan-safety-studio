using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;

namespace VideoSafetyStudio.Tests;

public sealed class StudioSettingsTests
{
    private static StudioSettings With(StudioSettings value, string region, Func<RegionSettings, RegionSettings> change) =>
        value with { Regions = value.Regions.ToDictionary(pair => pair.Key, pair => pair.Key == region ? change(pair.Value) : pair.Value, StringComparer.Ordinal) };

    private static int AuditCount(StudioRig rig, string result) =>
        rig.Store.Audit.Count(item => item.Key.Contains("-settings.update-", StringComparison.Ordinal) && item.Value.Contains($"\"result\":\"{result}\"", StringComparison.Ordinal));

    [Fact]
    public async Task A_valid_regional_timeout_is_saved_with_the_next_version_audited_and_applied_by_the_router_source()
    {
        var rig = await StudioRig.CreateAsync();
        var view = await rig.Lifecycle.SettingsAsync(default);
        Assert.False(view.Stored);
        Assert.Equal(StudioContracts.MaxJobMinutes, view.AbsoluteMaxJobMinutes);
        var region = rig.Foundation.ControlRegion;
        Assert.Equal(rig.Region(region).Policy.MaxJobMinutes, view.Policy[region].MaxJobMinutes);

        var saved = await rig.Lifecycle.UpdateSettingsAsync(StudioRig.Actor, view.Settings.Version, With(view.Settings, region, item => item with { MaxJobMinutes = 30 }), default);

        Assert.Equal(view.Settings.Version + 1, saved.Version);
        Assert.Equal(30, StudioContracts.ReadSettings(rig.Store.SettingsJson!, rig.Foundation).Regions[region].MaxJobMinutes);
        var source = new StudioSettingsSource(rig.Foundation, rig.Store, Microsoft.Extensions.Logging.Abstractions.NullLogger<StudioSettingsSource>.Instance);
        await source.RefreshAsync(default);
        Assert.Equal(30, source.Current.Regions[region].MaxJobMinutes);
        var audit = Assert.Single(rig.Store.Audit, item => item.Key.Contains("-settings.update-", StringComparison.Ordinal)).Value;
        Assert.Contains(StudioRig.Actor, audit);
        Assert.Contains("\"before\"", audit);
        Assert.Contains("\"after\"", audit);
        Assert.Equal(1, AuditCount(rig, "ok"));
    }

    [Fact]
    public async Task A_stale_version_or_lost_race_is_a_conflict_that_changes_nothing()
    {
        var rig = await StudioRig.CreateAsync();
        var view = await rig.Lifecycle.SettingsAsync(default);
        var region = rig.Foundation.ControlRegion;
        await rig.Lifecycle.UpdateSettingsAsync("a", view.Settings.Version, With(view.Settings, region, item => item with { MaxJobMinutes = 40 }), default);
        var after = rig.Store.SettingsJson;

        var stale = await Assert.ThrowsAsync<ConflictException>(() =>
            rig.Lifecycle.UpdateSettingsAsync("b", view.Settings.Version, With(view.Settings, region, item => item with { MaxJobMinutes = 50 }), default));
        Assert.Contains("Reload", stale.Message);
        Assert.Equal(after, rig.Store.SettingsJson);
        Assert.Equal(1, AuditCount(rig, "rejected"));

        var current = (await rig.Lifecycle.SettingsAsync(default)).Settings;
        rig.Store.BeforeSettingsWrite = () => { rig.Store.BeforeSettingsWrite = null; rig.Store.BumpSettings(); }; // another writer wins between read and write
        await Assert.ThrowsAsync<ConflictException>(() =>
            rig.Lifecycle.UpdateSettingsAsync("c", current.Version, With(current, region, item => item with { MaxJobMinutes = 20 }), default));
    }

    [Fact]
    public async Task Requests_above_a_deployed_ceiling_are_rejected_with_the_limit_never_clamped()
    {
        var rig = await StudioRig.CreateAsync();
        var view = await rig.Lifecycle.SettingsAsync(default);
        var region = rig.Foundation.ControlRegion;
        var policy = rig.Region(region).Policy;
        var before = rig.Store.SettingsJson;

        async Task<string> Rejected(StudioSettings requested) =>
            (await Assert.ThrowsAsync<ValidationException>(() => rig.Lifecycle.UpdateSettingsAsync("a", view.Settings.Version, requested, default))).Message;

        Assert.Contains($"1 to {policy.MaxJobMinutes} minutes", await Rejected(With(view.Settings, region, item => item with { MaxJobMinutes = policy.MaxJobMinutes + 1 })));
        Assert.Contains("job timeout", await Rejected(With(view.Settings, region, item => item with { MaxJobMinutes = 0 })));
        Assert.Contains($"1 to {policy.MaxNodes}", await Rejected(With(view.Settings, region, item => item with { MaxConcurrentJobs = policy.MaxNodes + 1 })));
        Assert.Contains("priority", await Rejected(With(view.Settings, region, item => item with { Priority = -1 })));
        Assert.Contains("Global concurrent jobs", await Rejected(view.Settings with { MaxConcurrentJobs = view.Settings.Regions.Values.Sum(item => item.MaxConcurrentJobs) + 1 }));
        Assert.Contains("Queue wait", await Rejected(view.Settings with { QueueMoveMinutes = 121 }));
        Assert.Contains("exactly the deployed regions", await Rejected(view.Settings with { Regions = new Dictionary<string, RegionSettings>(view.Settings.Regions) { ["other"] = new(1, 1, 9) } }));
        Assert.Contains("required", await Rejected(view.Settings with { Regions = null! }));

        Assert.Equal(before, rig.Store.SettingsJson);
        Assert.Equal(8, AuditCount(rig, "rejected"));
    }

    [Fact]
    public async Task An_invalid_stored_document_is_reported_and_can_be_replaced_from_the_deployed_defaults()
    {
        var rig = await StudioRig.CreateAsync();
        rig.Store.SettingsJson = "{ not valid";
        var view = await rig.Lifecycle.SettingsAsync(default);
        Assert.True(view.StoredInvalid);
        Assert.Equal(StudioSettingsSource.Defaults(rig.Foundation).Version, view.Settings.Version);

        await rig.Lifecycle.UpdateSettingsAsync("a", view.Settings.Version, view.Settings, default);

        Assert.False((await rig.Lifecycle.SettingsAsync(default)).StoredInvalid);
    }
}
