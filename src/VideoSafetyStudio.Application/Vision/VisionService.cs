using System.Security.Cryptography;
using System.Text;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Application.Vision;

/// <summary>Projects, source videos, frames, labels and frozen COCO dataset versions.</summary>
public sealed class VisionService(IObjectStore store, IMediaTools media, TimeProvider time)
{
    public const string Root = "vision/projects/";
    public const string Gallery = "video-library/";
    public const int MaxFrames = 2000;

    public IObjectStore Store => store;

    public static string Key(string project, params string[] parts) =>
        Root + string.Join('/', [Names.Check(project, "project"), .. parts]);

    private async Task<T> DocAsync<T>(string key, string what, CancellationToken ct)
    {
        var data = await store.ReadAsync(key, ct) ?? throw new NotFoundException($"{what} not found.");
        return Json.From<T>(data) ?? throw new NotFoundException($"{what} not found.");
    }

    // ---- projects
    public async Task<Project> CreateProjectAsync(string? name, IReadOnlyList<string>? classes, CancellationToken ct = default)
    {
        var project = Project.Create(name, classes);
        try
        {
            await store.WriteAsync(Key(project.Name, "project.json"), Json.ToBytes(project), overwrite: false, ct);
        }
        catch (IOException)
        {
            throw new ConflictException("A project with this name already exists.");
        }

        return project;
    }

