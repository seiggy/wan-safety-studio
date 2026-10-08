using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Infrastructure.Trainers;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class TrainingServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly VisionService _vision;

    public TrainingServiceTests()
    {
        _vision = new VisionService(new LocalFolderObjectStore(Path.Combine(_temp.Path, "s")), new FakeMedia(), TimeProvider.System);
        Directory.CreateDirectory(Path.Combine(_temp.Path, "trainers", "yolox"));
        File.WriteAllText(Path.Combine(_temp.Path, "trainers", "yolox", "trainer.json"),
            """{"name":"yolox","version":"0.1.0","image":"wan-trainer-yolox:dev","licenseCode":"Apache-2.0","licenseWeights":"Apache-2.0","presets":["nano","tiny","s"],"defaultPreset":"nano"}""");
        Directory.CreateDirectory(Path.Combine(_temp.Path, "trainers", "_template"));
        File.WriteAllText(Path.Combine(_temp.Path, "trainers", "_template", "trainer.json"), """{"name":"my-trainer"}""");
    }

    public void Dispose() => _temp.Dispose();

    private FileTrainerRegistry Registry() =>
        new(Microsoft.Extensions.Options.Options.Create(new TrainerOptions { BuiltIn = Path.Combine(_temp.Path, "trainers") }));

    private async Task SeedAsync()
    {
        await _vision.CreateProjectAsync("demo", ["leak"]);
        var path = Path.Combine(_temp.Path, "in.mp4");
        foreach (var content in new[] { "a", "b" })
        {
            await File.WriteAllTextAsync(path, content);
            var id = await _vision.ImportVideoAsync("demo", path, new VideoOrigin("upload"));
            await _vision.ExtractAsync("demo", id, 2);
            await _vision.PutLabelsAsync("demo", id, "000001", true, [new Box("leak", 1, 1, 5, 5)]);
        }

        await _vision.FreezeDatasetAsync("demo", 0.5, 1);
    }

    private static async Task<TrainingRun> WaitAsync(TrainingService service, string id)
    {
        await service.WhenIdleAsync();
        for (var i = 0; i < 200; i++)
        {
            var run = await service.GetAsync("demo", id);
            if (run.Status is RunStatus.Completed or RunStatus.Failed)
            {
                return run;
            }

            await Task.Delay(25);
        }

        var last = await service.GetAsync("demo", id);
        throw new TimeoutException($"stuck in {last.Status}: {last.Error}");
    }

    [Fact]
    public void Repo_trainers_are_discovered()
    {
        var found = new FileTrainerRegistry(Microsoft.Extensions.Options.Options.Create(new TrainerOptions { BuiltIn = Path.Combine(TestPaths.RepoRoot, "trainers") })).All();
        Assert.Equal(["yolox"], found.Select(m => m.Name));
    }

    [Fact]
    public void Registry_lists_trainers_and_skips_templates()
    {
        var found = Registry().All();
        Assert.Equal(["yolox"], found.Select(m => m.Name));
        Assert.Equal(["nano", "tiny", "s"], found[0].Presets);
        Assert.Throws<ValidationException>(() => Registry().Get("missing"));
    }

    [Fact]
    public async Task Run_materializes_the_dataset_runs_the_image_and_records_outputs()
    {
        await SeedAsync();
        var compute = new FakeCompute();
        var service = new TrainingService(_vision, Registry(), compute, new FakeEvents(), TimeProvider.System);
        var started = await service.StartAsync("demo", "v001", "yolox", "tiny", 5);
        var done = await WaitAsync(service, started.Id);

        Assert.Equal((RunStatus.Completed, 0.5), (done.Status, done.Metrics!.Map50));
        var call = Assert.Single(compute.Calls);
        Assert.Equal("wan-trainer-yolox:dev", call.Image);
        Assert.Equal(["all", "--preset", "tiny", "--epochs", "5"], call.Args);
        Assert.Equal(["classes.json", "images", "train.json", "val.json"], call.DataEntries);
        Assert.True(await _vision.Store.ExistsAsync($"vision/projects/demo/runs/{started.Id}/out/model.onnx"));
        Assert.Single(await service.ListAsync("demo"));
    }

    [Fact]
    public async Task Failure_is_recorded_and_the_node_lock_is_released()
    {
        await SeedAsync();
        var service = new TrainingService(_vision, Registry(), new FakeCompute(code: 3), new FakeEvents(), TimeProvider.System);
        var run = await service.StartAsync("demo", "v001", "yolox", null, null);
        Assert.Equal(RunStatus.Failed, (await WaitAsync(service, run.Id)).Status);
        var second = await service.StartAsync("demo", "v001", "yolox", null, null); // lock was released
        await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("demo", "v001", "yolox", null, null)); // busy now
        await WaitAsync(service, second.Id);
    }
}

public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "vss-api-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Backend"] = "local",
                ["Generation:Provider"] = "comfyui",
                ["Storage:Path"] = Folder,
                ["ComfyUi:Url"] = "http://127.0.0.1:1",
                ["FEATURE_DATASET_BUILDER_ENABLED"] = "true",
            }));
        }
    }

    private readonly HttpClient _client;
    private readonly Factory _factory;

    public ApiTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Session_status_and_health()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/session")).StatusCode);
        var status = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/status");
        Assert.Equal("local", status.GetProperty("mode").GetString());
        Assert.False(status.GetProperty("generationEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Errors_map_to_status_codes_and_the_error_body()
    {
        var created = await _client.PostAsJsonAsync("/api/vision/projects", new { name = "demo", classes = new[] { "leak" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/vision/projects", new { name = "demo", classes = new[] { "leak" } })).StatusCode);
        var bad = await _client.PostAsJsonAsync("/api/vision/projects", new { name = "BAD", classes = new[] { "leak" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("lowercase", (await bad.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/vision/projects/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/media/vision/projects/demo/project.json")).StatusCode);
        var trainers = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/vision/trainers");
        Assert.Equal("yolox", trainers.GetProperty("trainers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unknown_jobs_read_as_failed_so_stale_browsers_stop_waiting()
    {
        var job = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/jobs/local-gone");
        Assert.Equal("Failed", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Events_stream_starts_with_ready_and_content_type_is_sse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token), Encoding.UTF8);
        Assert.Equal("event: ready", await reader.ReadLineAsync(cts.Token));
    }
}
