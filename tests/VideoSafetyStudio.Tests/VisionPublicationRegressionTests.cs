using System.Text.Json;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Tests;

public sealed class VisionPublicationRegressionTests
{
    [Fact]
    public async Task Simultaneous_create_only_reservations_retry_without_mixing_payloads()
    {
        using var workspace = new VisionRegressionWorkspace();
        await workspace.SeedAsync();
        var bothReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contenders = 0;
        workspace.Store.BeforeWrite = async (key, _, ct) =>
        {
            if (key.EndsWith("/datasets/v001/.reserved", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref contenders) == 2) bothReady.TrySetResult();
                await bothReady.Task.WaitAsync(ct);
            }
        };
        var competitor = new VisionService(workspace.Store, new Support.FakeMedia(), TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var results = await Task.WhenAll(workspace.Vision.FreezeDatasetAsync("demo", seed: 1, ct: timeout.Token),
            competitor.FreezeDatasetAsync("demo", seed: 2, ct: timeout.Token));
        Assert.Equal(["v001", "v002"], results.Select(result => result.Version).Order());
        Assert.Equal(2, (await workspace.Vision.ListDatasetsAsync("demo")).Count);
        foreach (var manifest in results)
        {
            var raw = await workspace.Store.ReadAsync(VisionService.Key("demo", $"datasets/{manifest.Version}/train.json"));
            Assert.All(Json.From<CocoDataset>(raw!)!.Images, image => Assert.Contains(image.Video, manifest.Videos.Train));
        }
    }

    [Fact]
    public async Task Interrupted_freeze_is_unlisted_and_next_freeze_skips_its_version()
    {
        using var workspace = new VisionRegressionWorkspace();
        await workspace.SeedAsync();
        workspace.Store.BeforeWrite = (key, _, _) =>
            key.EndsWith("/v001/val.json", StringComparison.Ordinal)
                ? Task.FromException(new IOException("publication interrupted"))
                : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => workspace.Vision.FreezeDatasetAsync("demo"));
        Assert.Empty(await workspace.Vision.ListDatasetsAsync("demo"));
        var abandoned = await workspace.Store.ReadAsync(VisionService.Key("demo", "datasets/v001/train.json"));
        Assert.NotNull(abandoned);

