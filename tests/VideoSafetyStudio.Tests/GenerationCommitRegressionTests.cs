using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Infrastructure.ComfyUi;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class GenerationCommitRegressionTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Nonfinite_duration_is_rejected_before_admission(double duration)
    {
        var backend = new CommitBackend();
        var store = new CommitStore();
        var service = Create(store, backend);
        await Assert.ThrowsAsync<ValidationException>(() => service.SubmitAsync(Request() with { DurationSeconds = duration }));
        Assert.Equal(0, backend.Admissions);
        Assert.Empty(store.Data);
        Assert.Empty(service.OpenJobs());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task Short_image_list_reuses_scene_one_before_any_work_is_committed(double duration)
    {
        var service = Create(new CommitStore(), new CommitBackend());
        var request = Request() with { Prompts = ["one", "two"], DurationSeconds = duration };
        var batch = await service.SubmitAsync(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var queue = service.DequeueAllAsync(timeout.Token).GetAsyncEnumerator();
        foreach (var job in batch.Jobs)
        {
            Assert.True(await queue.MoveNextAsync());
            Assert.Same(request.Images[0], queue.Current.Image);
            Assert.Same(job, queue.Current.Job);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cancellation_during_committed_persistence_does_not_abandon_any_scene(int cancelAt)
    {
        using var caller = new CancellationTokenSource();
        var store = new CommitStore();
        var writes = 0;
        store.BeforeWrite = (_, ct) =>
        {
            if (Interlocked.Increment(ref writes) == cancelAt)
            {
                caller.Cancel();
            }
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var service = Create(store, new CommitBackend());
        var batch = await service.SubmitAsync(Request() with { Prompts = ["one", "two"], Images = [Request().Images[0], null] }, caller.Token);
        Assert.Same(batch, service.GetBatch(batch.Id));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var queue = service.DequeueAllAsync(timeout.Token).GetAsyncEnumerator();
        foreach (var job in batch.Jobs)
        {
            Assert.True(await queue.MoveNextAsync());
            Assert.Same(job, queue.Current.Job);
            Assert.True(store.Data.ContainsKey($"{JobRecords.Prefix}{job.Name}.json"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_timeout_becomes_a_durably_saved_terminal_failure(bool resume)
    {
        var store = new CommitStore();
        var backend = new CommitBackend { Run = _ => throw new TaskCanceledException("transport timeout") };
        var service = Create(store, backend);
        var batch = await service.SubmitAsync(Request());
        var job = batch.Jobs[0];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var queue = service.DequeueAllAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await queue.MoveNextAsync());
        if (resume)
        {
            job.RemoteId = job.Name;
            await new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance).SaveAsync(job);
            job = Assert.Single(await service.RecoverAsync(timeout.Token));
            await service.ResumeAsync(job, timeout.Token);
        }
        else
        {
            await service.ProcessAsync(queue.Current, timeout.Token);
        }
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(JobStatus.Failed, Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}{job.Name}.json"])!.Status);
    }

    [Fact]
    public async Task Gallery_skips_damaged_missing_and_null_documents_but_preserves_valid_items()
    {
        var store = new CommitStore();
        var good = new GalleryMeta("good", "good", 8, 8, 5, "p", 1, "video.mp4", 1, null);
        store.Data["video-library/a/meta.json"] = Json.ToBytes(good);
        store.Data["video-library/b/meta.json"] = "{"u8.ToArray();
        store.Data["video-library/c/meta.json"] = "null"u8.ToArray();
        store.Data["video-library/d/meta.json"] = "{}"u8.ToArray();
        store.ExtraListed = ["video-library/missing/meta.json"];
        var response = await Create(store, new CommitBackend()).GetGalleryAsync();
        Assert.Equal("good", Assert.Single(response.Items).JobName);
    }

    [Fact]
    public async Task Collection_loading_does_not_hide_storage_failures_or_cancellation()
    {
        var store = new CommitStore();
        var service = Create(store, new CommitBackend());
        store.ListError = new IOException("store offline");
        await Assert.ThrowsAsync<IOException>(() => service.GetGalleryAsync());
        await Assert.ThrowsAsync<IOException>(() => service.RecoverAsync());
        store.ListError = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetGalleryAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecoverAsync(cancelled.Token));
    }

    [Fact]
    public async Task Recovery_visits_both_valid_jobs_around_a_malformed_document()
    {
        var store = new CommitStore();
        var first = Job("first", JobStatus.Running);
        var last = Job("last", JobStatus.Running);
        first.RemoteId = "remote-first";
        last.RemoteId = "remote-last";
        store.Data[$"{JobRecords.Prefix}a.json"] = Json.ToBytes(first);
        store.Data[$"{JobRecords.Prefix}b.json"] = "{"u8.ToArray();
        store.Data[$"{JobRecords.Prefix}c.json"] = Json.ToBytes(last);
        store.Data[$"{JobRecords.Prefix}d.json"] = """{"name":null,"display_name":"bad"}"""u8.ToArray();
        var service = Create(store, new CommitBackend());
        var recovered = await service.RecoverAsync();
        Assert.Equal(["first", "last"], recovered.Select(j => j.Name));
    }

    [Fact]
    public async Task Three_writers_remain_serialized_after_terminal_gate_cleanup()
    {
        var store = new CommitStore();
        var enteredFirst = NewSignal();
        var enteredSecond = NewSignal();
        var enteredThird = NewSignal();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();
        var calls = 0;
        var active = 0;
        var maxActive = 0;
        store.BeforeWrite = async (_, ct) =>
        {
            var concurrent = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, concurrent);
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    enteredFirst.SetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                    break;
                case 2:
                    enteredSecond.SetResult();
                    await releaseSecond.Task.WaitAsync(ct);
                    break;
                case 3:
                    enteredThird.SetResult();
                    break;
            }
            Interlocked.Decrement(ref active);
        };
        var records = new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = records.SaveAsync(Job("same", JobStatus.Completed), timeout.Token);
        await enteredFirst.Task.WaitAsync(timeout.Token);
        var second = records.SaveAsync(Job("same", JobStatus.Running), timeout.Token);
        releaseFirst.SetResult();
        await first;
        await enteredSecond.Task.WaitAsync(timeout.Token);
        var third = records.SaveAsync(Job("same", JobStatus.Completed), timeout.Token);
        try
        {
            Assert.NotSame(enteredThird.Task, await Task.WhenAny(enteredThird.Task, Task.Delay(100, timeout.Token)));
        }
        finally
        {
            releaseSecond.TrySetResult();
            await Task.WhenAll(first, second, third);
        }
        Assert.Equal(1, maxActive);
        Assert.Equal(JobStatus.Completed, Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}same.json"])!.Status);
    }

    internal static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static GenerationJob Job(string name, string status) => new() { Name = name, DisplayName = name, Status = status };

    internal static SubmitRequest Request() => new(["one"], "", "1:1", 5, 1, [new SceneImage("a.png", AzureMlSamples.Png)]);

    internal static GenerationService Create(CommitStore store, IGenerationBackend backend) =>
        new(store, backend, new FileWorkflowSource(TestPaths.Workflows), new JobRecords(store, new FakeEvents(), NullLogger<JobRecords>.Instance),
            Options.Create(new GenerationOptions()), TimeProvider.System, NullLogger<GenerationService>.Instance);
}

internal sealed class CommitStore : IObjectStore
{
    public ConcurrentDictionary<string, byte[]> Data { get; } = new();
    public Func<string, CancellationToken, Task>? BeforeWrite { get; set; }
    public Exception? ListError { get; set; }
    public string[] ExtraListed { get; set; } = [];
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Data.ContainsKey(key));
    public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Data.TryGetValue(key, out var data) ? data : null);
    }
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
    public async Task WriteAsync(string key, ReadOnlyMemory<byte> data, bool overwrite = true, CancellationToken ct = default)
    {
        if (BeforeWrite is { } before) await before(key, ct);
        ct.ThrowIfCancellationRequested();
        Data[key] = data.ToArray();
    }
    public Task UploadAsync(string key, string path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DownloadAsync(string key, string path, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ListError is { } error) throw error;
        return Task.FromResult<IReadOnlyList<string>>([.. Data.Keys.Concat(ExtraListed).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order()]);
    }
}

internal sealed class CommitBackend : IGenerationBackend
{
    public string Provider => "test";
    public int MaxConcurrency => 2;
    public int Admissions { get; private set; }
    public Func<CancellationToken, Task> Run { get; set; } = _ => Task.CompletedTask;
    public string NewJobName() => "job-" + Guid.NewGuid().ToString("N");
    public void EnsureCanSubmit(SubmitRequest request) => Admissions++;
    public bool ModeAvailable(WanMode mode) => true;
    public Task<object> StatusAsync(string storage, CancellationToken ct) => Task.FromResult<object>(new { });
    public Task<IReadOnlyList<object>> OpenJobsAsync(IReadOnlyList<GenerationJob> local, CancellationToken ct) => Task.FromResult<IReadOnlyList<object>>([.. local]);
    public Task GenerateAsync(WorkItem item, double started, CancellationToken ct) => Run(ct);
    public bool CanResume(GenerationJob job) => job.RemoteId is not null;
    public Task ResumeAsync(GenerationJob job, CancellationToken ct) => Run(ct);
}
