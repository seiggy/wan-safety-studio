using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Operations;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

/// <summary>An Azure ML workspace for training: jobs advance through a script, and write their output folder when they finish.</summary>
internal sealed class TrainAml(MemStore store) : IAzureMlClient
{
    public Dictionary<string, JsonObject> Jobs { get; } = [];
    public List<string> Created { get; } = [];
    public int Cancels { get; set; }
    public int Gets { get; private set; }
    public int GetFailures { get; set; }
    public string? CreateFailure { get; set; }
    public bool CancelWorks { get; set; } = true;
    public string? Forced { get; set; }
    public Func<int, string> Script { get; set; } = count => count == 0 ? "Running" : "Completed";
    public Dictionary<string, string> Outputs { get; } = new() { ["best.pth"] = "weights", ["metrics.json"] = "{\"map50\":0.5,\"map50_95\":0.3}" };
    public Action? OnCreate { get; set; }

    public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct) => throw new NotSupportedException();

    public Task<JsonNode> CreateJobAsync(string name, JsonObject body, CancellationToken ct)
    {
        OnCreate?.Invoke();
        Created.Add(name);
        if (CreateFailure == "reject") throw new AzureMlException("rejected", HttpStatusCode.BadRequest);
        var job = new JsonObject { ["name"] = name, ["properties"] = JsonNode.Parse(body["properties"]!.ToJsonString())! };
        job["properties"]!["status"] = "NotStarted";
        Jobs[name] = job;
        if (CreateFailure == "lost") throw new AzureMlException("timeout");
        return Task.FromResult<JsonNode>(job);
    }

    public Task<JsonNode> GetJobAsync(string name, CancellationToken ct)
    {
        Gets++;
        if (GetFailures > 0)
        {
            GetFailures--;
            throw new AzureMlException("unavailable", HttpStatusCode.ServiceUnavailable);
        }

        if (!Jobs.TryGetValue(name, out var job)) throw new AzureMlException("missing", HttpStatusCode.NotFound);
        var seen = job["seen"]?.GetValue<int>() ?? 0;
        job["seen"] = seen + 1;
        var status = Forced ?? Script(seen);
        job["properties"]!["status"] = status;
        if (AzureMlStatus.IsTerminal(status) && job["written"] is null)
        {
            job["written"] = true;
            foreach (var (file, text) in Outputs) store.Data[$"studio/training/{name["train-".Length..]}/out/{file}"] = Encoding.UTF8.GetBytes(text);
        }

        return Task.FromResult<JsonNode>(JsonNode.Parse(job.ToJsonString())!);
    }

    public Task CancelJobAsync(string name, CancellationToken ct)
    {
        Cancels++;
        if (CancelWorks) Forced = "Canceled";
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonNode>>([]);

    public Task<string?> GetComputeStateAsync(CancellationToken ct) => Task.FromResult<string?>("Succeeded");
}

internal sealed class TrainRig : IDisposable
{
    public const string Trainer = "yolox";
    public required StudioRig Studio { get; init; }
    public Dictionary<string, MemStore> Stores { get; } = [];
    public Dictionary<string, TrainAml> Aml { get; } = [];
    public required Dictionary<string, RegionContext> Regions { get; init; }
    public RegionalAdmission Admission { get; } = new();
    public required StudioSettingsSource Settings { get; init; }
    public required TrainerBindings Bindings { get; init; }
    public required AzureMlTrainingCompute Compute { get; init; }
    public string Artifact { get; } = new('a', 64);
    public string Control => Studio.Foundation.ControlRegion;
    public string Other => Studio.Foundation.Regions.Keys.First(key => key != Control);
    public string Data { get; } = Path.Combine(Path.GetTempPath(), "train-rig-" + Guid.NewGuid().ToString("N"));

