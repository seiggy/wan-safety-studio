using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.AzureMl;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Infrastructure.Storage;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

/// <summary>Wraps the in-memory workspace so a test can make polling or creation fail.</summary>
internal sealed class FlakyAzureMl(FakeAzureMl inner) : IAzureMlClient
{
    public bool FailPolls { get; set; }
    public AzureMlException? CreateError { get; set; }

    public Task<JsonNode> CreateJobAsync(AzureMlJobSpec spec, CancellationToken ct) => CreateError is { } error ? throw error : inner.CreateJobAsync(spec, ct);

    public async Task<JsonNode> GetJobAsync(string name, CancellationToken ct)
    {
        var node = await inner.GetJobAsync(name, ct);
        return FailPolls && node["properties"]?["status"]?.GetValue<string>() != "NotStarted" ? throw new HttpRequestException("network down") : node;
    }

    public Task CancelJobAsync(string name, CancellationToken ct) => inner.CancelJobAsync(name, ct);

    public Task<IReadOnlyList<JsonNode>> ListJobsAsync(int max, CancellationToken ct) => inner.ListJobsAsync(max, ct);

    public Task<string?> GetComputeStateAsync(CancellationToken ct) => inner.GetComputeStateAsync(ct);
}

public sealed class GenerationHardeningTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly ReliabilityTimeProvider _time = new();

    public void Dispose() => _temp.Dispose();

    private (GenerationService Service, FakeAzureMl Aml, FlakyAzureMl Client) Create(GenerationOptions? generation = null)
    {
        var store = new LocalFolderObjectStore(Path.Combine(_temp.Path, "store"));
        var aml = new FakeAzureMl(store);
        var flaky = new FlakyAzureMl(aml);
        var records = new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance);
        var workflows = new FileWorkflowSource(TestPaths.Workflows);
        var options = Options.Create(generation ?? new GenerationOptions());
        var backend = new AzureMlBackend(store, flaky, AzureMlSamples.Foundation(), AzureMlSamples.Release(), AzureMlSamples.Gate(true, out _), workflows, new FakeTelemetry(), records,
            options, Options.Create(new AzureMlOptions { PollSeconds = 0.001 }), TimeProvider.System);
        return (new GenerationService(store, backend, workflows, records, options, _time, NullLogger<GenerationService>.Instance), aml, flaky);
    }

    private static SubmitRequest Request(int takes = 1, string negative = "blurry") =>
        new(["leaking valve"], negative, "1:1", 5, takes, [new SceneImage("a.png", AzureMlSamples.Png)]);

    private static async Task RunAllAsync(GenerationService service)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var queued = service.OpenJobs().Count;
        await using var reader = service.DequeueAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        for (var i = 0; i < queued; i++)
        {
            Assert.True(await reader.MoveNextAsync());
            await service.ProcessAsync(reader.Current, cts.Token);
        }
    }

    [Fact]
    public async Task Unfinished_jobs_are_capped_and_finished_ones_free_the_room()
    {
        var (service, _, _) = Create(new GenerationOptions { MaxOpenJobs = 3 });
        await service.SubmitAsync(Request(takes: 3));
        await Assert.ThrowsAsync<ConflictException>(() => service.SubmitAsync(Request()));
        await RunAllAsync(service);
        await service.SubmitAsync(Request());
        Assert.Single(service.OpenJobs());
    }

    [Fact]
    public async Task A_negative_prompt_over_4000_characters_is_rejected()
    {
        var (service, aml, _) = Create();
        await Assert.ThrowsAsync<ValidationException>(() => service.SubmitAsync(Request(negative: new string('x', 4001))));
        await service.SubmitAsync(Request(negative: new string('x', 4000)));
        Assert.Empty(aml.Created);
    }

    [Fact]
    public async Task Giving_up_on_polling_cancels_the_remote_job()
    {
        var (service, aml, client) = Create();
        client.FailPolls = true;
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        var job = service.FindJob(batch.Jobs[0].Name)!;
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal([job.Name], aml.Cancelled);
        Assert.DoesNotContain("network down", job.Error);
    }

    [Fact]
    public async Task Provider_detail_stays_out_of_the_job_error()
    {
        var (service, _, client) = Create();
        client.CreateError = new AzureMlException("Azure ML PUT failed (400)", HttpStatusCode.BadRequest, "/subscriptions/0000/resourceGroups/rg-secret quota exceeded");
        var batch = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        var error = service.FindJob(batch.Jobs[0].Name)!.Error!;
        Assert.Equal("Azure ML PUT failed (400)", error);
        Assert.DoesNotContain("rg-secret", error);
    }

    [Fact]
    public async Task Finished_jobs_past_the_retention_leave_memory()
    {
        var (service, _, _) = Create(new GenerationOptions { JobRetentionHours = 1 });
        var first = await service.SubmitAsync(Request());
        await RunAllAsync(service);
        _time.Advance(TimeSpan.FromHours(1));
        await service.SubmitAsync(Request());
        Assert.NotNull(service.FindJob(first.Jobs[0].Name));
        Assert.Equal(first.Id, service.GetBatch(first.Id).Id);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await service.SubmitAsync(Request());
        Assert.Null(service.FindJob(first.Jobs[0].Name));
        Assert.Throws<NotFoundException>(() => service.GetBatch(first.Id));
    }
}
