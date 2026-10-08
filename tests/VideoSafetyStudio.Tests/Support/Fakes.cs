using System.Text.Json.Nodes;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Tests.Support;

public sealed class FakeMedia : IMediaTools
{
    public Task<VideoInfo> ProbeAsync(string video, CancellationToken ct = default) => Task.FromResult(new VideoInfo(100, 50, 2.0, 10.0));

    public Task<IReadOnlyList<string>> ExtractFramesAsync(string video, string outDirectory, double fps, int limit, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDirectory);
        var frames = Enumerable.Range(1, 3).Select(i => Path.Combine(outDirectory, $"{i:D6}.jpg")).ToList();
        foreach (var frame in frames)
        {
            File.WriteAllBytes(frame, [1, 2, 3]);
        }

        return Task.FromResult<IReadOnlyList<string>>(frames);
    }
}

public sealed class FakeEvents : IEventPublisher
{
    public List<(string Type, string Json)> Items { get; } = [];

    public void Publish(string type, object payload)
    {
        lock (Items)
        {
            Items.Add((type, System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType(), Application.Json.Options)));
        }
    }
}

public sealed class FakeTelemetry : IGenerationTelemetry
{
    public List<string> Jobs { get; } = [];

    public void Emit(string jobName, GenerationParams parameters, StageTimer timer, IReadOnlyDictionary<string, double> phases, double jobStart, double jobEnd, bool ok) => Jobs.Add(jobName);
}

public sealed class FakeWorkflow(JsonObject template, bool exists = true) : IWorkflowSource
{
    public string Description => "fake";

    public bool Exists => exists;

    public Task<JsonObject> LoadAsync(CancellationToken ct = default) =>
        exists ? Task.FromResult((JsonObject)template.DeepClone()) : throw new MissingWorkflowException("No workflow.");
}

public sealed class FakeCompute(int code = 0) : IComputeTarget
{
    public string Name => "fake";

    public List<(string Image, string[] Args, string[] DataEntries)> Calls { get; } = [];

    public Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default)
    {
        Calls.Add((image, [.. args], [.. Directory.EnumerateFileSystemEntries(dataDirectory).Select(Path.GetFileName).Order().Cast<string>()]));
        File.WriteAllText(Path.Combine(outDirectory, "metrics.json"), """{"map50": 0.5, "map50_95": 0.3, "per_class": {}}""");
        File.WriteAllBytes(Path.Combine(outDirectory, "model.onnx"), [1]);
        File.WriteAllText(logPath, "log");
        return Task.FromResult(code);
    }
}

public static class TestPaths
{
    public static string RepoRoot { get; } = Find();

    public static string Workflows => Path.Combine(RepoRoot, "src", "VideoSafetyStudio.Server", "Workflows");

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vss-test-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