    public static async Task<TrainRig> CreateAsync(string[] armed, string[] promoted, string? artifact = null)
    {
        var studio = await StudioRig.CreateAsync();
        foreach (var key in armed) await studio.ArmedAsync(key);
        var regions = new Dictionary<string, RegionContext>();
        var stores = new Dictionary<string, MemStore>();
        var aml = new Dictionary<string, TrainAml>();
        foreach (var (key, region) in studio.Foundation.Regions)
        {
            var store = new MemStore();
            stores[key] = store;
            aml[key] = new TrainAml(store);
            regions[key] = new RegionContext(key, region, RegionContext.Derive(studio.Foundation, region), aml[key], store);
        }

        var settings = new StudioSettingsSource(studio.Foundation, studio.Store, NullLogger<StudioSettingsSource>.Instance);
        var bindings = new TrainerBindings(studio.Foundation, studio.Store, NullLogger<TrainerBindings>.Instance);
        var admission = new RegionalAdmission();
        var rig = new TrainRig
        {
            Studio = studio, Regions = regions, Settings = settings, Bindings = bindings,
            Compute = new AzureMlTrainingCompute(studio.Foundation, regions, bindings, studio.Gate, settings, admission, TimeProvider.System,
                new RemoteTrainingTimings(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60)), NullLogger<AzureMlTrainingCompute>.Instance),
        };
        foreach (var (key, value) in stores) rig.Stores[key] = value;
        foreach (var (key, value) in aml) rig.Aml[key] = value;
        foreach (var key in promoted) rig.Promote(key, artifact ?? rig.Artifact);
        await bindings.RefreshAsync(default);
        Directory.CreateDirectory(Path.Combine(rig.Data, "images"));
        await File.WriteAllTextAsync(Path.Combine(rig.Data, "train.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(rig.Data, "images", "a.jpg"), "jpeg");
        return rig;
    }

    public TrainerBinding Binding(string region, string artifact, Func<TrainerBinding, TrainerBinding>? change = null)
    {
        var value = Studio.Foundation.Regions[region];
        var binding = new TrainerBinding(
            1, region, value.WorkspaceId, StudioContracts.RegionFingerprint(Studio.Foundation, region), artifact, Trainer, "0.1.0", "vision.trainers.yolox",
            ["nano", "tiny", "s"], "nano", "Apache-2.0", "Apache-2.0 (weights)", new string('c', 40), "sha256:" + new string('b', 64),
            $"{StudioResourceNames.NameOf(value.RegistryId)}.azurecr.io/wan/yolox@sha256:{new string('b', 64)}",
            $"{value.WorkspaceId}/environments/wan-trainer-yolox-{artifact[..12]}/versions/1",
            new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), "33333333-3333-3333-3333-333333333333");
        return change?.Invoke(binding) ?? binding;
    }

    public void Promote(string region, string artifact, Func<TrainerBinding, TrainerBinding>? change = null) =>
        Studio.Store.Release[(region, $"trainers/{Trainer}/{artifact}.json")] = StudioContracts.Write(Binding(region, artifact, change));

    public TrainingRun Run(string id = "rabcdef0123", string preset = "nano", int? epochs = 5) => new()
    {
        Id = id, Project = "demo", Dataset = "v001", Trainer = Trainer, TrainerVersion = "0.1.0", Preset = preset, Epochs = epochs, Compute = "azureml",
        CreatedAt = "2026-10-26T12:00:00Z",
    };

    public TrainerManifest Manifest() => Bindings.Get(Trainer);

    public void Dispose()
    {
        if (Directory.Exists(Data)) Directory.Delete(Data, recursive: true);
    }
}

