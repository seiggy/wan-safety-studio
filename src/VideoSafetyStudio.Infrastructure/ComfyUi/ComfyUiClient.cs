using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Domain.Generation;

namespace VideoSafetyStudio.Infrastructure.ComfyUi;

public sealed class ComfyUiOptions
{
    public const string Section = "ComfyUi";

    public string Url { get; set; } = "http://localhost:8188";

    /// <summary>api (download through ComfyUI /view) | share (read ComfyUI's output folder from a mounted path)</summary>
    public string Retrieval { get; set; } = "api";

    public string? OutputDir { get; set; }
    public string? Workflow { get; set; }
    public string? SmbUser { get; set; }
    public string? SmbPassword { get; set; }
    public double PollSeconds { get; set; } = 3;
    public double TimeoutSeconds { get; set; } = 3600;
}

public sealed class ComfyUiClient(HttpClient http, IOptions<ComfyUiOptions> options, TimeProvider time) : IComfyUiClient
{
    private readonly string _clientId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    private double Now => time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    public string BaseUrl => options.Value.Url.TrimEnd('/');

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync(BaseUrl + "/system_stats", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<string> UploadImageAsync(string name, byte[] data, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(data), "image", name },
            { new StringContent("true"), "overwrite" },
        };
        using var response = await http.PostAsync(BaseUrl + "/upload/image", form, ct);
        var result = await ReadAsync(response, "POST /upload/image", ct);
        var subfolder = result["subfolder"]?.GetValue<string>();
        return (string.IsNullOrEmpty(subfolder) ? "" : subfolder + "/") + result["name"]!.GetValue<string>();
    }

    private async Task<string> QueueAsync(JsonObject workflow, CancellationToken ct)
    {
        var body = new JsonObject { ["prompt"] = workflow.DeepClone(), ["client_id"] = _clientId };
        using var response = await http.PostAsync(BaseUrl + "/prompt", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        return (await ReadAsync(response, "POST /prompt", ct))["prompt_id"]!.GetValue<string>();
    }

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyUiException($"ComfyUI {what} -> {(int)response.StatusCode}: {(text.Length > 500 ? text[..500] : text)}");
        }

        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    public async Task<JsonNode> RunAsync(JsonObject workflow, StageTimer timer, Action<string> onQueued, Action onEvent, CancellationToken ct = default)
    {
        // The websocket is opened BEFORE queueing so no event is missed.
        using var socket = new ClientWebSocket();
        var wsUri = new UriBuilder(BaseUrl) { Scheme = BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws", Path = "/ws", Query = "clientId=" + _clientId }.Uri;
        try
        {
            await socket.ConnectAsync(wsUri, ct);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException)
        {
            var id = await QueueAsync(workflow, ct); // no websocket: no per-stage timing
            onQueued(id);
            return await WaitAsync(id, ct);
        }

        var promptId = await QueueAsync(workflow, ct);
        onQueued(promptId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            while (!timer.Done && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveTextAsync(socket, deadline.Token);
                if (message is null)
                {
                    continue; // binary preview frame
                }

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                var data = root.TryGetProperty("data", out var d) ? d : default;
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("prompt_id", out var pid) && pid.GetString() != promptId)
                {
                    continue;
                }

                timer.Feed(root.GetProperty("type").GetString() ?? "", data, Now);
                onEvent();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ComfyUiException("Timed out waiting for ComfyUI.");
        }

        return await WaitAsync(promptId, ct);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var text = new MemoryStream();
        WebSocketReceiveResult result;
        var isText = true;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            isText = result.MessageType == WebSocketMessageType.Text;
            if (isText)
            {
                text.Write(buffer, 0, result.Count);
            }
        }
        while (!result.EndOfMessage);

        return isText ? Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length) : null;
    }

    public async Task<JsonNode> WaitAsync(string promptId, CancellationToken ct = default)
    {
        var deadline = Now + options.Value.TimeoutSeconds;
        while (Now < deadline)
        {
            using var response = await http.GetAsync($"{BaseUrl}/history/{promptId}", ct);
            var history = await ReadAsync(response, "GET /history", ct);
            if (history[promptId] is JsonObject entry)
            {
                var status = entry["status"];
                if (status?["status_str"]?.GetValue<string>() == "error")
                {
                    throw new ComfyUiException("ComfyUI reported an execution error for this prompt.");
                }

                if (status?["completed"]?.GetValue<bool>() == true || entry["outputs"] is JsonObject { Count: > 0 })
                {
                    return entry;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), ct);
        }

        throw new ComfyUiException("Timed out waiting for ComfyUI.");
    }

    public async Task<byte[]> ViewAsync(OutputRef reference, CancellationToken ct = default)
    {
        var query = $"filename={Uri.EscapeDataString(reference.Filename)}&subfolder={Uri.EscapeDataString(reference.Subfolder)}&type={Uri.EscapeDataString(reference.Type)}";
        using var response = await http.GetAsync($"{BaseUrl}/view?{query}", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : throw new ComfyUiException($"ComfyUI /view -> {(int)response.StatusCode}");
    }
}

/// <summary>Download through ComfyUI's /view endpoint: no shared storage needed.</summary>
public sealed class ApiVideoRetrieval : IVideoRetrieval
{
    public Task<byte[]> FetchAsync(IComfyUiClient client, OutputRef reference, CancellationToken ct = default) => client.ViewAsync(reference, ct);
}

/// <summary>Read the file straight from ComfyUI's output folder, mounted over NFS/SMB.</summary>
public sealed class ShareVideoRetrieval(string outputDirectory) : IVideoRetrieval
{
    private readonly string _root = Path.GetFullPath(outputDirectory);

    public async Task<byte[]> FetchAsync(IComfyUiClient client, OutputRef reference, CancellationToken ct = default)
    {
        var path = Path.GetFullPath(Path.Combine(_root, reference.Subfolder, reference.Filename));
        return path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? await File.ReadAllBytesAsync(path, ct)
            : throw new ComfyUiException("Output path escapes the shared output folder.");
    }
}

/// <summary>Loads the API-format workflow template from disk.</summary>
public sealed class FileWorkflowSource(string path) : IWorkflowSource
{
    public string Description => path;

    public bool Exists => File.Exists(path);

    public async Task<JsonObject> LoadAsync(CancellationToken ct = default) =>
        Exists
            ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject()
            : throw new MissingWorkflowException(
                $"No ComfyUI workflow at {path}. Export your Wan 2.2 I2V workflow with 'Save (API Format)' and use {{{{positive_prompt}}}}, " +
                "{{negative_prompt}}, {{input_image}}, {{seed}}, {{width}}, {{height}}, {{length}} and {{filename_prefix}} placeholders (see docs/local-generation.md).");
}
