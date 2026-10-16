using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class AzureMlHandoffRegressionTests
{
    [Fact]
    public async Task Shutdown_inside_create_durably_hands_off_once_then_restart_resumes_the_same_job()
    {
        using var shutdown = new CancellationTokenSource();
        var store = new CommitStore();
        var client = new HandoffClient { AfterCreate = shutdown.Cancel };
        var (service, _) = Build(store, client);
        var batch = await service.SubmitAsync(GenerationCommitRegressionTests.Request());
        var job = batch.Jobs[0];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessOne(service, shutdown.Token));
        var durable = Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}{job.Name}.json"])!;
        Assert.Equal(job.Name, durable.RemoteId);
        Assert.NotNull(durable.Context);
        Assert.Equal(JobStatus.Running, durable.Status);
        Assert.Equal(1, client.Creates);
        Assert.Equal(0, client.Cancels);
        Assert.True(client.VerificationToken.CanBeCanceled);
        Assert.False(client.VerificationToken.IsCancellationRequested);

        store.Data[$"{GenerationService.Library}{job.Name}/video.mp4"] = [1];
        client.Read = (_, _) => Task.FromResult<JsonNode>(new JsonObject { ["properties"] = new JsonObject { ["status"] = "Completed" } });
        var (restarted, _) = Build(store, client);
        var recovered = Assert.Single(await restarted.RecoverAsync());
        await restarted.ResumeAsync(recovered, CancellationToken.None);
        Assert.Equal(job.Name, recovered.RemoteId);
        Assert.Equal(JobStatus.Completed, recovered.Status);
        Assert.Equal(1, client.Creates);
        Assert.Equal(0, client.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Polling_shutdown_after_durable_handoff_preserves_remote_identity_without_cancel(bool concurrentTransportFailure)
    {
        using var shutdown = new CancellationTokenSource();
        var store = new CommitStore();
        var client = new HandoffClient();
        client.Read = (call, ct) =>
        {
            if (call == 1) return Task.FromResult(client.Stored!);
            shutdown.Cancel();
            if (concurrentTransportFailure) throw new HttpRequestException("transport failed as the host stopped");
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };
        var (service, _) = Build(store, client);
        var batch = await service.SubmitAsync(GenerationCommitRegressionTests.Request());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessOne(service, shutdown.Token));
        var durable = Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}{batch.Jobs[0].Name}.json"])!;
        Assert.Equal(batch.Jobs[0].Name, durable.RemoteId);
        Assert.NotNull(durable.Context);
        Assert.Equal(0, client.Cancels);
        Assert.Equal(1, client.Creates);
        Assert.Single(await Build(store, client).Service.RecoverAsync());
    }

    [Fact]
    public async Task Failed_verification_uses_bounded_cancel_and_keeps_original_diagnostic()
    {
        var original = new InvalidOperationException("verification diagnostic");
        var client = new HandoffClient { Read = (_, _) => throw original, CancelError = new IOException("cancel failed") };
        var store = new CommitStore();
        var (_, backend) = Build(store, client);
        var item = Item();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.GenerateAsync(item, 0, CancellationToken.None));
        Assert.Same(original, actual);
        Assert.Equal(1, client.Cancels);
        Assert.True(client.CancelToken.CanBeCanceled);
        Assert.Equal(item.Job.Name, Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}{item.Job.Name}.json"])!.RemoteId);
    }

    [Fact]
    public async Task Acceptance_storage_failure_is_reported_as_recovery_critical_and_not_resubmitted()
    {
        var original = new IOException("persistent store offline");
        var store = new CommitStore { BeforeWrite = (key, _) => key.StartsWith(JobRecords.Prefix, StringComparison.Ordinal) ? throw original : Task.CompletedTask };
        var client = new HandoffClient();
        var (_, backend) = Build(store, client);
        var item = Item();
        var error = await Assert.ThrowsAsync<AzureMlHandoffException>(() => backend.GenerateAsync(item, 0, CancellationToken.None));
        Assert.Same(original, error.InnerException);
        Assert.Contains("recovery information", error.Message);
        Assert.Equal(item.Job.Name, item.Job.RemoteId);
        Assert.Equal(1, client.Creates);
        Assert.Equal(1, client.Cancels);
        Assert.True(client.CancelToken.CanBeCanceled);
    }

    [Fact]
    public async Task Verification_and_best_effort_cancellation_have_independent_bounded_budgets()
    {
        var time = new ReliabilityTimeProvider();
        var verifying = GenerationCommitRegressionTests.NewSignal();
        var cancelling = GenerationCommitRegressionTests.NewSignal();
        var client = new HandoffClient
        {
            Read = async (_, ct) =>
            {
                verifying.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            Cancel = async ct =>
            {
                cancelling.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        var (_, backend) = Build(new CommitStore(), client, time);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var generation = backend.GenerateAsync(Item(), 0, CancellationToken.None);
        await verifying.Task.WaitAsync(watchdog.Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await cancelling.Task.WaitAsync(watchdog.Token);
        Assert.False(generation.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation.WaitAsync(watchdog.Token));
        Assert.Equal(client.VerificationToken, error.CancellationToken);
        Assert.True(client.CancelToken.IsCancellationRequested);
        Assert.Equal(1, client.Creates);
        Assert.Equal(1, client.Cancels);
    }

    [Fact]
    public async Task Acceptance_persistence_has_a_bounded_budget_independent_of_shutdown()
    {
        var time = new ReliabilityTimeProvider();
        using var shutdown = new CancellationTokenSource();
        var persisting = GenerationCommitRegressionTests.NewSignal();
        CancellationToken persistenceToken = default;
        var store = new CommitStore
        {
            BeforeWrite = async (key, ct) =>
            {
                if (!key.StartsWith(JobRecords.Prefix, StringComparison.Ordinal)) return;
                persistenceToken = ct;
                persisting.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        var client = new HandoffClient { AfterCreate = shutdown.Cancel };
        var (_, backend) = Build(store, client, time);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var generation = backend.GenerateAsync(Item(), 0, shutdown.Token);
        await persisting.Task.WaitAsync(watchdog.Token);
        Assert.True(shutdown.IsCancellationRequested);
        Assert.True(persistenceToken.CanBeCanceled);
        Assert.False(persistenceToken.IsCancellationRequested);
        Assert.False(generation.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<AzureMlHandoffException>(() => generation.WaitAsync(watchdog.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.True(persistenceToken.IsCancellationRequested);
        Assert.Equal(1, client.Creates);
        Assert.Equal(1, client.Cancels);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"properties":{"status":null}}""")]
    [InlineData("""{"properties":{"status":7}}""")]
    [InlineData("[]")]
    [InlineData("transport-timeout")]
    public async Task Malformed_successes_and_transport_timeouts_are_bounded_then_cancelled_and_saved_failed(string malformed)
    {
        JsonNode? accepted = null;
        var creates = 0;
        var gets = 0;
        var cancels = 0;
        using var http = new HttpClient(new ArmHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                creates++;
                accepted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                accepted["name"] = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.Split('/').Last());
                accepted["properties"]!["status"] = "NotStarted";
                return Response(accepted.ToJsonString());
            }
            if (request.Method == HttpMethod.Post)
            {
                cancels++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            gets++;
            if (gets == 1) return Response(accepted!.ToJsonString());
            if (malformed == "transport-timeout") throw new TaskCanceledException("HTTP timeout", null, CancellationToken.None);
            return Response(malformed);
        }));
        var client = new ArmAzureMlClient(http, new ArmCredential(), AzureMlSamples.Foundation());
        var store = new CommitStore();
        var (service, _) = Build(store, client);
        var job = Assert.Single((await service.SubmitAsync(GenerationCommitRegressionTests.Request())).Jobs);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ProcessOne(service, watchdog.Token);
        Assert.Equal(1, creates);
        Assert.Equal(21, gets);
        Assert.Equal(1, cancels);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobStatus.Failed, Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}{job.Name}.json"])!.Status);
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static WorkItem Item() => new(GenerationCommitRegressionTests.Job("wan-abc123abc123", JobStatus.Running), "one",
        new SceneImage("a.png", AzureMlSamples.Png), "", 5, 768, 768, WanModes.Get(null));

    private static (GenerationService Service, AzureMlBackend Backend) Build(CommitStore store, IAzureMlClient client, TimeProvider? time = null)
    {
        var workflows = new FileWorkflowSource(TestPaths.Workflows);
        var options = Options.Create(new GenerationOptions());
        var records = new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance);
        var backend = new AzureMlBackend(store, client, AzureMlSamples.Foundation(), AzureMlSamples.Release(), AzureMlSamples.Gate(true, out _),
            workflows, new FakeTelemetry(), records, options, Options.Create(new AzureMlOptions { PollSeconds = 0.001 }), time ?? TimeProvider.System);
        return (new GenerationService(store, backend, workflows, records, options, TimeProvider.System, NullLogger<GenerationService>.Instance), backend);
    }

    private static async Task ProcessOne(GenerationService service, CancellationToken ct)
    {
        // Queue consumption must succeed even when the fake create later cancels the processing token.
        await using var queue = service.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await queue.MoveNextAsync());
        await service.ProcessAsync(queue.Current, ct);
    }

    private sealed class HandoffClient : IAzureMlClient
    {
        public int Creates { get; private set; }
        public int Cancels { get; private set; }
        public int Reads { get; private set; }
        public JsonNode? Stored { get; private set; }
        public Action? AfterCreate { get; init; }
        public Func<int, CancellationToken, Task<JsonNode>>? Read { get; set; }
        public Exception? CancelError { get; init; }
        public Func<CancellationToken, Task>? Cancel { get; init; }
        public CancellationToken VerificationToken { get; private set; }
        public CancellationToken CancelToken { get; private set; }

        public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct)
        {
            Creates++;
            Stored = spec.ToArmBody();
            Stored["name"] = spec.Name;
            Stored["properties"]!["status"] = "NotStarted";
            AfterCreate?.Invoke();
            return Task.FromResult(Stored);
        }
        public Task<JsonNode> GetJobAsync(string name, CancellationToken ct)
        {
            Reads++;
            if (Reads == 1) VerificationToken = ct;
            ct.ThrowIfCancellationRequested();
            return Read?.Invoke(Reads, ct) ?? Task.FromResult(Stored!);
        }
        public Task CancelJobAsync(string name, CancellationToken ct)
        {
            Cancels++;
            CancelToken = ct;
            return CancelError is { } error ? Task.FromException(error) : Cancel?.Invoke(ct) ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<JsonNode>>([]);
        public Task<string?> GetComputeStateAsync(CancellationToken ct) => Task.FromResult<string?>("Succeeded");
    }

    private sealed class ArmHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ArmCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
