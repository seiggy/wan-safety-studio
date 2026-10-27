using System.Text.Json.Nodes;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Application.Abstractions;

/// <summary>Key/value blob storage: a local folder (or mounted share), Azurite, or Azure Blob.</summary>
public interface IObjectStore
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>A seekable stream the caller disposes, or null when the blob does not exist. Avoids loading large media into memory.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default);

    Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default);

    Task DownloadAsync(string key, string path, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);
}

public interface IMediaTools
{
    Task<VideoInfo> ProbeAsync(string video, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ExtractFramesAsync(string video, string outDirectory, double fps, int limit, CancellationToken ct = default);
}

/// <summary>Runs a trainer image to completion. Local Docker today; Azure ML on the shared node later.</summary>
public interface IComputeTarget
{
    string Name { get; }

    Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default);
}

/// <summary>
/// A compute target whose job outlives this process (Azure ML). The run's remote identity is handed to <paramref name="remember"/> and saved
/// before anything is created; a restart then follows or settles that job instead of creating another. Output is copied into
/// <paramref name="outDirectory"/> even for a failed job. Cancelling the token only detaches: the remote job keeps running.
/// </summary>
public interface IDurableComputeTarget : IComputeTarget
{
    Task<int> RunAsync(TrainingRun run, TrainerManifest manifest, string dataDirectory, string outDirectory, Func<RemoteRun, Task> remember, CancellationToken ct);

    Task<int> ResumeAsync(TrainingRun run, string outDirectory, CancellationToken ct);

    /// <summary>Cancels once and returns only after fresh proof the job is terminal; throws when that cannot be proven.</summary>
    Task CancelAsync(RemoteRun remote, CancellationToken ct);
}

public interface ITrainerRegistry
{
    IReadOnlyList<TrainerManifest> All();

    TrainerManifest Get(string name);
}

public sealed record OutputRef(string Filename, string Subfolder = "", string Type = "output");

public interface IComfyUiClient
{
    string BaseUrl { get; }

    Task<bool> IsReachableAsync(CancellationToken ct = default);

    Task<string> UploadImageAsync(string name, byte[] data, CancellationToken ct = default);

    /// <summary>Queues the prompt and waits for it. When a timer is given, ComfyUI's websocket feeds it every event.</summary>
    Task<JsonNode> RunAsync(JsonObject workflow, StageTimer timer, Func<string, Task> onQueued, Action onEvent, CancellationToken ct = default);

    Task<JsonNode> WaitAsync(string promptId, CancellationToken ct = default);

    Task<byte[]> ViewAsync(OutputRef reference, CancellationToken ct = default);
}

/// <summary>How a finished video gets from ComfyUI to this server.</summary>
public interface IVideoRetrieval
{
    Task<byte[]> FetchAsync(IComfyUiClient client, OutputRef reference, CancellationToken ct = default);
}

public interface IWorkflowSource
{
    string Description { get; }

    bool Exists(string template);

    /// <summary>Loads an API-format workflow template by file name; throws <see cref="MissingWorkflowException"/> when absent.</summary>
    Task<JsonObject> LoadAsync(string template, CancellationToken ct = default);
}

/// <summary>Pushes state changes to connected clients (SSE).</summary>
public interface IEventPublisher
{
    void Publish(string type, object payload);
}

/// <summary>A named wall-clock interval of a job that ComfyUI does not report itself (waiting in our queue, waiting in ComfyUI's queue).</summary>
public sealed record PhaseSpan(string Name, double Start, double End);

public interface IGenerationTelemetry
{
    void Emit(string jobName, GenerationParams parameters, StageTimer timer, IReadOnlyDictionary<string, double> phases,
        IReadOnlyList<PhaseSpan> spans, double jobStart, double jobEnd, bool ok);
}

public class ComfyUiException(string message) : Exception(message);

public class MissingWorkflowException(string message) : Exception(message);
