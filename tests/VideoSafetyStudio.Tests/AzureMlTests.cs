using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class AzureMlJobTests
{
    [Fact]
    public void Spec_builds_exactly_the_contract_job()
    {
        var spec = AzureMlSamples.Spec();
        var properties = spec.ToArmBody()["properties"]!;
        Assert.Equal("Command", properties["jobType"]!.GetValue<string>());
        Assert.Equal("comfyui-wan", properties["experimentName"]!.GetValue<string>());
        Assert.Equal(AzureMlSamples.ComputeId, properties["computeId"]!.GetValue<string>());
        Assert.Equal($"/subscriptions/{AzureMlSamples.Sub}/resourceGroups/rg-wan/providers/Microsoft.MachineLearningServices/workspaces/mlw-wan/environments/comfyui-wan-cu128/versions/v7", properties["environmentId"]!.GetValue<string>());
        Assert.Equal("PT7200S", properties["limits"]!["timeout"]!.GetValue<string>());
        Assert.Equal(1, properties["resources"]!["instanceCount"]!.GetValue<int>());
        Assert.Equal(AzureMlSamples.Identity, properties["identity"]!["clientId"]!.GetValue<string>());
        Assert.Empty(properties["environmentVariables"]!.AsObject());
        Assert.Null(properties["tags"]);

        var command = properties["command"]!.GetValue<string>();
        Assert.StartsWith("cd \"${{inputs.repo}}\" && python azureml/run_workflow_job.py --workflow \"${{inputs.workflow_json}}\" --models-dir ${{inputs.models}} --output-dir ${{outputs.generated}} --startup-timeout 900 --run-timeout 7200 --positive-prompt", command);
        Assert.EndsWith("--input-image-url \"file://${{inputs.input_image_url}}\" --input-image-filename \"${{inputs.input_image_filename}}\"", command);
        Assert.DoesNotContain("\n", command);

        var inputs = properties["inputs"]!.AsObject();
        Assert.Equal(["repo", "models", "input_image_url", "workflow_json", "positive_prompt", "negative_prompt", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "width", "height", "duration_seconds", "length", "batch_size", "fps", "filename_prefix", "input_image_filename"],
            inputs.Select(p => p.Key).ToArray());
        Assert.Equal(("uri_folder", "ReadOnlyMount", "azureml://datastores/workspaceblobstore/paths/code/v7-wan/"),
            (inputs["repo"]!["jobInputType"]!.GetValue<string>(), inputs["repo"]!["mode"]!.GetValue<string>(), inputs["repo"]!["uri"]!.GetValue<string>()));
        Assert.Equal(("uri_folder", "ReadOnlyMount"), (inputs["models"]!["jobInputType"]!.GetValue<string>(), inputs["models"]!["mode"]!.GetValue<string>()));
        Assert.Equal(("uri_file", "Download"), (inputs["input_image_url"]!["jobInputType"]!.GetValue<string>(), inputs["input_image_url"]!["mode"]!.GetValue<string>()));
        Assert.Equal(("uri_file", "Download"), (inputs["workflow_json"]!["jobInputType"]!.GetValue<string>(), inputs["workflow_json"]!["mode"]!.GetValue<string>()));
        Assert.Equal(("simple", "1.0", "1", "ComfyUI", "3.5", "5.0"), (Literal("scheduler"), Literal("denoise"), Literal("batch_size"), Literal("filename_prefix"), Literal("cfg"), Literal("duration_seconds")));
        Assert.Equal("a \\\"quoted\\\" \\$(rm -rf /) prompt", Literal("positive_prompt"));

        var output = properties["outputs"]!["generated"]!;
        Assert.Equal(("uri_folder", "ReadWriteMount", "azureml://datastores/workspaceblobstore/paths/video-library/wan-abc123abc123/"),
            (output["jobOutputType"]!.GetValue<string>(), output["mode"]!.GetValue<string>(), output["uri"]!.GetValue<string>()));

        string Literal(string key) => inputs[key]!["value"]!.GetValue<string>();
    }

    [Fact]
    public void A_registered_models_asset_is_an_asset_input()
    {
        var release = AzureMlSamples.Release() with { ModelsRef = "azureml:wan-models:3", ModelsKind = "model" };
        var spec = AzureMlJobSpecBuilder.Build(AzureMlSamples.Foundation(), release, "wan-1", "d", "azureml://datastores/workspaceblobstore/paths/studio/web-inputs/i.png",
            "azureml://datastores/workspaceblobstore/paths/studio/web-inputs/w.json", new AzureMlRunSettings("p", "", 1, 4, 1, "euler", 8, 8, 5, 81, 16, "i.png"));
        var models = spec.ToArmBody()["properties"]!["inputs"]!["models"]!;
        var foundation = AzureMlSamples.Foundation();
        Assert.Equal(("custom_model", $"{foundation.WorkspaceArmId}/models/wan-models/versions/3"), (models["jobInputType"]!.GetValue<string>(), models["uri"]!.GetValue<string>()));
    }

    private static void Rejected(Func<AzureMlJobSpec, AzureMlJobSpec> change, bool armed = true) =>
        Assert.Throws<AzureMlSafetyException>(() => AzureMlGuard.Validate(change(AzureMlSamples.Spec()), AzureMlSamples.Foundation(), AzureMlSamples.Release(), armed));

    [Fact]
    public void Guard_accepts_the_built_spec() =>
        AzureMlGuard.Validate(AzureMlSamples.Spec(), AzureMlSamples.Foundation(), AzureMlSamples.Release(), true);

    [Fact]
    public void Guard_rejects_when_not_armed() => Rejected(s => s, armed: false);

    [Fact]
    public void Guard_rejects_another_compute()
    {
        Rejected(s => s with { ComputeId = AzureMlSamples.ComputeId.Replace("gpu1", "gpu2") });
        Rejected(s => s with { Experiment = "other" });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(7201)]
    public void Guard_rejects_timeouts_outside_one_to_7200(int seconds) => Rejected(s => s with { TimeoutSeconds = seconds });

    [Fact]
    public void Guard_rejects_more_than_one_instance_or_video()
    {
        Rejected(s => s with { InstanceCount = 2 });
        Rejected(s => s with { Literals = new Dictionary<string, string>(s.Literals) { ["batch_size"] = "2" } });
    }

    [Theory]
    [InlineData("azureml://datastores/workspaceblobstore/paths/other/job/")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/")]
    [InlineData("azureml://datastores/other/paths/video-library/job/")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/../x/")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/j?sas=1")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/j#f")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/j%2e")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/video-library/j\\k")]
    [InlineData("https://stwan.blob.core.windows.net/c/video-library/j/")]
    public void Guard_rejects_output_outside_the_video_library(string output) => Rejected(s => s with { OutputUri = output });

    [Theory]
    [InlineData("azureml://datastores/workspaceblobstore/paths/studio/web-inputs/a.png?sv=sas")]
    [InlineData("https://stwan.blob.core.windows.net/c/studio/web-inputs/a.png?sv=sas")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/elsewhere/a.png")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/studio/web-inputs/../a.png")]
    [InlineData("azureml://datastores/workspaceblobstore/paths/studio/web-inputs/%2e%2e/a.png")]
    public void Guard_rejects_inputs_that_are_not_private_datastore_files(string input)
    {
        Rejected(s => s with { ImageUri = input });
        Rejected(s => s with { WorkflowUri = input });
    }

    [Fact]
    public void Guard_rejects_assets_that_differ_from_the_release()
    {
        Rejected(s => s with { EnvironmentId = "/subscriptions/x/environments/evil/versions/1" });
        Rejected(s => s with { ModelsRef = "azureml://datastores/workspaceblobstore/paths/models/other/" });
        Rejected(s => s with { RepoUri = "azureml://datastores/workspaceblobstore/paths/code/evil/" });
        Rejected(s => s with { Command = s.Command + " && curl evil" });
        Rejected(s => s with { IdentityClientId = "44444444-4444-4444-4444-444444444444" });
    }

    private static JsonNode Server(string timeout = "PT7200S", int instances = 1, string? compute = AzureMlSamples.ComputeId, string name = "wan-abc123abc123") => new JsonObject
    {
        ["name"] = name,
        ["properties"] = new JsonObject
        {
            ["limits"] = new JsonObject { ["timeout"] = timeout },
            ["resources"] = new JsonObject { ["instanceCount"] = instances },
            ["computeId"] = compute,
        },
    };

    [Theory]
    [InlineData("PT7200S")]
    [InlineData("PT2H")]
    [InlineData("PT1H30M")]
    [InlineData("3600")]
    public void Created_job_within_limits_is_verified(string timeout) =>
        AzureMlGuard.VerifyCreated(Server(timeout), AzureMlSamples.Spec(), AzureMlSamples.Foundation());

    [Theory]
    [InlineData("PT7201S", 1, AzureMlSamples.ComputeId, "wan-abc123abc123")]
    [InlineData("P1D", 1, AzureMlSamples.ComputeId, "wan-abc123abc123")]
    [InlineData("garbage", 1, AzureMlSamples.ComputeId, "wan-abc123abc123")]
    [InlineData("PT0S", 1, AzureMlSamples.ComputeId, "wan-abc123abc123")]
    [InlineData("PT7200S", 2, AzureMlSamples.ComputeId, "wan-abc123abc123")]
    [InlineData("PT7200S", 1, null, "wan-abc123abc123")]
    [InlineData("PT7200S", 1, "/subscriptions/x/computes/other", "wan-abc123abc123")]
    [InlineData("PT7200S", 1, AzureMlSamples.ComputeId, "wan-other")]
    public void Created_job_that_cannot_be_proven_safe_is_rejected(string timeout, int instances, string? compute, string name) =>
        Assert.Throws<AzureMlSafetyException>(() => AzureMlGuard.VerifyCreated(Server(timeout, instances, compute, name), AzureMlSamples.Spec(), AzureMlSamples.Foundation()));

    [Fact]
    public void Created_job_without_limits_is_rejected()
    {
        var job = Server();
        job["properties"]!.AsObject().Remove("limits");
        Assert.Throws<AzureMlSafetyException>(() => AzureMlGuard.VerifyCreated(job, AzureMlSamples.Spec(), AzureMlSamples.Foundation()));
    }

    [Fact]
    public void Output_video_selection_prefers_mp4_and_skips_previews()
    {
        Assert.Equal("a.mp4", AzureMlOutput.PickVideo(["notes.txt", "a.webm", "a.mp4", "meta.json"]));
        Assert.Equal("clip.mp4", AzureMlOutput.PickVideo(["preview.mp4", "clip.mp4"]));
        Assert.Equal("b.webm", AzureMlOutput.PickVideo(["a_preview.mp4", "b.webm"]));
        Assert.Equal("only_preview.mp4", AzureMlOutput.PickVideo(["only_preview.mp4", "x.txt"]));
        Assert.Equal("x.webp", AzureMlOutput.PickVideo(["x.webp", "x.json"]));
        Assert.Null(AzureMlOutput.PickVideo(["log.txt", "meta.json"]));
    }

    [Theory]
    [InlineData("NotStarted", "Queued")]
    [InlineData("Queued", "Queued")]
    [InlineData("Starting", "Queued")]
    [InlineData("Preparing", "Queued")]
    [InlineData("Running", "Running")]
    [InlineData("Finalizing", "Running")]
    [InlineData("CancelRequested", "Running")]
    [InlineData("Completed", "Completed")]
    [InlineData("Failed", "Failed")]
    [InlineData("Canceled", "Failed")]
    [InlineData("Cancelled", "Failed")]
    [InlineData("NotResponding", "Failed")]
    public void Azure_ml_states_map_to_job_statuses(string aml, string expected) => Assert.Equal(expected, AzureMlStatus.ToJobStatus(aml));

    [Fact]
    public void Images_are_validated_and_names_sanitized()
    {
        Assert.True(AzureMlImage.IsSupported(AzureMlSamples.Png));
        Assert.True(AzureMlImage.IsSupported([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(AzureMlImage.IsSupported("GIF89a......."u8));
        Assert.Throws<ValidationException>(() => AzureMlImage.Check([new SceneImage("a.png", [1, 2, 3])]));
        Assert.Equal("my-pic-.png", AzureMlImage.SafeName("my pic!.png"));
        Assert.Equal("input.png", AzureMlImage.SafeName("???"));
        Assert.Equal("scan.png", AzureMlImage.SafeName("scan"));
        Assert.True(AzureMlImage.SafeName(new string('a', 300) + ".png").Length <= 120);
    }
}

/// <summary>An Azure ML workspace in memory: a job goes Queued, Running, Completed and then has output files in the store.</summary>
internal sealed class FakeAzureMl(LocalFolderObjectStore store) : IAzureMlClient
{
    public List<AzureMlJobSpec> Created { get; } = [];
    public List<string> Cancelled { get; } = [];
    public string[] Sequence { get; set; } = ["Queued", "Running", "Completed"];
    public string[] OutputFiles { get; set; } = ["ComfyUI_00001_preview.mp4", "ComfyUI_00001_.mp4"];
    public int TimeoutSecondsStored { get; set; } = 7200;
    private readonly Dictionary<string, int> _polls = [];

    public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct)
    {
        Created.Add(spec);
        _polls[spec.Name] = 0;
        return Task.FromResult(Stored(spec.Name, "NotStarted"));
    }

    public async Task<JsonNode> GetJobAsync(string name, CancellationToken ct)
    {
        var spec = Created.Single(s => s.Name == name);
        var poll = _polls[name]++;
        if (poll == 0)
        {
            return Stored(name, "NotStarted"); // the post-create verification read
        }

        var status = Sequence[Math.Min(poll - 1, Sequence.Length - 1)];
        if (status == "Completed")
        {
            foreach (var file in OutputFiles)
            {
                await store.WriteAsync($"video-library/{name}/{file}", new byte[] { 1, 2, 3 }, true, ct);
            }
        }

        return Stored(spec.Name, status);
    }

    private JsonNode Stored(string name, string status) => new JsonObject
    {
        ["name"] = name,
        ["systemData"] = new JsonObject { ["createdAt"] = "2026-01-01T00:00:00Z" },
        ["properties"] = new JsonObject
        {
            ["status"] = status, ["experimentName"] = "comfyui-wan", ["displayName"] = "d",
            ["limits"] = new JsonObject { ["timeout"] = $"PT{TimeoutSecondsStored}S" },
            ["resources"] = new JsonObject { ["instanceCount"] = 1 },
            ["computeId"] = AzureMlSamples.ComputeId,
        },
    };

    public Task CancelJobAsync(string name, CancellationToken ct)
    {
        Cancelled.Add(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JsonNode>>(
        [
            Listed("wan-open1", "Running", "comfyui-wan"), Listed("wan-done", "Completed", "comfyui-wan"), Listed("other", "Running", "comfyui-ltx"),
        ]);

    private static JsonNode Listed(string name, string status, string experiment) => new JsonObject
    {
        ["name"] = name, ["systemData"] = new JsonObject { ["createdAt"] = "2026-01-02T00:00:00Z" },
        ["properties"] = new JsonObject { ["status"] = status, ["experimentName"] = experiment, ["displayName"] = name + "-display" },
    };

    public Task<string?> GetComputeStateAsync(CancellationToken ct) => Task.FromResult<string?>("Succeeded");
}

public sealed class AzureMlWiringTests
{
    [Fact]
    public void Provider_flag_selects_the_backend_and_the_datastore_container()
    {
        using var temp = new TempFolder();
        IGenerationBackend Backend(params (string, string?)[] extra)
        {
            var configuration = AzureMlSamples.Config([.. AzureMlSamples.Settings(), ("Storage:Path", temp.Path), .. extra]);
            var services = new ServiceCollection().AddLogging().AddSingleton(configuration);
            services.AddStudio(configuration, temp.Path);
            var provider = services.BuildServiceProvider();
            var backend = provider.GetRequiredService<IGenerationBackend>();
            if (backend.Provider == "azureml")
            {
                Assert.Equal("Azure Storage stwan/azureml-blobstore", provider.GetRequiredService<StoreSelection>().Label);
            }

            return backend;
        }

        Assert.Equal("azureml", Backend().Provider); // the default
        Assert.Equal("azureml", Backend(("Generation:Provider", "azureml")).Provider);
        Assert.Equal("comfyui", Backend(("Generation:Provider", "comfyui"), ("Storage:Backend", "local")).Provider);
        Assert.Throws<InvalidOperationException>(() => Backend(("Generation:Provider", "bogus")));
    }
}

public sealed class AzureMlGenerationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeTelemetry _telemetry = new();

    public void Dispose() => _temp.Dispose();

    private (GenerationService Service, FakeAzureMl Aml, LocalFolderObjectStore Store, AzureMlBackend Backend) Create(bool armed = true, bool released = true)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_temp.Path, "store"));
        var aml = new FakeAzureMl(store);
        var events = new FakeEvents();
        var records = new JobRecords(store, events, NullLogger<JobRecords>.Instance);
        var workflows = new FileWorkflowSource(TestPaths.Workflows);
        var generation = Options.Create(new GenerationOptions());
        var gate = AzureMlSamples.Gate(armed, out _);
        var backend = new AzureMlBackend(store, aml, AzureMlSamples.Foundation(), released ? AzureMlSamples.Release() : null, gate, workflows, _telemetry, records, generation,
            Options.Create(new AzureMlOptions { PollSeconds = 0.001 }), TimeProvider.System);
        return (new GenerationService(store, backend, workflows, records, generation, TimeProvider.System, NullLogger<GenerationService>.Instance), aml, store, backend);
    }

    private static SubmitRequest Request(string? mode = null) =>
        new(["leaking valve"], "blurry", "1:1", 5, 1, [new SceneImage("My Image.png", AzureMlSamples.Png)], mode);

    private static async Task RunAllAsync(GenerationService service)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var queued = service.OpenJobs().Count;
        var reader = service.DequeueAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        for (var i = 0; i < queued; i++)
        {
            Assert.True(await reader.MoveNextAsync());
            await service.ProcessAsync(reader.Current, cts.Token);
        }
    }

    [Theory]
    [InlineData("prime", 20, 3.5, "euler", 81, 16)]
    [InlineData("draft", 4, 1.0, "euler", 81, 16)]
    [InlineData("turbo", 20, 5.0, "uni_pc", 121, 24)]
    public async Task A_run_goes_queued_running_completed_and_lands_in_the_gallery(string mode, int steps, double cfg, string sampler, int length, int fps)
    {
        var (service, aml, store, _) = Create();
        var batch = await service.SubmitAsync(Request(mode));
        var job = service.FindJob(batch.Jobs[0].Name)!;
        Assert.StartsWith("wan-", job.Name);
        Assert.Equal(JobStatus.Queued, job.Status);

        await RunAllAsync(service);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(job.Name, job.RemoteId);
        Assert.Contains("ml.azure.com/runs/" + job.Name, job.StudioUrl);
        var spec = Assert.Single(aml.Created);
        Assert.Equal(job.Name, spec.Name);
        Assert.Equal(steps.ToString(), spec.Literals["steps"]);
        Assert.Equal(cfg.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture), spec.Literals["cfg"]);
        Assert.Equal(sampler, spec.Literals["sampler_name"]);
        Assert.Equal(length.ToString(), spec.Literals["length"]);
        Assert.Equal(fps.ToString(), spec.Literals["fps"]);
        Assert.Equal("My-Image.png", spec.Literals["input_image_filename"]);
        Assert.Empty(aml.Cancelled);

        // Inputs were uploaded to the datastore container, the workflow is the mode's own graph.
        var inputs = (await store.ListAsync("studio/web-inputs")).ToList();
        Assert.Equal(2, inputs.Count);
        Assert.EndsWith("/workflow.json", inputs.Single(k => k.EndsWith("workflow.json")));
        Assert.Matches(@"^studio/web-inputs/\d{8}/\d{14}-[0-9a-f]{10}-My-Image\.png$", inputs.Single(k => !k.EndsWith("workflow.json")));
        var workflow = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(store.Root, inputs.Single(k => k.EndsWith("workflow.json")))))!.AsObject();
        Assert.Contains("leaking valve", workflow.ToJsonString());

        var item = Assert.Single((await service.GetGalleryAsync()).Items);
        Assert.Equal(mode, item.Mode);
        Assert.Equal($"/api/media/video-library/{job.Name}/ComfyUI_00001_.mp4", item.VideoUrl);
        Assert.Equal("azureml", item.Timing!.Params!.Provider);
        Assert.True(item.Timing.Phases!.ContainsKey("waiting") && item.Timing.Phases.ContainsKey("run") && item.Timing.Phases.ContainsKey("azureml_queue"));
        Assert.Equal(["waiting", "azureml_queue", "run"], _telemetry.Jobs.Single().Spans.Select(s => s.Name).ToArray());
        Assert.NotNull(await store.ReadAsync($"video-library-jobs/{job.Name}.json"));
    }

    [Fact]
    public async Task A_failed_azure_ml_job_fails_the_studio_job()
    {
        var (service, aml, _, _) = Create();
        aml.Sequence = ["Queued", "Running", "Failed"];
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        var job = service.FindJob(batch.Jobs[0].Name)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("Failed", job.Error);
        Assert.Empty((await service.GetGalleryAsync()).Items);
    }

    [Fact]
    public async Task Missing_output_video_fails_the_job()
    {
        var (service, aml, _, _) = Create();
        aml.OutputFiles = ["log.txt"];
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        Assert.Contains("no video", service.FindJob(batch.Jobs[0].Name)!.Error);
    }

    [Fact]
    public async Task An_unprovable_timeout_cancels_once_and_never_resubmits()
    {
        var (service, aml, _, _) = Create();
        aml.TimeoutSecondsStored = 86400;
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        var job = service.FindJob(batch.Jobs[0].Name)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("safety", job.Error);
        Assert.Single(aml.Created);
        Assert.Equal([job.Name], aml.Cancelled);
        Assert.Null(job.RemoteId);
    }

    [Fact]
    public async Task Submitting_while_not_armed_or_not_prepared_is_a_conflict()
    {
        await Assert.ThrowsAsync<ConflictException>(() => Create(armed: false).Service.SubmitAsync(Request()));
        await Assert.ThrowsAsync<ConflictException>(() => Create(released: false).Service.SubmitAsync(Request()));
    }

    [Fact]
    public async Task Stop_between_submissions_halts_the_job_without_creating_anything()
    {
        var gate = AzureMlSamples.Gate(true, out var arm);
        var store = new LocalFolderObjectStore(Path.Combine(_temp.Path, "store"));
        var aml = new FakeAzureMl(store);
        var records = new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance);
        var workflows = new FileWorkflowSource(TestPaths.Workflows);
        var backend = new AzureMlBackend(store, aml, AzureMlSamples.Foundation(), AzureMlSamples.Release(), gate, workflows,
            _telemetry, records, Options.Create(new GenerationOptions()), Options.Create(new AzureMlOptions { PollSeconds = 0.001 }), TimeProvider.System);
        var service = new GenerationService(store, backend, workflows, records, Options.Create(new GenerationOptions()), TimeProvider.System, NullLogger<GenerationService>.Instance);

        var batch = await service.SubmitAsync(Request());
        arm(false);
        await RunAllAsync(service);

        Assert.Empty(aml.Created);
        Assert.Equal(JobStatus.Failed, service.FindJob(batch.Jobs[0].Name)!.Status);
        Assert.Contains("not armed", service.FindJob(batch.Jobs[0].Name)!.Error);
    }

    [Fact]
    public async Task Bad_images_are_rejected_before_anything_is_queued()
    {
        var (service, aml, _, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() => service.SubmitAsync(new SubmitRequest(["p"], "", "1:1", 5, 1, [new SceneImage("a.png", [1, 2, 3])])));
        Assert.Empty(aml.Created);
        Assert.Empty(service.OpenJobs());
    }

    [Fact]
    public async Task A_restart_resumes_a_recorded_azure_ml_job()
    {
        var (service, aml, store, _) = Create();
        var spec = AzureMlSamples.Spec("wan-resume000001");
        await aml.CreateJobAsync(spec, default);
        await aml.GetJobAsync(spec.Name, default); // the verification read
        var job = new GenerationJob { Name = spec.Name, DisplayName = "scene 1 take 1", Status = JobStatus.Running, RemoteId = spec.Name, Mode = "prime", Context = new JobContext("p", 8, 8, 5, 1) };
        var orphan = new GenerationJob { Name = "wan-orphan0000001", DisplayName = "x", Status = JobStatus.Queued };
        foreach (var record in new[] { job, orphan })
        {
            await store.WriteAsync($"video-library-jobs/{record.Name}.json", VideoSafetyStudio.Application.Json.ToBytes(record));
        }

        await service.RecoverAsync();
        for (var i = 0; i < 400 && service.FindJob(spec.Name)!.Status != JobStatus.Completed; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(JobStatus.Completed, service.FindJob(spec.Name)!.Status);
        Assert.Equal(JobStatus.Failed, service.FindJob(orphan.Name)!.Status);
        Assert.Single(aml.Created); // resuming never submits again
        Assert.Equal("ComfyUI_00001_.mp4", Assert.Single((await service.GetGalleryAsync()).Items).VideoUrl.Split('/')[^1]);
    }

    [Fact]
    public async Task Many_jobs_are_all_created_up_front_and_complete()
    {
        var (service, aml, _, backend) = Create();
        Assert.Equal(25, backend.MaxConcurrency);
        var batch = await service.SubmitAsync(new SubmitRequest(["a", "b"], "", "1:1", 5, 3, [new SceneImage("i.png", AzureMlSamples.Png), null]));
        Assert.Equal(6, batch.Jobs.Count);
        await RunAllAsync(service);
        Assert.Equal(6, aml.Created.Count);
        Assert.All(batch.Jobs, j => Assert.Equal(JobStatus.Completed, j.Status));
        Assert.Equal(6, aml.Created.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public async Task Status_mode_availability_and_open_jobs()
    {
        var (service, _, _, backend) = Create();
        var status = JsonSerializer.SerializeToElement(await service.StatusAsync("ignored", default), VideoSafetyStudio.Application.Json.Options);
        Assert.Equal("azureml", status.GetProperty("mode").GetString());
        Assert.Equal("mlw-wan", status.GetProperty("workspace").GetString());
        Assert.Equal("stwan", status.GetProperty("storage").GetString());
        Assert.Equal("gpu1", status.GetProperty("compute").GetString());
        Assert.Equal("Succeeded", status.GetProperty("computeState").GetString());
        Assert.True(status.GetProperty("prepared").GetBoolean());
        Assert.True(status.GetProperty("armed").GetBoolean());
        Assert.True(status.GetProperty("generationEnabled").GetBoolean());
        Assert.Equal("v7", status.GetProperty("version").GetString());
        Assert.Equal(120, status.GetProperty("maxJobMinutes").GetInt32());
        Assert.Equal(64, status.GetProperty("maxUploadMb").GetInt32());
        Assert.Equal(16, status.GetProperty("profile").GetProperty("fps").GetInt32());

        Assert.All(WanModes.All, mode => Assert.True(service.ModeAvailable(mode)));
        Assert.False(Create(released: false).Service.ModeAvailable(WanModes.Get("prime")));

        var open = Assert.Single(await service.OpenJobsAsync(default));
        var json = JsonSerializer.SerializeToElement(open, VideoSafetyStudio.Application.Json.Options);
        Assert.Equal("wan-open1", json.GetProperty("name").GetString());
        Assert.Equal("wan-open1-display", json.GetProperty("display_name").GetString());
        Assert.Equal("Running", json.GetProperty("status").GetString());
        Assert.StartsWith("https://ml.azure.com/runs/wan-open1", json.GetProperty("studio_url").GetString());
        Assert.Equal("2026-01-02T00:00:00Z", json.GetProperty("created_at").GetString());
        _ = backend;
    }

    [Fact]
    public async Task Disarmed_status_disables_generation()
    {
        var (service, _, _, _) = Create(armed: false);
        var status = JsonSerializer.SerializeToElement(await service.StatusAsync("x", default), VideoSafetyStudio.Application.Json.Options);
        Assert.False(status.GetProperty("armed").GetBoolean());
        Assert.False(status.GetProperty("generationEnabled").GetBoolean());
    }
}
