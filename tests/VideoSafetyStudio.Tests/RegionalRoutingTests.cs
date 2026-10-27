using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

internal sealed class MemStore : IObjectStore
{
    public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Data.ContainsKey(key));

    public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult(Data.TryGetValue(key, out var value) ? value : null);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Data.TryGetValue(key, out var value) ? new MemoryStream(value) : null);

    public Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        if (!overwrite && Data.ContainsKey(key)) throw new IOException("exists");
        Data[key] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default) => WriteAsync(key, File.ReadAllBytes(path), overwrite, ct);

    public Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        File.WriteAllBytes(path, Data[key]);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Data.Keys.Where(key => key.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);
}

internal enum CancelMode { Works, Ignored, Throws }

/// <summary>One regional workspace: jobs sit in the status a test gives them and write their output only when Completed.</summary>
internal sealed class RegionAml(MemStore store, RegionalFoundation region) : IAzureMlClient
{
    public List<AzureMlJobSpec> Created { get; } = [];
    public List<string> Cancelled { get; } = [];
    public Dictionary<string, string> Status { get; } = [];
    public HashSet<string> Preexisting { get; } = [];
    public CancelMode Cancel { get; set; } = CancelMode.Works;
    public string StatusAfterCreate { get; set; } = "Queued";
    public bool WriteOutput { get; set; } = true;
    private readonly HashSet<string> written = [];

    public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct)
    {
        Created.Add(spec);
        Status[spec.Name] = StatusAfterCreate;
        return Task.FromResult(Stored(spec.Name));
    }

    public async Task<JsonNode> GetJobAsync(string name, CancellationToken ct)
    {
        if (!Status.ContainsKey(name)) throw new AzureMlException("not found", System.Net.HttpStatusCode.NotFound);
        if (Status[name] == "Completed" && WriteOutput && written.Add(name))
        {
            await store.WriteAsync($"video-library/{name}/ComfyUI_00001_.mp4", new byte[] { 1, 2, 3 }, true, ct);
        }

        return Stored(name);
    }

    private JsonNode Stored(string name)
    {
        var timeout = Created.LastOrDefault(spec => spec.Name == name)?.TimeoutSeconds ?? 7200;
        return new JsonObject
        {
            ["name"] = name,
            ["properties"] = new JsonObject
            {
                ["status"] = Status[name], ["experimentName"] = "comfyui-wan", ["displayName"] = "d", ["computeId"] = region.ComputeId,
                ["limits"] = new JsonObject { ["timeout"] = $"PT{timeout}S" }, ["resources"] = new JsonObject { ["instanceCount"] = 1 },
            },
        };
    }

    public Task CancelJobAsync(string name, CancellationToken ct)
    {
        Cancelled.Add(name);
        if (Cancel == CancelMode.Throws) throw new AzureMlException("Azure ML POST failed (400)", System.Net.HttpStatusCode.BadRequest);
        if (Cancel == CancelMode.Works && Status.TryGetValue(name, out var state) && state is "Queued" or "NotStarted") Status[name] = "Canceled";
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonNode>>([.. Status.Keys.Select(Stored)]);

    public Task<string?> GetComputeStateAsync(CancellationToken ct) => Task.FromResult<string?>("Succeeded");
}

internal sealed class RegionalRig
{
    public required StudioRig Studio { get; init; }
    public required ReliabilityTimeProvider Clock { get; init; }
    public Dictionary<string, MemStore> Stores { get; } = [];
    public Dictionary<string, RegionAml> Aml { get; } = [];
    public RegionalAdmission Admission { get; } = new();
    public required StudioSettingsSource Settings { get; init; }
    public required AttemptLedger Ledger { get; init; }
    public required RegionalLibraryStore Library { get; init; }
    public required JobRecords Records { get; init; }
    public required Dictionary<string, RegionContext> Regions { get; init; }
    public MemStore Control => Stores[Studio.Foundation.ControlRegion];
    public RegionalAzureMlBackend Backend { get; private set; } = null!;