        var complete = await workspace.Vision.FreezeDatasetAsync("demo");
        Assert.Equal("v002", complete.Version);
        Assert.Equal("v002", Assert.Single(await workspace.Vision.ListDatasetsAsync("demo")).Version);
        Assert.Equal(abandoned, await workspace.Store.ReadAsync(VisionService.Key("demo", "datasets/v001/train.json")));
    }

    [Fact]
    public async Task Competing_instances_reserve_distinct_versions_and_publish_manifest_last()
    {
        using var workspace = new VisionRegressionWorkspace();
        await workspace.SeedAsync();
        var competing = new VisionService(workspace.Store, new Support.FakeMedia(), TimeProvider.System);
        var firstReserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Store.BeforeWrite = async (key, _, ct) =>
        {
            if (key.EndsWith("/v001/train.json", StringComparison.Ordinal))
            {
                firstReserved.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            if (key.EndsWith("/manifest.json", StringComparison.Ordinal))
            {
                var prefix = key[..^"manifest.json".Length];
                Assert.True(await workspace.Store.ExistsAsync(prefix + "train.json", ct));
                Assert.True(await workspace.Store.ExistsAsync(prefix + "val.json", ct));
            }
        };

        var first = workspace.Vision.FreezeDatasetAsync("demo", seed: 1);
        try
        {
            await firstReserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(await workspace.Vision.ListDatasetsAsync("demo"));
            var second = await competing.FreezeDatasetAsync("demo", seed: 2);
            Assert.Equal("v002", second.Version);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal("v001", (await first).Version);
        var manifests = await workspace.Vision.ListDatasetsAsync("demo");
        Assert.Equal(2, manifests.Count);
        foreach (var manifest in manifests)
        {
            var expectedTrain = manifest.Videos.Train;
            var raw = await workspace.Store.ReadAsync(VisionService.Key("demo", $"datasets/{manifest.Version}/train.json"));
            var coco = Json.From<CocoDataset>(raw!)!;
            Assert.All(coco.Images, image => Assert.Contains(image.Video, expectedTrain));
        }
    }

    [Fact]
    public async Task Frozen_bytes_survive_label_changes_and_splits_never_share_source_videos()
    {
        using var workspace = new VisionRegressionWorkspace();
        var videos = await workspace.SeedAsync(4);
        await workspace.Vision.FreezeDatasetAsync("demo", 0.5, 17);
        var keys = await workspace.Store.ListAsync(VisionService.Key("demo", "datasets/v001"));
        var old = new Dictionary<string, byte[]>();
        foreach (var key in keys) old[key] = (await workspace.Store.ReadAsync(key))!;

        await workspace.Vision.PutLabelsAsync("demo", videos[0], "000001", true, [new Box("leak", 3, 2, 8, 9)]);
        var changed = await workspace.Vision.FreezeDatasetAsync("demo", 0.5, 17);
        Assert.Equal("v002", changed.Version);
        foreach (var (key, bytes) in old) Assert.Equal(bytes, await workspace.Store.ReadAsync(key));

        using var train = JsonDocument.Parse(old[VisionService.Key("demo", "datasets/v001/train.json")]);
        using var validation = JsonDocument.Parse(old[VisionService.Key("demo", "datasets/v001/val.json")]);
        static HashSet<string> Sources(JsonDocument coco) =>
            coco.RootElement.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("video").GetString()!).ToHashSet();
        Assert.NotEmpty(Sources(train));
        Assert.NotEmpty(Sources(validation));
        Assert.Empty(Sources(train).Intersect(Sources(validation)));
        Assert.Equal(videos.Order(), Sources(train).Union(Sources(validation)).Order());
    }

    [Fact]
    public async Task Competing_extraction_loses_before_media_or_pixels_and_frozen_references_remain_stable()
    {
        var media = new DistinctMedia();
        using var workspace = new VisionRegressionWorkspace(media);
        await workspace.Vision.CreateProjectAsync("demo", ["leak"]);
        var source = Path.Combine(workspace.Root, "source.mp4");
        await File.WriteAllTextAsync(source, "video");
        var video = await workspace.Vision.ImportVideoAsync("demo", source, new VideoOrigin("upload"));
        var reserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workspace.Store.BeforeDownload = async (_, ct) =>
        {
            reserved.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var winner = workspace.Vision.ExtractAsync("demo", video, 2);
        try
        {
            await reserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var competing = new VisionService(workspace.Store, media, TimeProvider.System);
            await Assert.ThrowsAsync<ConflictException>(() => competing.ExtractAsync("demo", video, 7));
            Assert.Equal(0, media.Extractions);
        }
        finally
        {
            release.TrySetResult();
        }

        await winner;
        Assert.Equal(1, media.Extractions);
        await workspace.Vision.PutLabelsAsync("demo", video, "000001", true, [new Box("leak", 0, 0, 100, 50)]);
        await workspace.Vision.FreezeDatasetAsync("demo", 0);
        var frameKey = VisionService.Key("demo", $"frames/{video}/000001.jpg");
        var indexKey = VisionService.Key("demo", $"frames/{video}/index.json");
        var bytes = await workspace.Store.ReadAsync(frameKey);
        var index = await workspace.Store.ReadAsync(indexKey);
        await Assert.ThrowsAsync<ConflictException>(() => workspace.Vision.ExtractAsync("demo", video, 7));
        Assert.Equal(new byte[] { 2 }, bytes);
        Assert.Equal(bytes, await workspace.Store.ReadAsync(frameKey));
        Assert.Equal(index, await workspace.Store.ReadAsync(indexKey));
        var coco = Json.From<CocoDataset>((await workspace.Store.ReadAsync(VisionService.Key("demo", "datasets/v001/train.json")))!)!;
        Assert.Equal(bytes, await workspace.Store.ReadAsync(VisionService.Key("demo", Assert.Single(coco.Images).FileName)));
    }

    private sealed class DistinctMedia : IMediaTools
    {
        public int Extractions { get; private set; }
        public Task<VideoInfo> ProbeAsync(string video, CancellationToken ct = default) => Task.FromResult(new VideoInfo(100, 50, 1, 10));
        public async Task<IReadOnlyList<string>> ExtractFramesAsync(string video, string outDirectory, double fps, int limit, CancellationToken ct = default)
        {
            Extractions++;
            Directory.CreateDirectory(outDirectory);
            var path = Path.Combine(outDirectory, "000001.jpg");
            await File.WriteAllBytesAsync(path, [(byte)fps], ct);
            return [path];
        }
    }
}
