using Microsoft.AspNetCore.Hosting;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

/// <summary>An in-process stand-in for the ComfyUI HTTP + websocket API.</summary>
public sealed class FakeComfyUi : IAsyncDisposable
{
    private static readonly (string Type, string Data)[] Events =
    [
        ("execution_start", "{}"), ("execution_cached", """{"nodes": ["38"]}"""), ("executing", """{"node": "37"}"""),
        ("executing", """{"node": "57"}"""), ("progress", """{"node": "57", "value": 5, "max": 10}"""), ("executing", """{"node": "58"}"""),
        ("executing", """{"node": "8"}"""), ("executing", """{"node": "47"}"""), ("execution_success", "{}"),
    ];

    private readonly WebApplication _app;
    private WebSocket? _socket;

    public string Url { get; private set; } = "";
    public JsonObject? LastPrompt { get; private set; }
    public string? UploadedName { get; private set; }
    public byte[] Video { get; set; } = Encoding.UTF8.GetBytes("MP4DATA");

    private FakeComfyUi()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/system_stats", () => Results.Json(new { }));
        _app.MapPost("/upload/image", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            UploadedName = form.Files["image"]!.FileName;
            return Results.Json(new { name = UploadedName, subfolder = "", type = "input" });
        });
        _app.Map("/ws", async (HttpContext context) =>
        {
            _socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[256];
            while (_socket.State == WebSocketState.Open && (await _socket.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close)
            {
            }
        });
        _app.MapPost("/prompt", async (HttpRequest request) =>
        {
            LastPrompt = (await JsonNode.ParseAsync(request.Body))!["prompt"]!.AsObject();
            if (_socket is { State: WebSocketState.Open })
            {
                foreach (var (type, data) in Events)
                {
                    var payload = JsonNode.Parse(data)!.AsObject();
                    payload["prompt_id"] = "p1";
                    var message = new JsonObject { ["type"] = type, ["data"] = payload }.ToJsonString();
                    await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }

            return Results.Json(new { prompt_id = "p1" });
        });
        _app.MapGet("/history/{id}", (string id) => Results.Json(new Dictionary<string, object>
        {
            [id] = new
            {
                status = new { status_str = "success", completed = true },
                outputs = new Dictionary<string, object> { ["47"] = new { images = new[] { new { filename = "out.mp4", subfolder = "wan", type = "output" } } } },
            },
        }));
        _app.MapGet("/view", () => Results.Bytes(Video));
    }

    public static async Task<FakeComfyUi> StartAsync()
    {
        var fake = new FakeComfyUi();
        await fake._app.StartAsync();
        fake.Url = fake._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public sealed class GenerationFlowTests : IAsyncLifetime
{
    private readonly TempFolder _temp = new();
    private FakeComfyUi _comfy = null!;

    public async Task InitializeAsync() => _comfy = await FakeComfyUi.StartAsync();

    public async Task DisposeAsync()
    {
        await _comfy.DisposeAsync();
        _temp.Dispose();
    }

    private (GenerationService Service, FakeEvents Events, LocalFolderObjectStore Store) Create(Application.Abstractions.IVideoRetrieval? retrieval = null, bool workflowExists = true)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_temp.Path, "store"));
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Workflows, "wan22_i2v.api.json")))!.AsObject();
        var client = new ComfyUiClient(new HttpClient(), Options.Create(new ComfyUiOptions { Url = _comfy.Url, PollSeconds = 0.01 }), TimeProvider.System);
        var events = new FakeEvents();
        var service = new GenerationService(store, client, retrieval ?? new ApiVideoRetrieval(), new FakeWorkflow(template, workflowExists), new FakeTelemetry(), events,
            Options.Create(new GenerationOptions()), TimeProvider.System, NullLogger<GenerationService>.Instance);
        return (service, events, store);
    }

    private static SubmitRequest Request(int takes = 1) =>
        new(["leaking valve"], "", "1:1", 5, takes, [new SceneImage("img.png", [1, 2, 3])]);

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

    [Fact]
    public async Task Api_retrieval_stores_the_video_with_stage_timings_and_pushes_progress()
    {
        var (service, events, store) = Create();
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);

        var job = service.FindJob(batch.Jobs[0].Name)!;
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("leaking valve", _comfy.LastPrompt!["6"]!["inputs"]!["text"]!.GetValue<string>());
        Assert.Equal(81, _comfy.LastPrompt["50"]!["inputs"]!["length"]!.GetValue<int>()); // 5 s at 16 fps, 4n+1
        Assert.StartsWith(job.Name, _comfy.UploadedName);

        var item = (await service.GetGalleryAsync()).Items.Single();
        Assert.Equal("success", item.Timing!.Outcome);
        Assert.Contains("sample_high_noise", item.Timing.Stages.Keys);
        Assert.Contains("vae_decode", item.Timing.Stages.Keys);
        Assert.Equal(["comfyui", "retrieve", "store_video", "upload_image"], item.Timing.Phases!.Keys.Order().ToArray());
        Assert.Equal(81, item.Timing.Params!.Frames);
        Assert.Equal("MP4DATA", Encoding.UTF8.GetString((await store.ReadAsync(item.VideoUrl["/api/media/".Length..]))!));
        lock (events.Items)
        {
            Assert.Contains(events.Items, e => e.Type == "job" && e.Json.Contains("\"stage\":\"sample_high_noise\""));
        }
    }

    [Fact]
    public async Task Share_retrieval_reads_the_mounted_output_and_blocks_escapes()
    {
        var shared = Path.Combine(_temp.Path, "comfy-out");
        Directory.CreateDirectory(Path.Combine(shared, "wan"));
        await File.WriteAllTextAsync(Path.Combine(shared, "wan", "out.mp4"), "SHARED");
        var (service, _, store) = Create(new ShareVideoRetrieval(shared));
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);

        Assert.Equal(JobStatus.Completed, service.FindJob(batch.Jobs[0].Name)!.Status);
        Assert.Equal("SHARED", Encoding.UTF8.GetString((await store.ReadAsync($"video-library/{batch.Jobs[0].Name}/video.mp4"))!));
        await Assert.ThrowsAsync<VideoSafetyStudio.Application.Abstractions.ComfyUiException>(() =>
            new ShareVideoRetrieval(shared).FetchAsync(null!, new("../../x.mp4")));
    }

    [Fact]
    public async Task Missing_workflow_is_a_clear_error()
    {
        var (service, _, _) = Create(workflowExists: false);
        await Assert.ThrowsAsync<VideoSafetyStudio.Application.Abstractions.MissingWorkflowException>(() => service.SubmitAsync(Request()));
    }

    [Fact]
    public async Task Restart_resumes_accepted_jobs_and_fails_the_rest()
    {
        var (service, _, store) = Create();
        var context = new JobContext("p", 8, 8, 5, 1);
        var running = new GenerationJob { Name = "local-a", DisplayName = "a", Status = JobStatus.Running, PromptId = "p1", Context = context };
        var queued = new GenerationJob { Name = "local-b", DisplayName = "b", Status = JobStatus.Queued };
        foreach (var job in new[] { running, queued })
        {
            await store.WriteAsync($"video-library-jobs/{job.Name}.json", VideoSafetyStudio.Application.Json.ToBytes(job));
        }

        await service.RecoverAsync();
        for (var i = 0; i < 200 && service.FindJob("local-a")!.Status != JobStatus.Completed; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(JobStatus.Completed, service.FindJob("local-a")!.Status);
        Assert.Equal(JobStatus.Failed, service.FindJob("local-b")!.Status);
        Assert.True(await store.ExistsAsync("video-library/local-a/video.mp4"));
    }

    [Theory]
    [InlineData(0, 5, 1, "1:1")]
    [InlineData(1, 4, 1, "1:1")]
    [InlineData(1, 5, 2, "1:1")]
    [InlineData(1, 5, 1, "2:1")]
    public async Task Submit_validates_inputs(int scenes, double seconds, int takes, string aspect)
    {
        var (service, _, _) = Create();
        var prompts = Enumerable.Repeat("p", scenes).ToList();
        await Assert.ThrowsAsync<VideoSafetyStudio.Domain.ValidationException>(() =>
            service.SubmitAsync(new SubmitRequest(prompts, "", aspect, seconds, takes, [new SceneImage("a.png", [1])])));
    }
}