public sealed class RemoteTrainingTests
{
    private static string Out() => Path.Combine(Path.GetTempPath(), "train-out-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_trainer_binding_must_match_the_selected_scope_and_use_only_validated_command_parts()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        var foundation = rig.Studio.Foundation;
        TrainerBinding Good() => rig.Binding(rig.Control, rig.Artifact);
        Assert.Equal("yolox", StudioContracts.ReadTrainer(StudioContracts.Write(Good()), foundation).Name);

        void Rejected(Func<TrainerBinding, TrainerBinding> change) =>
            Assert.Throws<ValidationException>(() => StudioContracts.ReadTrainer(StudioContracts.Write(change(Good())), foundation));
        Rejected(binding => binding with { Region = "nowhere" });
        Rejected(binding => binding with { ScopeFingerprint = new string('0', 64) });
        Rejected(binding => binding with { WorkspaceId = binding.WorkspaceId + "x" });
        Rejected(binding => binding with { Module = "vision.trainers.yolox; curl evil" });
        Rejected(binding => binding with { Module = "..yolox" });
        Rejected(binding => binding with { Presets = ["nano", "nano"] });
        Rejected(binding => binding with { Presets = ["nano --epochs 9999"] });
        Rejected(binding => binding with { DefaultPreset = "huge" });
        Rejected(binding => binding with { Image = "evil.azurecr.io/wan/yolox@sha256:" + new string('b', 64) });
        Rejected(binding => binding with { Image = binding.Image[..^64] + new string('d', 64) });
        Rejected(binding => binding with { EnvironmentId = "azureml:other:1" });
        Rejected(binding => binding with { LicenseCode = "bad\nlicence" });
        Rejected(binding => binding with { VerifiedAt = DateTimeOffset.Parse("2026-10-26T12:00:00+02:00") });
        Rejected(binding => binding with { Name = "../x" });
    }

    [Fact]
    public async Task The_registry_lists_only_valid_promoted_bindings_and_keeps_the_last_proof_when_a_region_is_unreadable()
    {
        using var rig = await TrainRig.CreateAsync([], [], null);
        rig.Promote(rig.Control, rig.Artifact);
        rig.Promote(rig.Control, new string('e', 64), binding => binding with { Version = "0.2.0", VerifiedAt = binding.VerifiedAt.AddDays(1) });
        rig.Studio.Store.Release[(rig.Other, $"trainers/yolox/{new string('f', 64)}.json")] = "{ not a binding";
        rig.Studio.Store.Release[(rig.Control, "trainers/yolox/readme.txt")] = "ignored";
        await rig.Bindings.RefreshAsync(default);

        var manifest = Assert.Single(rig.Bindings.All());
        Assert.Equal("0.2.0", manifest.Version);
        Assert.Equal(new string('e', 64), manifest.ArtifactId);
        Assert.Equal("vision.trainers.yolox", manifest.Module);
        Assert.Null(rig.Bindings.Binding(rig.Other, "yolox", rig.Artifact));
        Assert.Throws<ValidationException>(() => rig.Bindings.Get("unknown"));

        var failing = new TrainerBindings(rig.Studio.Foundation, new FlakyReleases(rig.Studio.Store), NullLogger<TrainerBindings>.Instance);
        await failing.RefreshAsync(default);
        Assert.Single(failing.All());
        FlakyReleases.Down = true;
        try
        {
            await failing.RefreshAsync(default);
            Assert.Equal("0.2.0", Assert.Single(failing.All()).Version);
        }
        finally { FlakyReleases.Down = false; }
    }

    private sealed class FlakyReleases(IStudioReleaseReader inner) : IStudioReleaseReader
    {
        public static bool Down { get; set; }
        public Task<string?> ReadAsync(string region, string path, CancellationToken ct) => inner.ReadAsync(region, path, ct);
        public Task<IReadOnlyList<string>> ListAsync(string region, string prefix, CancellationToken ct) =>
            Down ? throw new IOException("unreachable") : inner.ListAsync(region, prefix, ct);
    }

    [Fact]
    public async Task The_job_spec_is_one_fixed_command_on_the_regional_compute_and_a_tampered_spec_is_refused()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        var region = rig.Studio.Foundation.Regions[rig.Control];
        var foundation = RegionContext.Derive(rig.Studio.Foundation, region);
        var binding = rig.Binding(rig.Control, rig.Artifact);
        var run = rig.Run();
        var spec = AzureMlTrainingSpecBuilder.Build(foundation, binding, run, 1800);