    public static async Task<RegionalRig> CreateAsync(params string[] armed)
    {
        var clock = new ReliabilityTimeProvider();
        var studio = await StudioRig.CreateAsync(time: clock);
        foreach (var key in armed) await studio.ArmedAsync(key);
        var regions = new Dictionary<string, RegionContext>();
        var stores = new Dictionary<string, MemStore>();
        var aml = new Dictionary<string, RegionAml>();
        foreach (var (key, region) in studio.Foundation.Regions)
        {
            var store = new MemStore();
            var client = new RegionAml(store, region);
            stores[key] = store;
            aml[key] = client;
            regions[key] = new RegionContext(key, region, RegionContext.Derive(studio.Foundation, region), client, store);
        }

        var control = stores[studio.Foundation.ControlRegion];
        var rig = new RegionalRig
        {
            Studio = studio, Clock = clock, Regions = regions,
            Settings = new StudioSettingsSource(studio.Foundation, studio.Store, NullLogger<StudioSettingsSource>.Instance),
            Ledger = new AttemptLedger(control, studio.Foundation),
            Library = new RegionalLibraryStore(control, stores.ToDictionary(pair => pair.Key, pair => (IObjectStore)pair.Value), studio.Foundation.ControlRegion),
            Records = new JobRecords(control, new FakeEvents(), NullLogger<JobRecords>.Instance),
        };
        foreach (var (key, value) in stores) rig.Stores[key] = value;
        foreach (var (key, value) in aml) rig.Aml[key] = value;
        rig.Backend = rig.NewBackend();
        return rig;
    }

    public RegionalAzureMlBackend NewBackend() => new(
        Studio.Foundation, Regions, new RegionalReleaseResolver(Studio.Foundation, Studio.Store, Studio.Gate), Studio.Gate, Settings, Admission, Ledger, Library,
        new FileWorkflowSource(TestPaths.Workflows), new FakeTelemetry(), Records, Options.Create(new GenerationOptions()),
        Options.Create(new AzureMlOptions { PollSeconds = 5, MaxUploadMb = 64 }), Clock, new RegionalTimings(TimeSpan.FromMinutes(2)), NullLogger<RegionalAzureMlBackend>.Instance);

    public GenerationJob Job(string name = "wan-abc123abc123") => new()
    {
        Name = name, DisplayName = "Scene", SubmittedAt = Clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, Mode = WanModes.DefaultId, Scene = 1, Take = 1,
    };

    public WorkItem Item(GenerationJob job) =>
        new(job, "a calm lake", new SceneImage("a.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0]), "blur", 5, 768, 768, WanModes.Get(null));

    public Task GenerateAsync(GenerationJob job, RegionalAzureMlBackend? backend = null, CancellationToken ct = default) =>
        (backend ?? Backend).GenerateAsync(Item(job), Clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0, ct);

    /// <summary>Drives the manual clock and the durable-snapshot refresh (what the host's refresh loop does) until the task ends.</summary>
    public async Task PumpAsync(Task task, Func<int, Task>? tick = null, int max = 6000)
    {
        for (var step = 0; step < max && !task.IsCompleted; step++)
        {
            Clock.Advance(TimeSpan.FromSeconds(5));
            await Studio.Lifecycle.RefreshAsync(default);
            await Settings.RefreshAsync(default);
            if (tick is not null) await tick(step);
            await Task.Delay(1);
        }

        Assert.True(task.IsCompleted, "The operation did not finish within the simulated time.");
        await task;
    }

    public async Task PumpOneAsync()
    {
        Clock.Advance(TimeSpan.FromSeconds(5));
        await Studio.Lifecycle.RefreshAsync(default);
        await Settings.RefreshAsync(default);
        await Task.Delay(1);
    }

    public async Task PumpUntilAsync(Func<bool> condition, int max = 6000)
    {
        for (var step = 0; step < max && !condition(); step++)
        {
            await PumpOneAsync();
        }

        Assert.True(condition(), "The condition was not reached within the simulated time.");
    }

    public async Task<StudioJobRecord> LedgerAsync(string name) => (await Ledger.LoadAsync(name, default))!;

    public void SetSettings(Func<StudioSettings, StudioSettings> change)
    {
        var next = change(Settings.Current) with { Version = Settings.Current.Version + 1 };
        Studio.Store.SettingsJson = StudioContracts.Write(next);
    }
}

public sealed class RegionalRoutingTests
{
    private const string East = "eastus2";
    private const string West = "westeurope";

    private static readonly TimeSpan MoveWait = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task Single_armed_region_completes_with_its_own_timeout_and_serves_output_from_that_region()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        var spec = Assert.Single(rig.Aml[East].Created);
        Assert.Empty(rig.Aml[West].Created);
        Assert.Equal(rig.Studio.Region(East).Policy.MaxJobMinutes * 60, spec.TimeoutSeconds);
        Assert.Contains($"--run-timeout {spec.TimeoutSeconds}", spec.Command);

        rig.Aml[East].Status[job.Name] = "Running";
        await rig.PumpUntilAsync(() => rig.Aml[East].Status[job.Name] == "Running" && rig.LedgerAsync(job.Name).Result.Attempts[0].StartedAt is not null);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);

        var ledger = await rig.LedgerAsync(job.Name);
        var attempt = Assert.Single(ledger.Attempts);
        Assert.Equal(AttemptPhase.Completed, attempt.Phase);
        Assert.NotNull(attempt.TerminalVerifiedAt);
        Assert.Equal(East, job.Region);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.DoesNotContain(rig.Stores[West].Data.Keys, key => key.StartsWith("video-library/", StringComparison.Ordinal));
        Assert.NotNull(await rig.Library.OpenReadAsync($"video-library/{job.Name}/ComfyUI_00001_.mp4"));
        Assert.Equal(0, rig.Admission.Total);
        Assert.Contains("a calm lake", System.Text.Encoding.UTF8.GetString(rig.Stores[East].Data[$"studio/web-inputs/{job.Name}/workflow.json"]));
    }

