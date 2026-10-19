using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

internal sealed class VisionRegressionWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(TestPaths.RepoRoot, ".artifacts", "vision-fixtures", Guid.NewGuid().ToString("N"));
    public InterceptingVisionStore Store { get; }
    public VisionService Vision { get; }

    public VisionRegressionWorkspace(IMediaTools? media = null)
    {
        Directory.CreateDirectory(Root);
        Store = new(new LocalFolderObjectStore(Path.Combine(Root, "store")));
        Vision = new(Store, media ?? new FakeMedia(), TimeProvider.System);
    }

    public async Task<string[]> SeedAsync(int count = 2)
    {
        await Vision.CreateProjectAsync("demo", ["leak"]);
        var videos = new List<string>();
        var path = Path.Combine(Root, "source.mp4");
        for (var i = 0; i < count; i++)
        {
            await File.WriteAllTextAsync(path, $"source {i}");
            var id = await Vision.ImportVideoAsync("demo", path, new VideoOrigin("upload"));
            await Vision.ExtractAsync("demo", id, 2);
            await Vision.PutLabelsAsync("demo", id, "000001", true, [new Box("leak", 1, 1, 5, 5)]);
            videos.Add(id);
        }

        return [.. videos];
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class InterceptingVisionStore(IObjectStore inner) : IObjectStore
{
    public Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? BeforeWrite { get; set; }
    public Func<string, CancellationToken, Task>? BeforeUpload { get; set; }
    public Func<string, CancellationToken, Task>? BeforeDownload { get; set; }
    public Func<string, CancellationToken, Task>? BeforeRead { get; set; }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => inner.OpenReadAsync(key, ct);
    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) => inner.ListAsync(prefix, ct);

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        if (BeforeRead is { } before) await before(key, ct);
        return await inner.ReadAsync(key, ct);
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        if (BeforeWrite is { } before) await before(key, data, ct);
        await inner.WriteAsync(key, data, overwrite, ct);
    }

    public async Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default)
    {
        if (BeforeUpload is { } before) await before(key, ct);
        await inner.UploadAsync(key, path, overwrite, ct);
    }

    public async Task DownloadAsync(string key, string path, CancellationToken ct = default)
    {
        if (BeforeDownload is { } before) await before(key, ct);
        await inner.DownloadAsync(key, path, ct);
    }
}
