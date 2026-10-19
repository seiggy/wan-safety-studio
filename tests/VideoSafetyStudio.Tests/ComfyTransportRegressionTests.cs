using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class ComfyTransportRegressionTests
{
    [Fact]
    public async Task Receive_failure_follows_accepted_prompt_history_without_resubmission()
    {
        await using var server = await ComfyServer.StartAsync(completed: true);
        using var http = new HttpClient();
        var client = server.Client(http);
        var accepted = new List<string>();
        var entry = await client.RunAsync(new JsonObject(), new StageTimer(new JsonObject()), id =>
        {
            accepted.Add(id);
            return Task.CompletedTask;
        }, () => { });
        Assert.True(entry["status"]!["completed"]!.GetValue<bool>());
        Assert.Equal(["accepted"], accepted);
        Assert.Equal(1, server.Submissions);
        Assert.True(server.HistoryReads > 0);
    }

    [Fact]
    public async Task History_fallback_preserves_prompt_caller_cancellation()
    {
        await using var server = await ComfyServer.StartAsync(completed: false);
        using var http = new HttpClient();
        using var caller = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = server.Client(http).RunAsync(new JsonObject(), new StageTimer(new JsonObject()), _ => Task.CompletedTask, () => { }, caller.Token);
        await server.HistoryStarted.Task.WaitAsync(watchdog.Token);
        var elapsed = Stopwatch.StartNew();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(watchdog.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(1, server.Submissions);
    }

    [Fact]
    public async Task Websocket_and_history_share_one_operation_deadline()
    {
        var time = new ReliabilityTimeProvider();
        await using var server = await ComfyServer.StartAsync(completed: false);
        server.BeforeReset = () => time.Advance(TimeSpan.FromSeconds(4));
        server.OnHistory = () => time.Advance(TimeSpan.FromSeconds(1));
        using var http = new HttpClient();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ComfyUiException>(() => server.Client(http, time: time).RunAsync(
            new JsonObject(), new StageTimer(new JsonObject()), _ => Task.CompletedTask, () => { }).WaitAsync(watchdog.Token));
        Assert.Contains("Timed out", error.Message);
        Assert.True(server.HistoryReads > 0);
        Assert.Equal(1, server.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_callback_failure_or_cancellation_is_awaited_by_the_client(bool cancel)
    {
        await using var server = await ComfyServer.StartAsync(completed: true);
        using var http = new HttpClient();
        Exception error = cancel ? new OperationCanceledException("persistence cancelled") : new IOException("persistence failed");
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => server.Client(http).RunAsync(
            new JsonObject(), new StageTimer(new JsonObject()), async _ =>
            {
                await Task.Yield();
                throw error;
            }, () => { }));
        Assert.Same(error, actual);
        Assert.Equal(1, server.Submissions);
        Assert.Equal(0, server.HistoryReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backend_acceptance_persistence_failure_or_cancellation_is_observed(bool cancel)
    {
        Exception error = cancel ? new OperationCanceledException("store cancelled") : new IOException("store offline");
        var store = new CommitStore { BeforeWrite = (_, _) => Task.FromException(error) };
        var events = new FakeEvents();
        var records = new JobRecords(store, events, NullLogger<JobRecords>.Instance);
        var backend = new ComfyUiBackend(store, new AcceptanceClient(), new ApiVideoRetrieval(), new FileWorkflowSource(TestPaths.Workflows),
            new FakeTelemetry(), events, records, Options.Create(new GenerationOptions()), TimeProvider.System);
        var job = GenerationCommitRegressionTests.Job("acceptance", JobStatus.Running);
        var item = new WorkItem(job, "p", new SceneImage("a.png", AzureMlSamples.Png), "", 5, 768, 768, WanModes.Get(null));
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => backend.GenerateAsync(item, 0, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.Equal("accepted", job.PromptId);
        Assert.NotNull(job.Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Share_retrieval_handles_trailing_separator_and_rejects_absolute_and_parent_escapes(bool trailing)
    {
        var folder = Path.Combine(TestPaths.RepoRoot, ".artifacts", "generation-share", Guid.NewGuid().ToString("N"));
        var root = Path.Combine(folder, "output");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "video.mp4"), [1, 2]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "outside.mp4"), [3]);
            var retrieval = new ShareVideoRetrieval(root + (trailing ? Path.DirectorySeparatorChar : ""));
            var client = new AcceptanceClient();
            Assert.Equal(new byte[] { 1, 2 }, await retrieval.FetchAsync(client, new OutputRef("video.mp4")));
            await Assert.ThrowsAsync<ComfyUiException>(() => retrieval.FetchAsync(client, new OutputRef("outside.mp4", "..")));
            await Assert.ThrowsAsync<ComfyUiException>(() => retrieval.FetchAsync(client, new OutputRef(Path.Combine(folder, "outside.mp4"))));
            await Assert.ThrowsAsync<ComfyUiException>(() => retrieval.FetchAsync(client, new OutputRef("outside.mp4", folder)));
            if (!OperatingSystem.IsWindows())
            {
                var sibling = Path.Combine(folder, "OUTPUT");
                Directory.CreateDirectory(sibling);
                await File.WriteAllBytesAsync(Path.Combine(sibling, "sibling.mp4"), [3]);
                await Assert.ThrowsAsync<ComfyUiException>(() => retrieval.FetchAsync(client, new OutputRef("sibling.mp4", Path.Combine("..", "OUTPUT"))));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class AcceptanceClient : IComfyUiClient
    {
        public string BaseUrl => "http://unused";
        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> UploadImageAsync(string name, byte[] data, CancellationToken ct = default) => Task.FromResult(name);
        public async Task<JsonNode> RunAsync(JsonObject workflow, StageTimer timer, Func<string, Task> onQueued, Action onEvent, CancellationToken ct = default)
        {
            await onQueued("accepted");
            throw new InvalidOperationException("Persistence failures must propagate before following the prompt.");
        }
        public Task<JsonNode> WaitAsync(string promptId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<byte[]> ViewAsync(OutputRef reference, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ComfyServer(WebApplication app) : IAsyncDisposable
    {
        public int Submissions;
        public int HistoryReads;
        public TaskCompletionSource HistoryStarted { get; } = GenerationCommitRegressionTests.NewSignal();
        public Action? BeforeReset { get; set; }
        public Action? OnHistory { get; set; }

        public ComfyUiClient Client(HttpClient http, double timeout = 5, TimeProvider? time = null) => new(http,
            Options.Create(new ComfyUiOptions { Url = app.Urls.Single(), PollSeconds = 0.01, TimeoutSeconds = timeout }), time ?? TimeProvider.System);

        public static async Task<ComfyServer> StartAsync(bool completed)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
            var app = builder.Build();
            var server = new ComfyServer(app);
            var accepted = GenerationCommitRegressionTests.NewSignal();
            app.UseWebSockets();
            app.Map("/ws", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await accepted.Task.WaitAsync(context.RequestAborted);
                server.BeforeReset?.Invoke();
                socket.Abort();
                context.Abort();
            });
            app.MapPost("/prompt", () =>
            {
                Interlocked.Increment(ref server.Submissions);
                accepted.TrySetResult();
                return Results.Json(new { prompt_id = "accepted" });
            });
            app.MapGet("/history/{id}", () =>
            {
                Interlocked.Increment(ref server.HistoryReads);
                server.HistoryStarted.TrySetResult();
                server.OnHistory?.Invoke();
                return Results.Text(completed ? """{"accepted":{"status":{"completed":true},"outputs":{"video":{}}}}""" : "{}", "application/json");
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(shutdown.Token);
            await app.DisposeAsync();
        }
    }
}