    [Fact]
    public async Task A_completed_job_without_output_fails_explicitly_and_is_neither_replayed_nor_left_holding_capacity()
    {
        var rig = await RegionalRig.CreateAsync(East);
        rig.Aml[East].WriteOutput = false;
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpUntilAsync(() => task.IsCompleted);

        var error = await Assert.ThrowsAsync<AzureMlException>(() => task);
        Assert.Contains("no video output", error.Message);
        // The ledger records the remote terminal state; only output collection failed, so a restart collects again and never creates a second job.
        Assert.Equal(AttemptPhase.Completed, (await rig.LedgerAsync(job.Name)).Attempts[0].Phase);
        Assert.DoesNotContain($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.Single(rig.Aml[East].Created);
        Assert.Empty(rig.Aml[West].Created);
        Assert.Equal(0, rig.Admission.Total);
    }

    [Fact]
    public async Task Job_still_queued_after_the_wait_moves_once_after_proven_cancellation_with_the_destination_timeout()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        rig.SetSettings(settings => settings with { Regions = new(settings.Regions) { [West] = settings.Regions[West] with { MaxJobMinutes = 15 } } });
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        Assert.Equal(rig.Studio.Region(East).Policy.MaxJobMinutes * 60, rig.Aml[East].Created[0].TimeoutSeconds);

        await rig.PumpUntilAsync(() => rig.Aml[West].Created.Count == 1);
        Assert.Equal([job.Name], rig.Aml[East].Cancelled);
        Assert.Equal("Canceled", rig.Aml[East].Status[job.Name]);
        var moved = rig.Aml[West].Created[0];
        Assert.Equal(15 * 60, moved.TimeoutSeconds);
        Assert.Contains("--run-timeout 900", moved.Command);
        Assert.Equal(job.Name, moved.Name);
        Assert.Contains($"studio/web-inputs/{job.Name}/a.png", rig.Stores[West].Data.Keys);

        var midway = await rig.LedgerAsync(job.Name);
        Assert.Equal([East, West], midway.Attempts.Select(item => item.Region));
        Assert.Equal(AttemptPhase.Canceled, midway.Attempts[0].Phase);
        Assert.Null(midway.Attempts[0].StartedAt);
        Assert.NotNull(midway.Attempts[0].TerminalVerifiedAt);
        Assert.True(midway.Attempts[0].TerminalVerifiedAt <= midway.Attempts[1].CreatedAt);
        Assert.True(rig.Admission.Active(West) == 1 && rig.Admission.Active(East) == 0);

        rig.Aml[West].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
        Assert.Equal(West, job.Region);
        Assert.Equal(AttemptPhase.Completed, (await rig.LedgerAsync(job.Name)).Attempts[1].Phase);
        Assert.Single(rig.Aml[East].Cancelled);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[West].Data.Keys);
        Assert.DoesNotContain($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.Equal(0, rig.Admission.Total);

        // The video is served from the region that produced it, whatever the current priorities say.
        rig.SetSettings(settings => settings with { Regions = new(settings.Regions) { [East] = settings.Regions[East] with { Priority = 0 }, [West] = settings.Regions[West] with { Priority = 9 } } });
        Assert.NotNull(await rig.Library.OpenReadAsync($"video-library/{job.Name}/ComfyUI_00001_.mp4"));
        Assert.Contains($"video-library/{job.Name}/meta.json", await rig.Library.ListAsync("video-library"));
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Preparing")]
    [InlineData("Running")]
    public async Task A_started_job_is_never_treated_as_unstarted_queue_work(string started)
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        rig.Aml[East].Status[job.Name] = started;
        await rig.PumpUntilAsync(() => rig.LedgerAsync(job.Name).Result.Attempts[0].StartedAt is not null);
        for (var elapsed = TimeSpan.Zero; elapsed < MoveWait + TimeSpan.FromMinutes(2); elapsed += TimeSpan.FromSeconds(5)) await rig.PumpAsyncStep();

        Assert.Empty(rig.Aml[East].Cancelled);
        Assert.Empty(rig.Aml[West].Created);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
        Assert.Equal(East, job.Region);
    }