        Assert.Equal("train-rabcdef0123", spec.Name);
        Assert.Equal("python -m vision.trainers.yolox all --preset nano --epochs 5 --data \"${{inputs.data}}\" --out \"${{outputs.out}}\"", spec.Command);
        var body = spec.ToArmBody()["properties"]!;
        Assert.Equal(foundation.ComputeId, body["computeId"]!.GetValue<string>());
        Assert.Equal(binding.EnvironmentId, body["environmentId"]!.GetValue<string>());
        Assert.Equal(1, body["resources"]!["instanceCount"]!.GetValue<int>());
        Assert.Equal("PT1800S", body["limits"]!["timeout"]!.GetValue<string>());
        Assert.Equal("ReadOnlyMount", body["inputs"]!["data"]!["mode"]!.GetValue<string>());
        Assert.Equal(foundation.DatastorePath("studio/training/rabcdef0123/data/"), body["inputs"]!["data"]!["uri"]!.GetValue<string>());
        Assert.Equal(foundation.ComputeIdentityClientId, body["identity"]!["clientId"]!.GetValue<string>());

        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Build(foundation, binding, rig.Run(preset: "huge"), 60));
        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Build(foundation, binding, rig.Run(epochs: 5000), 60));
        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Build(foundation, binding, rig.Run(id: "../escape"), 60));
        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Build(foundation, binding, run, AzureMlTrainingSpec.MaxSeconds + 1));
        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Validate(spec with { Command = spec.Command + "; curl evil" }, foundation, binding, run));
        Assert.Throws<AzureMlSafetyException>(() => AzureMlTrainingSpecBuilder.Validate(spec with { DataUri = foundation.DatastorePath("studio/training/other/data/") }, foundation, binding, run));
    }

    [Fact]
    public async Task A_run_stages_create_only_data_saves_its_identity_before_the_single_create_and_collects_output()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var events = new List<string>();
        var aml = rig.Aml[rig.Control];
        aml.OnCreate = () => events.Add("create");
        var run = rig.Run();
        var output = Out();

        var code = await rig.Compute.RunAsync(run, rig.Manifest(), rig.Data, output, _ => { events.Add("remember"); return Task.CompletedTask; }, default);

        Assert.Equal(0, code);
        Assert.Equal(["remember", "create"], events);
        Assert.Equal(["train-rabcdef0123"], aml.Created);
        var store = rig.Stores[rig.Control];
        Assert.Contains("studio/training/rabcdef0123/data/train.json", store.Data.Keys);
        Assert.Contains("studio/training/rabcdef0123/data/images/a.jpg", store.Data.Keys);
        var manifest = JsonNode.Parse(store.Data["studio/training/rabcdef0123/data.manifest.json"])!;
        Assert.Equal(2, manifest["files"]!.AsArray().Count);
        Assert.Equal("weights", await File.ReadAllTextAsync(Path.Combine(output, "best.pth")));
        Assert.True(File.Exists(Path.Combine(output, "metrics.json")));
        Assert.Contains("ended Completed", await File.ReadAllTextAsync(Path.Combine(output, "train.log")));
        Directory.Delete(output, true);
    }

    [Fact]
    public async Task A_failed_job_still_returns_its_output_for_salvage_and_a_nonzero_code()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        rig.Aml[rig.Control].Script = _ => "Failed";
        rig.Aml[rig.Control].Outputs.Remove("metrics.json");
        var output = Out();

        var code = await rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, output, _ => Task.CompletedTask, default);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(output, "best.pth")));
        Assert.Contains("ended Failed", await File.ReadAllTextAsync(Path.Combine(output, "train.log")));
        Directory.Delete(output, true);
    }

    [Fact]
    public async Task The_job_timeout_is_the_regions_configured_limit_and_never_above_the_policy()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var current = rig.Settings.Current;
        var minutes = Math.Max(1, current.Regions[rig.Control].MaxJobMinutes / 2);
        var lowered = current with { Regions = current.Regions.ToDictionary(pair => pair.Key, pair => pair.Key == rig.Control ? pair.Value with { MaxJobMinutes = minutes } : pair.Value) };
        rig.Studio.Store.SettingsJson = StudioContracts.Write(lowered with { Version = current.Version + 1 });
        await rig.Settings.RefreshAsync(default);
        var output = Out();
        await rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, output, _ => Task.CompletedTask, default);

        var timeout = rig.Aml[rig.Control].Jobs["train-rabcdef0123"]["properties"]!["limits"]!["timeout"]!.GetValue<string>();
        Assert.Equal($"PT{minutes * 60}S", timeout);
        Assert.InRange(minutes * 60, 1, AzureMlTrainingSpec.MaxSeconds);
        Directory.Delete(output, true);
    }

    [Fact]
    public async Task Training_shares_generation_admission_and_never_submits_when_full_or_unpromoted_or_unarmed()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        var manifest = rig.Bindings.Get("yolox");
        var output = Out();

        // Nothing is armed.
        var unarmed = await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.RunAsync(rig.Run(), manifest, rig.Data, output, _ => Task.CompletedTask, default));
        Assert.Contains("not armed", unarmed.Message);

        // Armed, but only a different region holds the promoted build.
        await rig.Studio.ArmedAsync(rig.Other);
        var unpromoted = await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.RunAsync(rig.Run(), manifest, rig.Data, output, _ => Task.CompletedTask, default));
        Assert.Contains("promoted trainer", unpromoted.Message);

        // Armed and promoted, but generation already holds every slot.
        await rig.Studio.ArmedAsync(rig.Control);
        var limits = rig.Settings.Current;
        var filled = new RegionalAdmission();
        while (filled.TryAcquire(rig.Control, limits.Regions[rig.Control].MaxConcurrentJobs, limits.MaxConcurrentJobs)) { }
        var shared = new AzureMlTrainingCompute(rig.Studio.Foundation, rig.Regions, rig.Bindings, rig.Studio.Gate, rig.Settings, filled, TimeProvider.System,
            new RemoteTrainingTimings(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60)));
        var full = await Assert.ThrowsAsync<ConflictException>(() => shared.RunAsync(rig.Run(), manifest, rig.Data, output, _ => Task.CompletedTask, default));
        Assert.Contains("job limit", full.Message);

        Assert.Empty(rig.Aml[rig.Control].Created);
        Assert.DoesNotContain(rig.Stores[rig.Control].Data.Keys, key => key.StartsWith("studio/training/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_admission_slot_is_held_while_the_job_runs_and_released_afterwards()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var admission = new RegionalAdmission();
        var compute = new AzureMlTrainingCompute(rig.Studio.Foundation, rig.Regions, rig.Bindings, rig.Studio.Gate, rig.Settings, admission, TimeProvider.System,
            new RemoteTrainingTimings(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60)));
        var held = 0;
        rig.Aml[rig.Control].Script = _ => { held = Math.Max(held, admission.Total); return held > 0 && rig.Aml[rig.Control].Gets > 2 ? "Completed" : "Running"; };
        var output = Out();

        await compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, output, _ => Task.CompletedTask, default);

        Assert.Equal(1, held);
        Assert.Equal(0, admission.Total);
        Directory.Delete(output, true);
    }

    [Fact]
    public async Task An_unclear_create_is_settled_by_reading_the_job_and_never_created_twice()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var aml = rig.Aml[rig.Control];
        aml.CreateFailure = "lost";
        var output = Out();

        Assert.Equal(0, await rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, output, _ => Task.CompletedTask, default));
        Assert.Single(aml.Created);
        Directory.Delete(output, true);

        // The create was rejected and nothing exists: nothing is running, and still no second create.
        var rejected = await TrainRig.CreateAsync([], []);
        rejected.Promote(rejected.Control, rejected.Artifact);
        await rejected.Bindings.RefreshAsync(default);
        await rejected.Studio.ArmedAsync(rejected.Control);
        rejected.Aml[rejected.Control].CreateFailure = "reject";
        var error = await Assert.ThrowsAsync<ConflictException>(() => rejected.Compute.RunAsync(rejected.Run(), rejected.Manifest(), rejected.Data, Out(), _ => Task.CompletedTask, default));
        Assert.Contains("nothing is running", error.Message);
        Assert.Single(rejected.Aml[rejected.Control].Created);
        rejected.Dispose();
    }

    [Fact]
    public async Task When_the_job_cannot_be_confirmed_it_is_asked_to_stop_and_the_run_fails_without_a_second_create()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var aml = rig.Aml[rig.Control];
        aml.CreateFailure = "lost";
        aml.GetFailures = 1;

        var error = await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, Out(), _ => Task.CompletedTask, default));

        Assert.Contains("Could not confirm", error.Message);
        Assert.Single(aml.Created);
        Assert.Equal(1, aml.Cancels);
    }

    [Fact]
    public async Task A_restart_follows_the_saved_job_without_creating_and_a_missing_job_is_an_explicit_failure()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var aml = rig.Aml[rig.Control];
        var run = rig.Run();
        run.Remote = new RemoteRun(rig.Control, "train-rabcdef0123", rig.Artifact);
        aml.Jobs["train-rabcdef0123"] = new JsonObject { ["name"] = "train-rabcdef0123", ["properties"] = new JsonObject { ["status"] = "Running" } };
        var output = Out();

        Assert.Equal(0, await rig.Compute.ResumeAsync(run, output, default));
        Assert.Empty(aml.Created);
        Assert.True(File.Exists(Path.Combine(output, "best.pth")));
        Directory.Delete(output, true);

        aml.Jobs.Clear();
        await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.ResumeAsync(run, Out(), default));
        run.Remote = new RemoteRun("elsewhere", "train-rabcdef0123", rig.Artifact);
        await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.ResumeAsync(run, Out(), default));
    }

    [Fact]
    public async Task Detaching_never_cancels_the_remote_job_but_an_explicit_cancel_requires_proof()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var aml = rig.Aml[rig.Control];
        aml.Script = _ => "Running";
        using var detach = new CancellationTokenSource();
        var running = rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, Out(), _ => Task.CompletedTask, detach.Token);
        while (aml.Gets < 2) await Task.Delay(1);
        await detach.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(0, aml.Cancels);

        var remote = new RemoteRun(rig.Control, "train-rabcdef0123", rig.Artifact);
        await rig.Compute.CancelAsync(remote, default);
        Assert.Equal(1, aml.Cancels);

        aml.Forced = null;
        aml.CancelWorks = false;
        await Assert.ThrowsAsync<ConflictException>(() => rig.Compute.CancelAsync(remote, default));
        Assert.Equal(2, aml.Cancels);
    }

    [Fact]
    public async Task Transient_status_failures_are_retried_and_a_persistent_outage_stops_the_job()
    {
        using var rig = await TrainRig.CreateAsync([], []);
        rig.Promote(rig.Control, rig.Artifact);
        await rig.Bindings.RefreshAsync(default);
        await rig.Studio.ArmedAsync(rig.Control);
        var aml = rig.Aml[rig.Control];
        aml.GetFailures = 3;
        var output = Out();
        Assert.Equal(0, await rig.Compute.RunAsync(rig.Run(), rig.Manifest(), rig.Data, output, _ => Task.CompletedTask, default));
        Directory.Delete(output, true);

        using var down = await TrainRig.CreateAsync([], []);
        down.Promote(down.Control, down.Artifact);
        await down.Bindings.RefreshAsync(default);
        await down.Studio.ArmedAsync(down.Control);
        down.Aml[down.Control].Script = _ => "Running";
        var created = false;
        down.Aml[down.Control].OnCreate = () => created = true;
        var task = down.Compute.RunAsync(down.Run(), down.Manifest(), down.Data, Out(), _ => Task.CompletedTask, default);
        while (!created) await Task.Delay(1);
        down.Aml[down.Control].GetFailures = int.MaxValue;
        var error = await Assert.ThrowsAsync<ConflictException>(() => task);
        Assert.Contains("asked to stop", error.Message);
        Assert.Equal(1, down.Aml[down.Control].Cancels);
    }
}

