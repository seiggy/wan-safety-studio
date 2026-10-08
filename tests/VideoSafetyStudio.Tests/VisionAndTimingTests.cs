using System.Text.Json.Nodes;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class VisionServiceTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly VisionService _service;

    public VisionServiceTests() => _service = new VisionService(new LocalFolderObjectStore(Path.Combine(_temp.Path, "store")), new FakeMedia(), TimeProvider.System);

    public void Dispose() => _temp.Dispose();

    private async Task<string> ImportAsync(string content)
    {
        var path = Path.Combine(_temp.Path, "in.mp4");
        await File.WriteAllTextAsync(path, content);
        return await _service.ImportVideoAsync("demo", path, new VideoOrigin("upload"));
    }

    private async Task<(string A, string B)> PrepareAsync()
    {
        await _service.CreateProjectAsync("demo", ["leak", "fire"]);
        var a = await ImportAsync("a");
        var b = await ImportAsync("b");
        await _service.ExtractAsync("demo", a, 2);
        await _service.ExtractAsync("demo", b, 2);
        return (a, b);
    }

    [Fact]
    public async Task Project_rules()
    {
        await _service.CreateProjectAsync("demo", ["leak"]);
        await Assert.ThrowsAsync<ConflictException>(() => _service.CreateProjectAsync("demo", ["leak"]));
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProjectAsync("Bad Name", ["a1"]));
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProjectAsync("ok-name", []));
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateProjectAsync("ok-name", ["dup", "dup"]));
        Assert.Equal(["demo"], await _service.ListProjectsAsync());
    }

    [Fact]
    public async Task Import_is_idempotent_and_extraction_happens_once()
    {
        var (a, b) = await PrepareAsync();
        Assert.Equal(a, await ImportAsync("a"));
        var videos = (await _service.ListVideosAsync("demo")).ToDictionary(v => v.Id, v => v.Frames);
        Assert.Equal(new Dictionary<string, int> { [a] = 3, [b] = 3 }, videos);
        await Assert.ThrowsAsync<ConflictException>(() => _service.ExtractAsync("demo", a, 2));
    }

    [Fact]
    public async Task Gallery_import_is_restricted_to_the_video_library()
    {
        await _service.CreateProjectAsync("demo", ["leak"]);
        await Assert.ThrowsAsync<ValidationException>(() => _service.ImportGalleryVideoAsync("demo", "vision/projects/other/sources/x/video.mp4"));
        await Assert.ThrowsAsync<ValidationException>(() => _service.ImportGalleryVideoAsync("demo", "video-library/../secret.mp4"));
    }

    [Fact]
    public async Task Label_validation()
    {
        var (a, _) = await PrepareAsync();
        var ok = new Box("leak", 10, 5, 20, 10);
        await _service.PutLabelsAsync("demo", a, "000001", true, [ok]);
        foreach (var bad in new[] { ok with { Cls = "nope" }, ok with { W = 0 }, ok with { X = 95 } })
        {
            await Assert.ThrowsAsync<ValidationException>(() => _service.PutLabelsAsync("demo", a, "000001", true, [bad]));
        }

        await Assert.ThrowsAsync<NotFoundException>(() => _service.PutLabelsAsync("demo", a, "000009", true, []));
    }

    [Fact]
    public async Task Freeze_splits_by_video_and_versions_are_immutable()
    {
        var (a, b) = await PrepareAsync();
        foreach (var video in new[] { a, b })
        {
            foreach (var frame in new[] { "000001", "000002" })
            {
                await _service.PutLabelsAsync("demo", video, frame, true, [new Box("fire", 1, 1, 5, 5)]);
            }

            await _service.PutLabelsAsync("demo", video, "000003", false, []);
        }

        var first = await _service.FreezeDatasetAsync("demo", 0.5, 1);
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), first.Videos.Train.Concat(first.Videos.Val).Order(StringComparer.Ordinal));
        Assert.Equal(new SplitCounts(2, 2), first.Counts["train"]);
        Assert.Contains("No training boxes for class 'leak'.", first.Warnings);
        Assert.Equal("v002", (await _service.FreezeDatasetAsync("demo", 0.5, 1)).Version);
        var val = Json.From<CocoDataset>((await _service.Store.ReadAsync("vision/projects/demo/datasets/v001/val.json"))!)!;
        Assert.Equal(2, val.Annotations[0].CategoryId);
    }

    [Fact]
    public async Task Freeze_needs_two_videos_for_a_split()
    {
        await _service.CreateProjectAsync("demo", ["leak"]);
        var only = await ImportAsync("a");
        await _service.ExtractAsync("demo", only, 2);
        await Assert.ThrowsAsync<ValidationException>(() => _service.FreezeDatasetAsync("demo", 0.2));
    }
}

public class StageTimerTests
{
    [Fact]
    public void Stages_steps_and_cache()
    {
        var workflow = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Workflows, "wan22_i2v.api.json")))!.AsObject();
        var timer = new StageTimer(workflow);
        void Feed(double at, string type, string json = "{}") => timer.Feed(type, System.Text.Json.JsonDocument.Parse(json).RootElement, 100 + at);
        Feed(0, "execution_start");
        Feed(0, "execution_cached", """{"nodes": ["38"]}""");
        Feed(1, "executing", """{"node": "37"}""");
        Feed(11, "executing", """{"node": "57"}""");
        Feed(31, "progress", """{"node": "57", "value": 10, "max": 10}""");
        Feed(31, "executing", """{"node": "58"}""");
        Feed(61, "executing", """{"node": "8"}""");
        Feed(66, "executing", """{"node": "47"}""");
        Feed(70, "execution_success");

        Assert.Equal([("load_models", "38", 100.0)], timer.CachedIntervals());
        var summary = timer.Summary();
        Assert.Equal(70, summary.ExecutionSeconds);
        Assert.Equal(10, summary.Stages["load_models"].Seconds);
        Assert.Equal(["37", "38"], summary.Stages["load_models"].Nodes);
        Assert.Equal((20, 2), (summary.Stages["sample_high_noise"].Seconds, summary.Stages["sample_high_noise"].SecondsPerStep));
        Assert.Equal(30, summary.Stages["sample_low_noise"].Seconds);
        Assert.Equal((5, 4), (summary.Stages["vae_decode"].Seconds, summary.Stages["save_video"].Seconds));
    }
}