    [Fact]
    public async Task Unproven_cancellation_blocks_movement_and_is_never_repeated()
    {
        foreach (var mode in new[] { CancelMode.Ignored, CancelMode.Throws })
        {
            var rig = await RegionalRig.CreateAsync(East, West);
            rig.Aml[East].Cancel = mode;
            var job = rig.Job();
            var task = rig.GenerateAsync(job);
            await rig.PumpUntilAsync(() => rig.Aml[East].Cancelled.Count == 1);
            await rig.PumpUntilAsync(() => rig.LedgerAsync(job.Name).Result.Attempts[0].Phase == AttemptPhase.Unknown);
            for (var step = 0; step < 150; step++) await rig.PumpAsyncStep();

            Assert.Single(rig.Aml[East].Cancelled);
            Assert.Empty(rig.Aml[West].Created);
            Assert.Equal(0, rig.Admission.Active(West));
            Assert.Equal(1, rig.Admission.Active(East));
            Assert.False(task.IsCompleted);

            rig.Aml[East].Status[job.Name] = "Completed";
            await rig.PumpAsync(task);
            Assert.Equal(East, job.Region);
            Assert.Single((await rig.LedgerAsync(job.Name)).Attempts);
        }
    }

    [Fact]
    public async Task A_job_that_completes_during_cancellation_is_collected_and_never_replayed()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        rig.Aml[East].Cancel = CancelMode.Ignored;
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Cancelled.Count == 1);
        Assert.Equal(1, rig.Admission.Active(West)); // the destination slot is only reserved, not used
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);

        Assert.Empty(rig.Aml[West].Created);
        Assert.Equal(East, job.Region);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.Equal(AttemptPhase.Completed, Assert.Single((await rig.LedgerAsync(job.Name)).Attempts).Phase);
        Assert.Equal(0, rig.Admission.Total);
    }

    [Fact]
    public async Task At_most_one_attempt_per_region_and_exhaustion_leaves_the_last_attempt_tracked()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[West].Created.Count == 1);
        for (var step = 0; step < 150; step++) await rig.PumpAsyncStep(); // 2x the wait again in the last region

        Assert.Single(rig.Aml[East].Created);
        Assert.Single(rig.Aml[West].Created);
        Assert.Single(rig.Aml[East].Cancelled);
        Assert.Empty(rig.Aml[West].Cancelled);
        Assert.False(task.IsCompleted);
        Assert.Equal("Queued", rig.Aml[West].Status[job.Name]);
        Assert.Equal(2, (await rig.LedgerAsync(job.Name)).Attempts.Length);

        rig.Aml[West].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
        Assert.Equal(West, job.Region);
    }

    [Fact]
    public async Task A_busy_destination_prevents_the_move_instead_of_cancelling_into_nothing()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        rig.SetSettings(settings => settings with { MaxConcurrentJobs = 2, Regions = new(settings.Regions) { [West] = settings.Regions[West] with { MaxConcurrentJobs = 1 } } });
        await rig.Settings.RefreshAsync(default);
        Assert.Equal(1, rig.Settings.Current.Regions[West].MaxConcurrentJobs);
        Assert.True(rig.Admission.TryAcquire(West, 1, 99)); // another job already holds the only western slot
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        for (var step = 0; step < 150; step++) await rig.PumpAsyncStep();
        Assert.Empty(rig.Aml[East].Cancelled);
        Assert.Empty(rig.Aml[West].Created);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
    }

    [Fact]
    public async Task Admission_enforces_regional_and_global_limits_and_waits_instead_of_failing()
    {
        var rig = await RegionalRig.CreateAsync(East);
        rig.SetSettings(settings => settings with { MaxConcurrentJobs = 1 });
        await rig.Settings.RefreshAsync(default);
        Assert.Equal(1, rig.Settings.Current.MaxConcurrentJobs);
        var first = rig.Job("wan-first0000001");
        var second = rig.Job("wan-second000001");
        var firstTask = rig.GenerateAsync(first);
        var secondTask = rig.GenerateAsync(second);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count >= 1);
        for (var step = 0; step < 100; step++) await rig.PumpAsyncStep();

        Assert.Single(rig.Aml[East].Created);
        Assert.Equal(1, rig.Admission.Total);
        var live = rig.Aml[East].Created[0].Name;
        var waiting = live == first.Name ? second : first;
        Assert.Equal(JobStatus.Queued, waiting.Status);

        rig.Aml[East].Status[live] = "Completed";
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 2);
        Assert.Equal(1, rig.Admission.Total);
        var next = rig.Aml[East].Created[1].Name;
        rig.Aml[East].Status[next] = "Completed";
        await Task.WhenAll(rig.PumpAsync(firstTask), rig.PumpAsync(secondTask));
        Assert.Equal(0, rig.Admission.Total);
    }

    [Fact]
    public async Task Lowering_a_limit_never_terminates_existing_work()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        rig.SetSettings(settings => settings with { MaxConcurrentJobs = 1, Regions = new(settings.Regions) { [East] = settings.Regions[East] with { MaxConcurrentJobs = 1 } } });
        for (var step = 0; step < 50; step++) await rig.PumpAsyncStep();
        Assert.Empty(rig.Aml[East].Cancelled);
        Assert.False(task.IsCompleted);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
    }

    [Fact]
    public async Task No_eligible_region_fails_explicitly_and_a_stopped_region_is_never_used()
    {
        var rig = await RegionalRig.CreateAsync();
        var failure = await Assert.ThrowsAsync<ConflictException>(() => rig.GenerateAsync(rig.Job()));
        Assert.Contains("not armed", failure.Message);
        Assert.Contains(East, failure.Message);
        Assert.Contains(West, failure.Message);
        Assert.Empty(rig.Aml[East].Created);
        Assert.Throws<ConflictException>(() => rig.Backend.EnsureCanSubmit(new SubmitRequest(["p"], "", "1:1", 5, 1, [null])));

        var armed = await RegionalRig.CreateAsync(East);
        await armed.Studio.Lifecycle.StopAsync(StudioRig.Actor);
        Assert.False(armed.Studio.Gate.IsArmed(East));
        await Assert.ThrowsAsync<ConflictException>(() => armed.GenerateAsync(armed.Job()));
        Assert.Empty(armed.Aml[East].Created);
    }

    [Fact]
    public async Task Regional_priority_then_headroom_then_name_decides_the_first_region()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        rig.SetSettings(settings => settings with { Regions = new(settings.Regions) { [East] = settings.Regions[East] with { Priority = 5 }, [West] = settings.Regions[West] with { Priority = 1 } } });
        await rig.Settings.RefreshAsync(default);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[West].Created.Count == 1);
        Assert.Empty(rig.Aml[East].Created);
        rig.Aml[West].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);

        var plan = RegionalPlanner.Plan(rig.Studio.Foundation, StudioSettingsSource.Defaults(rig.Studio.Foundation), _ => true, _ => true, new RegionalAdmission(), new HashSet<string>());
        Assert.Equal([East, West], plan.Candidates.Select(item => item.Region));
        var reasons = RegionalPlanner.Plan(rig.Studio.Foundation, StudioSettingsSource.Defaults(rig.Studio.Foundation), key => key == East, _ => false, new RegionalAdmission(), new HashSet<string> { East });
        Assert.False(reasons.AnyEligible);
        Assert.Contains("already tried", reasons.Ineligible[East]);
        Assert.Contains("not armed", reasons.Ineligible[West]);
    }

    // ---- restart boundaries ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_stop_mid_attempt_resumes_the_same_remote_job_without_a_second_create()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job();
        using var stop = new CancellationTokenSource();
        var task = rig.GenerateAsync(job, ct: stop.Token);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.PumpAsync(task));
        Assert.Empty(rig.Aml[East].Cancelled);
        Assert.Equal(0, rig.Admission.Total);

        var restarted = new GenerationJob { Name = job.Name, DisplayName = job.DisplayName, SubmittedAt = job.SubmittedAt, Mode = job.Mode, RemoteId = job.RemoteId, Region = job.Region, Context = job.Context };
        var backend = rig.NewBackend();
        Assert.True(backend.CanResume(restarted));
        var resumed = backend.ResumeAsync(restarted, default);
        await rig.PumpUntilAsync(() => rig.Admission.Active(East) == 1);
        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(resumed);

        Assert.Single(rig.Aml[East].Created);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.Equal(0, rig.Admission.Total);
    }

    [Fact]
    public async Task Recovery_of_an_unrecorded_acceptance_reads_the_job_and_never_creates_it()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var job = rig.Job();
        var region = rig.Studio.Region(East);
        var now = rig.Clock.GetUtcNow();
        job.RemoteId = job.Name;
        job.Region = East;
        job.Context = new JobContext("p", 768, 768, 5, 1, "", "a.png");
        await rig.Ledger.SaveAsync(new StudioJobRecord(1, 0, rig.Studio.Foundation.Scope, job.Name,
            [new JobAttempt(1, East, region.WorkspaceId, job.Name, new string('a', 64), 7200, AttemptPhase.Intent, now, now, null, null,
                $"azureml://datastores/{region.Datastore}/paths/video-library/{job.Name}/")]), default);

        var absent = rig.NewBackend().ResumeAsync(job, default);
        await rig.PumpAsync(absent.ContinueWith(_ => { }));
        await Assert.ThrowsAsync<ConflictException>(() => absent);
        Assert.Empty(rig.Aml[East].Created);
        Assert.Equal(AttemptPhase.Failed, (await rig.LedgerAsync(job.Name)).Attempts[0].Phase);
        Assert.Equal(0, rig.Admission.Total);

        var found = await RegionalRig.CreateAsync(East);
        var present = found.Job("wan-present000001");
        present.RemoteId = present.Name;
        present.Region = East;
        present.Context = new JobContext("p", 768, 768, 5, 1, "", "a.png");
        found.Aml[East].Status[present.Name] = "Queued";
        await found.Ledger.SaveAsync(new StudioJobRecord(1, 0, found.Studio.Foundation.Scope, present.Name,
            [new JobAttempt(1, East, region.WorkspaceId, present.Name, new string('a', 64), 7200, AttemptPhase.Intent, now, now, null, null,
                $"azureml://datastores/{region.Datastore}/paths/video-library/{present.Name}/")]), default);
        var resumed = found.NewBackend().ResumeAsync(present, default);
        await found.PumpUntilAsync(() => found.LedgerAsync(present.Name).Result.Attempts[0].Phase == AttemptPhase.Queued || found.LedgerAsync(present.Name).Result.Attempts[0].Phase == AttemptPhase.Accepted);
        found.Aml[East].Status[present.Name] = "Completed";
        await found.PumpAsync(resumed);
        Assert.Empty(found.Aml[East].Created);
    }

    [Fact]
    public async Task A_crash_between_the_job_record_and_the_intent_cannot_have_created_a_remote_job()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var job = rig.Job();
        job.RemoteId = job.Name;
        job.Region = East;
        job.Context = new JobContext("p", 768, 768, 5, 1, "", "a.png");
        var failure = await Assert.ThrowsAsync<ConflictException>(() => rig.NewBackend().ResumeAsync(job, default));
        Assert.Contains("not accepted", failure.Message);

        rig.Aml[East].Status[job.Name] = "Queued";
        var unexpected = await Assert.ThrowsAsync<ConflictException>(() => rig.NewBackend().ResumeAsync(job, default));
        Assert.Contains("unrecorded remote job", unexpected.Message);
        Assert.Empty(rig.Aml[East].Created);
    }

    [Fact]
    public async Task A_restart_after_cancel_intent_does_not_cancel_again_and_continues_the_move_when_cancellation_is_proven()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        rig.Aml[East].Cancel = CancelMode.Ignored;
        var job = rig.Job();
        using var stop = new CancellationTokenSource();
        var task = rig.GenerateAsync(job, ct: stop.Token);
        await rig.PumpUntilAsync(() => rig.Aml[East].Cancelled.Count == 1);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.PumpAsync(task));
        Assert.Equal(AttemptPhase.CancelRequested, (await rig.LedgerAsync(job.Name)).Attempts[0].Phase);
        Assert.Equal(0, rig.Admission.Total);

        // The cancellation lands while the host is down.
        rig.Aml[East].Status[job.Name] = "Canceled";
        var restarted = new GenerationJob { Name = job.Name, DisplayName = job.DisplayName, SubmittedAt = job.SubmittedAt, Mode = job.Mode, RemoteId = job.RemoteId, Region = job.Region, Context = job.Context };
        var resumed = rig.NewBackend().ResumeAsync(restarted, default);
        await rig.PumpUntilAsync(() => rig.Aml[West].Created.Count == 1);
        Assert.Single(rig.Aml[East].Cancelled);
        Assert.Contains($"studio/web-inputs/{job.Name}/a.png", rig.Stores[West].Data.Keys);
        rig.Aml[West].Status[job.Name] = "Completed";
        await rig.PumpAsync(resumed);
        Assert.Equal(West, restarted.Region);
        Assert.Equal([AttemptPhase.Canceled, AttemptPhase.Completed], (await rig.LedgerAsync(job.Name)).Attempts.Select(item => item.Phase));
    }

    [Fact]
    public async Task A_completed_attempt_is_collected_after_a_restart_not_replayed()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job();
        using var stop = new CancellationTokenSource();
        var task = rig.GenerateAsync(job, ct: stop.Token);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.PumpAsync(task));
        rig.Aml[East].Status[job.Name] = "Completed";

        var restarted = new GenerationJob { Name = job.Name, DisplayName = job.DisplayName, SubmittedAt = job.SubmittedAt, Mode = job.Mode, RemoteId = job.RemoteId, Region = job.Region, Context = job.Context };
        await rig.PumpAsync(rig.NewBackend().ResumeAsync(restarted, default));
        Assert.Single(rig.Aml[East].Created);
        Assert.Empty(rig.Aml[West].Created);
        Assert.Equal(AttemptPhase.Completed, Assert.Single((await rig.LedgerAsync(job.Name)).Attempts).Phase);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
    }

    [Fact]
    public async Task A_job_from_before_regional_routing_resumes_in_the_control_region_without_inputs_it_cannot_move()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job("wan-legacy000001");
        job.RemoteId = job.Name; // no Region, no attempt ledger, no recorded image name or negative prompt
        job.Context = new JobContext("old prompt", 768, 768, 5, 7);
        Assert.True(rig.Backend.CanResume(job));
        rig.Aml[East].Status[job.Name] = "Queued";

        var task = rig.Backend.ResumeAsync(job, default);
        for (var step = 0; step < 150; step++) await rig.PumpAsyncStep(); // far past the move wait
        Assert.Empty(rig.Aml[East].Cancelled);
        Assert.Empty(rig.Aml[West].Created);
        var ledger = await rig.LedgerAsync(job.Name);
        var attempt = Assert.Single(ledger.Attempts);
        Assert.Equal(East, attempt.Region);
        Assert.Equal(RegionalAzureMlBackend.LegacyArtifactId, attempt.ArtifactId);

        rig.Aml[East].Status[job.Name] = "Completed";
        await rig.PumpAsync(task);
        Assert.Equal(East, job.Region);
        Assert.Contains($"video-library/{job.Name}/meta.json", rig.Stores[East].Data.Keys);
        Assert.NotNull(await rig.Library.OpenReadAsync($"video-library/{job.Name}/ComfyUI_00001_.mp4"));
    }

    [Fact]
    public async Task A_failed_job_does_not_move_or_retry()
    {
        var rig = await RegionalRig.CreateAsync(East, West);
        var job = rig.Job();
        var task = rig.GenerateAsync(job);
        await rig.PumpUntilAsync(() => rig.Aml[East].Created.Count == 1);
        rig.Aml[East].Status[job.Name] = "Failed";
        await Assert.ThrowsAsync<AzureMlException>(() => rig.PumpAsync(task));
        Assert.Empty(rig.Aml[West].Created);
        Assert.Single(rig.Aml[East].Created);
        Assert.Equal(AttemptPhase.Failed, Assert.Single((await rig.LedgerAsync(job.Name)).Attempts).Phase);
        Assert.Equal(0, rig.Admission.Total);
    }

    // ---- shared pieces ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_stored_settings_keep_the_last_valid_ones_and_defaults_stay_within_ceilings()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var defaults = rig.Settings.Current;
        Assert.Equal(StudioSettingsSource.DefaultQueueMoveMinutes, defaults.QueueMoveMinutes);
        foreach (var (region, value) in defaults.Regions)
        {
            Assert.Equal(rig.Studio.Region(region).Policy.MaxJobMinutes, value.MaxJobMinutes);
            Assert.Equal(rig.Studio.Region(region).Policy.MaxNodes, value.MaxConcurrentJobs);
        }

        Assert.Equal(0, defaults.Regions[East].Priority);

        rig.SetSettings(settings => settings with { QueueMoveMinutes = 20 });
        await rig.Settings.RefreshAsync(default);
        Assert.Equal(20, rig.Settings.Current.QueueMoveMinutes);

        // Raising a timeout past the deployed ceiling is rejected, not clamped.
        rig.SetSettings(settings => settings with { Regions = new(settings.Regions) { [East] = settings.Regions[East] with { MaxJobMinutes = 121 } } });
        await rig.Settings.RefreshAsync(default);
        Assert.Equal(20, rig.Settings.Current.QueueMoveMinutes);
        Assert.Equal(rig.Studio.Region(East).Policy.MaxJobMinutes, rig.Settings.Current.Regions[East].MaxJobMinutes);
    }

    [Fact]
    public async Task A_hosted_foundation_selects_the_regional_backend_and_library_and_a_plain_host_keeps_the_flat_one()
    {
        var studio = StudioSettingsFixture.Foundation();
        var control = RegionContext.Derive(studio, studio.Regions[studio.ControlRegion]);
        (string, string?)[] flat =
        [
            ("AzureMl:SubscriptionId", control.SubscriptionId), ("AzureMl:TenantId", control.TenantId), ("AzureMl:ResourceGroup", control.ResourceGroupName),
            ("AzureMl:Workspace", control.WorkspaceName), ("AzureMl:Compute", control.ComputeName), ("AzureMl:ComputeIdentityClientId", control.ComputeIdentityClientId),
            ("AzureMl:StorageAccount", control.StorageAccountName), ("AzureMl:Container", control.ContainerName), ("AzureMl:Datastore", control.DatastoreName),
            ("AzureMl:ReleaseContainer", control.ReleaseContainerName), ("AzureMl:ReleaseDatastore", control.ReleaseDatastoreName),
        ];
        (string, string?)[] studioSettings = [.. StudioSettingsFixture.Flat().Select(pair => (pair.Key, pair.Value))];

        async Task<(string Backend, string Store)> Build(params (string, string?)[] settings)
        {
            using var temp = new TempFolder();
            var configuration = AzureMlSamples.Config([("Storage:Path", temp.Path), .. settings]);
            var services = new ServiceCollection().AddLogging().AddSingleton(configuration);
            services.AddStudio(configuration, temp.Path);
            await using var provider = services.BuildServiceProvider();
            return (provider.GetRequiredService<IGenerationBackend>().GetType().Name, provider.GetRequiredService<IObjectStore>().GetType().Name);
        }
        // The Studio settings alone select the regional backend: no flat AzureMl settings and no Terraform output are needed.
        Assert.Equal(("RegionalAzureMlBackend", "RegionalLibraryStore"), await Build(studioSettings));
        Assert.Equal("AzureMlBackend", (await Build(flat)).Backend);
        // An incomplete topology fails with the setting named instead of silently falling back.
        var incomplete = studioSettings.Where(pair => !pair.Item1.EndsWith("Compute:IdentityClientId", StringComparison.Ordinal)).ToArray();
        var error = await Assert.ThrowsAsync<ValidationException>(() => Build(incomplete));
        Assert.Contains("IdentityClientId", error.Message);
    }
    [Fact]
    public async Task The_library_unions_regions_pins_jobs_and_writes_only_to_the_control_store()
    {
        var rig = await RegionalRig.CreateAsync();
        await rig.Stores[East].WriteAsync("video-library/wan-east/meta.json", new byte[] { 1 });
        await rig.Stores[West].WriteAsync("video-library/wan-west/meta.json", new byte[] { 2 });
        await rig.Stores[West].WriteAsync("video-library/wan-east/meta.json", new byte[] { 9 }); // a stale duplicate in another region
        await rig.Stores[East].WriteAsync("video-library-jobs/wan-east.json", new byte[] { 3 });

        var listed = await rig.Library.ListAsync("video-library");
        Assert.Equal(["video-library/wan-east/meta.json", "video-library/wan-west/meta.json"], listed);
        Assert.Equal<byte[]>([1], (await rig.Library.ReadAsync("video-library/wan-east/meta.json"))!);
        Assert.Equal<byte[]>([2], (await rig.Library.ReadAsync("video-library/wan-west/meta.json"))!);
        Assert.Equal<byte[]>([3], (await rig.Library.ReadAsync("video-library-jobs/wan-east.json"))!);
        Assert.Null(await rig.Library.ReadAsync("video-library/wan-unknown/meta.json"));

        rig.Library.Pin("wan-east", West);
        Assert.Equal<byte[]>([9], (await rig.Library.ReadAsync("video-library/wan-east/meta.json"))!);
        await rig.Library.WriteAsync("portal-state/x.json", new byte[] { 4 });
        Assert.Contains("portal-state/x.json", rig.Control.Data.Keys);
        Assert.DoesNotContain("portal-state/x.json", rig.Stores[West].Data.Keys);
    }

    [Fact]
    public async Task The_attempt_ledger_refuses_a_history_the_contract_would_reject()
    {
        var rig = await RegionalRig.CreateAsync(East);
        var region = rig.Studio.Region(East);
        var now = rig.Clock.GetUtcNow();
        JobAttempt Attempt(int number, string key, AttemptPhase phase) => new(number, key, rig.Studio.Region(key).WorkspaceId, "wan-ledger000001", new string('a', 64), 600, phase, now, now, null,
            phase is AttemptPhase.Completed or AttemptPhase.Failed or AttemptPhase.Canceled ? now : null,
            $"azureml://datastores/{rig.Studio.Region(key).Datastore}/paths/video-library/wan-ledger000001/");

        await Assert.ThrowsAsync<ValidationException>(() => rig.Ledger.SaveAsync(new StudioJobRecord(1, 0, rig.Studio.Foundation.Scope, "wan-ledger000001",
            [Attempt(1, East, AttemptPhase.Running), Attempt(2, West, AttemptPhase.Intent)]), default)); // a live attempt cannot precede another
        await Assert.ThrowsAsync<ValidationException>(() => rig.Ledger.SaveAsync(new StudioJobRecord(1, 0, rig.Studio.Foundation.Scope, "wan-ledger000001",
            [Attempt(1, East, AttemptPhase.Canceled), Attempt(2, East, AttemptPhase.Intent)]), default)); // one attempt per region
        Assert.DoesNotContain(rig.Control.Data.Keys, key => key.StartsWith("video-library-attempts/", StringComparison.Ordinal));
        _ = region;
    }
}

internal static class RegionalRigExtensions
{
    public static Task PumpAsyncStep(this RegionalRig rig) => rig.PumpOneAsync();
}