    public Task<Project> GetProjectAsync(string name, CancellationToken ct = default) =>
        DocAsync<Project>(Key(name, "project.json"), "Project", ct);

    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken ct = default) =>
        [.. (await store.ListAsync(Root.TrimEnd('/'), ct)).Select(key => key.Split('/')[2]).Distinct().Order(StringComparer.Ordinal)];

    // ---- sources
    public async Task<string> ImportVideoAsync(string project, string path, VideoOrigin origin, CancellationToken ct = default)
    {
        await GetProjectAsync(project, ct);
        var info = await media.ProbeAsync(path, ct);
        var video = await VideoIdAsync(path, ct);
        var key = Key(project, "sources", video, "video.mp4");
        if (!await store.ExistsAsync(key, ct))
        {
            await store.UploadAsync(key, path, overwrite: false, ct);
        }

        var source = new VideoSource(video, origin, info.Width, info.Height, info.Duration, info.Fps);
        await store.WriteAsync(Key(project, "sources", video, "source.json"), Json.ToBytes(source), true, ct);
        return video;
    }

    public async Task<string> ImportGalleryVideoAsync(string project, string blob, CancellationToken ct = default)
    {
        if (!blob.StartsWith(Gallery, StringComparison.Ordinal) || !(blob.EndsWith(".mp4", StringComparison.Ordinal) || blob.EndsWith(".webm", StringComparison.Ordinal)))
        {
            throw new ValidationException("Only generated .mp4/.webm files from the video library can be imported.");
        }

        if (blob.Contains("..", StringComparison.Ordinal))
        {
            throw new ValidationException("Invalid video path.");
        }

        if (!await store.ExistsAsync(blob, ct))
        {
            throw new NotFoundException("Generated video not found.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "vision-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "video.mp4");
            await store.DownloadAsync(blob, path, ct);
            return await ImportVideoAsync(project, path, new VideoOrigin("wan", Blob: blob), ct);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    public async Task<IReadOnlyList<VideoSource>> ListVideosAsync(string project, CancellationToken ct = default)
    {
        await GetProjectAsync(project, ct);
        var videos = new List<VideoSource>();
        foreach (var key in (await store.ListAsync(Key(project, "sources"), ct)).Where(k => k.EndsWith("/source.json", StringComparison.Ordinal)))
        {
            var source = await DocAsync<VideoSource>(key, "Video", ct);
            var index = await store.ReadAsync(Key(project, "frames", source.Id, "index.json"), ct);
            videos.Add(source with { Frames = index is null ? 0 : Json.From<FrameIndex>(index)!.Frames.Count });
        }

        return videos;
    }

    // ---- frames
    public async Task<FrameIndex> ExtractAsync(string project, string video, double fps, CancellationToken ct = default)
    {
        await GetProjectAsync(project, ct);
        if (fps is <= 0 or > 30)
        {
            throw new ValidationException("fps must be above 0 and at most 30.");
        }

        var source = await DocAsync<VideoSource>(Key(project, "sources", video, "source.json"), "Video", ct);
        if (await store.ExistsAsync(Key(project, "frames", video, "index.json"), ct))
        {
            throw new ConflictException("Frames were already extracted for this video; labels depend on them.");
        }

        var temp = Path.Combine(Path.GetTempPath(), "vision-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var local = Path.Combine(temp, "video.mp4");
            await store.DownloadAsync(Key(project, "sources", video, "video.mp4"), local, ct);
            var frames = await media.ExtractFramesAsync(local, Path.Combine(temp, "frames"), fps, MaxFrames, ct);
            foreach (var frame in frames)
            {
                await store.UploadAsync(Key(project, "frames", video, Path.GetFileName(frame)), frame, overwrite: true, ct);
            }

            var index = new FrameIndex(source.Width, source.Height, fps, [.. frames.Select(Path.GetFileNameWithoutExtension).Cast<string>()]);
            await store.WriteAsync(Key(project, "frames", video, "index.json"), Json.ToBytes(index), overwrite: false, ct);
            return index;
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    public Task<FrameIndex> FrameIndexAsync(string project, string video, CancellationToken ct = default) =>
        DocAsync<FrameIndex>(Key(project, "frames", video, "index.json"), "Frames", ct);

    public async Task<byte[]> FrameImageAsync(string project, string video, string frame, CancellationToken ct = default) =>
        await store.ReadAsync(Key(project, "frames", video, Names.CheckFrame(frame) + ".jpg"), ct) ?? throw new NotFoundException("Frame not found.");

    // ---- labels
    public async Task<FrameLabels> GetLabelsAsync(string project, string video, string frame, CancellationToken ct = default)
    {
        var data = await store.ReadAsync(Key(project, "labels", video, Names.CheckFrame(frame) + ".json"), ct);
        return data is null ? FrameLabels.Empty : Json.From<FrameLabels>(data)!;
    }

    public async Task<FrameLabels> PutLabelsAsync(string project, string video, string frame, bool reviewed, IReadOnlyList<Box> boxes, CancellationToken ct = default)
    {
        var classes = (await GetProjectAsync(project, ct)).Classes;
        var index = await FrameIndexAsync(project, video, ct);
        if (!index.Frames.Contains(frame))
        {
            throw new NotFoundException("Frame not found.");
        }

        var labels = new FrameLabels(reviewed, [.. boxes.Select(box => box.Checked(classes, index.Width, index.Height))]);
        await store.WriteAsync(Key(project, "labels", video, frame + ".json"), Json.ToBytes(labels), true, ct);
        return labels;
    }

    // ---- datasets
    /// <summary>Immutable COCO dataset version, split by source video so train and validation never share a scene.</summary>
    public async Task<DatasetManifest> FreezeDatasetAsync(string project, double valFraction = 0.2, int seed = 0, CancellationToken ct = default)
    {
        var classes = (await GetProjectAsync(project, ct)).Classes;
        var videos = (await ListVideosAsync(project, ct)).Where(v => v.Frames > 0).Select(v => v.Id).Order(StringComparer.Ordinal).ToList();
        if (valFraction is < 0 or >= 1)
        {
            throw new ValidationException("Validation fraction must be in [0, 1).");
        }

        var valCount = valFraction > 0 ? (int)Math.Ceiling(videos.Count * valFraction) : 0;
        if (valCount > 0 && videos.Count < 2)
        {
            throw new ValidationException("Splitting needs at least two videos; import another or use a validation fraction of 0.");
        }

        var ranked = videos.OrderBy(v => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{v}"))), StringComparer.Ordinal).ToList();
        var val = ranked.Take(valCount).ToHashSet();
        var trainVideos = videos.Where(v => !val.Contains(v)).ToList();
        var valVideos = val.Order(StringComparer.Ordinal).ToList();
        var train = await BuildCocoAsync(project, trainVideos, classes, ct);
        var validation = await BuildCocoAsync(project, valVideos, classes, ct);
        if (train.Images.Count == 0)
        {
            throw new ValidationException("No reviewed frames in the training split. Review labels first.");
        }

        var existing = (await store.ListAsync(Key(project, "datasets"), ct)).Count(k => k.EndsWith("/manifest.json", StringComparison.Ordinal));
        var version = $"v{existing + 1:D3}";
        var baseKey = Key(project, "datasets", version);
        await store.WriteAsync($"{baseKey}/train.json", Json.ToBytes(train), false, ct);
        await store.WriteAsync($"{baseKey}/val.json", Json.ToBytes(validation), false, ct);

        var warnings = train.Categories
            .Where(c => train.Annotations.All(a => a.CategoryId != c.Id))
            .Select(c => $"No training boxes for class '{c.Name}'.").ToList();
        var manifest = new DatasetManifest(version, classes, seed, valFraction, new DatasetVideos(trainVideos, valVideos),
            new Dictionary<string, SplitCounts>
            {
                ["train"] = new(train.Images.Count, train.Annotations.Count),
                ["val"] = new(validation.Images.Count, validation.Annotations.Count),
            },
            warnings, time.GetUtcNow().ToString("o"));
        await store.WriteAsync($"{baseKey}/manifest.json", Json.ToBytes(manifest), false, ct);
        return manifest;
    }

    public async Task<IReadOnlyList<DatasetManifest>> ListDatasetsAsync(string project, CancellationToken ct = default)
    {
        var manifests = new List<DatasetManifest>();
        foreach (var key in (await store.ListAsync(Key(project, "datasets"), ct)).Where(k => k.EndsWith("/manifest.json", StringComparison.Ordinal)))
        {
            manifests.Add(await DocAsync<DatasetManifest>(key, "Dataset", ct));
        }

        return manifests;
    }

    private async Task<CocoDataset> BuildCocoAsync(string project, IEnumerable<string> videos, IReadOnlyList<string> classes, CancellationToken ct)
    {
        var images = new List<CocoImage>();
        var annotations = new List<CocoAnnotation>();
        foreach (var video in videos)
        {
            var index = await FrameIndexAsync(project, video, ct);
            foreach (var frame in index.Frames)
            {
                var labels = await GetLabelsAsync(project, video, frame, ct);
                if (!labels.Reviewed)
                {
                    continue;
                }

                var imageId = images.Count + 1;
                images.Add(new CocoImage(imageId, $"frames/{video}/{frame}.jpg", index.Width, index.Height, video));
                foreach (var box in labels.Boxes)
                {
                    annotations.Add(new CocoAnnotation(annotations.Count + 1, imageId, classes.ToList().IndexOf(box.Cls) + 1,
                        [box.X, box.Y, box.W, box.H], box.W * box.H, 0));
                }
            }
        }

        return new CocoDataset(images, annotations, [.. classes.Select((name, i) => new CocoCategory(i + 1, name))]);
    }

    private static async Task<string> VideoIdAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return "v" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct))[..12];
    }
}