public sealed class RemoteTrainingServiceTests
{
    private static string RunKey(string id) => VisionService.Key("demo", "runs", id, "run.json");

    private sealed class Registry(string? artifact) : ITrainerRegistry
    {
        private readonly TrainerManifest manifest = new("test", "1", "test:1", "MIT", "MIT", ["nano"], "nano", "vision.trainers.test", artifact);
        public IReadOnlyList<TrainerManifest> All() => [manifest];
        public TrainerManifest Get(string name) => manifest;
    }

    private sealed class FakeDurable : IDurableComputeTarget
    {
        public string Name => "fake";
        public List<string> Log { get; } = [];
        public bool Hang { get; set; }
        public Func<string, Task<byte[]?>>? ReadRun { get; set; }
        public RemoteRun? SavedBeforeCreate { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> IComputeTarget.RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public async Task<int> RunAsync(TrainingRun run, TrainerManifest manifest, string dataDirectory, string outDirectory, Func<RemoteRun, Task> remember, CancellationToken ct)
        {
            Log.Add("run");
            await remember(new RemoteRun("eastus2", "train-" + run.Id, new string('a', 64)));
            SavedBeforeCreate = Json.From<TrainingRun>((await ReadRun!(run.Id))!)!.Remote;
            Log.Add("created");
            Entered.TrySetResult();
            if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "metrics.json"), "{\"map50\":0.5,\"map50_95\":0.3}", ct);
            return 0;
        }

        public async Task<int> ResumeAsync(TrainingRun run, string outDirectory, CancellationToken ct)
        {
            Log.Add("resume " + run.Id);
            await File.WriteAllTextAsync(Path.Combine(outDirectory, "metrics.json"), "{\"map50\":0.9,\"map50_95\":0.7}", ct);
            return 0;
        }

        public Task CancelAsync(RemoteRun remote, CancellationToken ct)
        {
            Log.Add("cancel " + remote.Job);
            return Task.CompletedTask;
        }
    }

    private static async Task<(VisionRegressionWorkspace Workspace, FakeDurable Compute)> SeedAsync()
    {
        var workspace = new VisionRegressionWorkspace();
        await workspace.SeedAsync(1);
        await workspace.Vision.FreezeDatasetAsync("demo", 0);
        var compute = new FakeDurable();
        compute.ReadRun = id => workspace.Store.ReadAsync(RunKey(id));
        return (workspace, compute);
    }

    private static TrainingService Create(VisionRegressionWorkspace workspace, IComputeTarget compute, TimeProvider? time = null, double hours = 12) =>
        new(workspace.Vision, new Registry(new string('a', 64)), compute, new FakeEvents(), time ?? TimeProvider.System,
            Options.Create(new TrainingOptions { CleanupTimeoutSeconds = 1, MaxRunHours = hours }));

    private static Task SaveRunAsync(VisionRegressionWorkspace workspace, string id, string status, RemoteRun? remote) =>
        workspace.Store.WriteAsync(RunKey(id), Json.ToBytes(new TrainingRun
        {
            Id = id, Project = "demo", Dataset = "v001", Trainer = "test", TrainerVersion = "1", Preset = "nano", Compute = "fake", Status = status, Remote = remote,
            CreatedAt = "2026-10-26T12:00:00Z",
        }));

    [Fact]
    public async Task A_durable_run_saves_its_remote_identity_before_creation_and_completes_with_metrics()
    {
        var (workspace, compute) = await SeedAsync();
        using var _ = workspace;
        await using var service = Create(workspace, compute);

        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await service.WhenIdleAsync();

        Assert.Equal(["run", "created"], compute.Log);
        Assert.Equal("train-" + run.Id, compute.SavedBeforeCreate?.Job);
        var done = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal(0.5, done.Metrics?.Map50);
        Assert.Equal("train-" + run.Id, done.Remote?.Job);
    }

    [Fact]
    public async Task Durable_runs_are_limited_by_admission_not_by_the_local_one_at_a_time_rule()
    {
        var (workspace, compute) = await SeedAsync();
        using var _ = workspace;
        compute.Hang = true;
        await using var service = Create(workspace, compute);

        await service.StartAsync("demo", "v001", "test", null, null);
        await service.StartAsync("demo", "v001", "test", null, null);
        await compute.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        while (compute.Log.Count(item => item == "created") < 2) await Task.Delay(1);

        Assert.Equal(2, compute.Log.Count(item => item == "run"));
    }

    [Fact]
    public async Task A_restart_resumes_saved_remote_runs_once_and_settles_runs_that_never_reached_a_remote_job()
    {
        var (workspace, compute) = await SeedAsync();
        using var _ = workspace;
        await SaveRunAsync(workspace, "r0000000001", RunStatus.Running, new RemoteRun("eastus2", "train-r0000000001", new string('a', 64)));
        await SaveRunAsync(workspace, "r0000000002", RunStatus.Preparing, null);
        await SaveRunAsync(workspace, "r0000000003", RunStatus.Completed, null);
        await using var service = Create(workspace, compute);

        await service.RecoverAsync(default);
        await service.RecoverAsync(default);
        await service.WhenIdleAsync();

        Assert.Equal(["resume r0000000001"], compute.Log);
        var resumed = await service.GetAsync("demo", "r0000000001");
        Assert.Equal(RunStatus.Completed, resumed.Status);
        Assert.Equal(0.9, resumed.Metrics?.Map50);
        var stale = await service.GetAsync("demo", "r0000000002");
        Assert.Equal(RunStatus.Failed, stale.Status);
        Assert.Contains("nothing was submitted", stale.Error);
        Assert.Equal(RunStatus.Completed, (await service.GetAsync("demo", "r0000000003")).Status);
    }

    [Fact]
    public async Task A_restart_fails_unresumable_local_runs_instead_of_leaving_them_in_progress_forever()
    {
        using var workspace = new VisionRegressionWorkspace();
        await workspace.SeedAsync(1);
        await SaveRunAsync(workspace, "r0000000004", RunStatus.Running, null);
        await using var service = new TrainingService(workspace.Vision, new Registry(null), new LocalNoop(), new FakeEvents(), TimeProvider.System);

        await service.RecoverAsync(default);

        var run = await service.GetAsync("demo", "r0000000004");
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("cannot be resumed", run.Error);
    }

    private sealed class LocalNoop : IComputeTarget
    {
        public string Name => "local";
        public Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task The_run_time_limit_cancels_the_remote_job_but_a_shutdown_only_detaches()
    {
        var clock = new ReliabilityTimeProvider();
        var (workspace, compute) = await SeedAsync();
        using var _ = workspace;
        compute.Hang = true;
        var service = Create(workspace, compute, clock, hours: 0.01);
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await compute.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.WhenIdleAsync();

        Assert.Contains("cancel train-" + run.Id, compute.Log);
        var failed = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("maximum run time", failed.Error);
        await service.DisposeAsync();

        var (second, other) = await SeedAsync();
        using var __ = second;
        other.Hang = true;
        await using var stopping = Create(second, other);
        var live = await stopping.StartAsync("demo", "v001", "test", null, null);
        await other.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopping.DisposeAsync();

        Assert.DoesNotContain(other.Log, item => item.StartsWith("cancel", StringComparison.Ordinal));
        Assert.Equal("train-" + live.Id, (await stopping.GetAsync("demo", live.Id)).Remote?.Job);
    }
}
