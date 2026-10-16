using Microsoft.Extensions.Logging.Abstractions;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Generation;
using VideoSafetyStudio.Domain.Generation;
using VideoSafetyStudio.Server.Workers;

namespace VideoSafetyStudio.Tests;

public sealed class GenerationLifetimeRegressionTests
{
    [Fact]
    public async Task Worker_drains_recovered_and_new_work_together_before_successful_shutdown()
    {
        var store = new CommitStore();
        var started = new[] { GenerationCommitRegressionTests.NewSignal(), GenerationCommitRegressionTests.NewSignal() };
        var cleaning = new[] { GenerationCommitRegressionTests.NewSignal(), GenerationCommitRegressionTests.NewSignal() };
        var release = new[] { GenerationCommitRegressionTests.NewSignal(), GenerationCommitRegressionTests.NewSignal() };
        var calls = 0;
        var completed = 0;
        var backend = new CommitBackend
        {
            Run = async ct =>
            {
                var index = Interlocked.Increment(ref calls) - 1;
                started[index].SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    cleaning[index].SetResult();
                    await release[index].Task;
                    Interlocked.Increment(ref completed);
                }
            },
        };
        var job = GenerationCommitRegressionTests.Job("recovered", JobStatus.Running);
        job.RemoteId = "remote";
        store.Data[$"{JobRecords.Prefix}{job.Name}.json"] = Json.ToBytes(job);
        var service = GenerationCommitRegressionTests.Create(store, backend);
        using var worker = new GenerationWorker(service, NullLogger<GenerationWorker>.Instance);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(watchdog.Token);
        await started[0].Task.WaitAsync(watchdog.Token);
        await service.SubmitAsync(GenerationCommitRegressionTests.Request());
        await started[1].Task.WaitAsync(watchdog.Token);
        var stop = worker.StopAsync(watchdog.Token);
        await Task.WhenAll(cleaning.Select(signal => signal.Task)).WaitAsync(watchdog.Token);
        try
        {
            release[1].SetResult();
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            foreach (var signal in release) signal.TrySetResult();
            await stop.WaitAsync(watchdog.Token);
        }
        Assert.Equal(2, completed);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Expired_host_budget_does_not_report_unfinished_cleanup_as_a_successful_drain()
    {
        var cleaning = GenerationCommitRegressionTests.NewSignal();
        var release = GenerationCommitRegressionTests.NewSignal();
        var started = GenerationCommitRegressionTests.NewSignal();
        var backend = new CommitBackend
        {
            Run = async ct =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    cleaning.SetResult();
                    await release.Task;
                }
            },
        };
        var store = new CommitStore();
        var job = GenerationCommitRegressionTests.Job("recovered", JobStatus.Running);
        job.RemoteId = "remote";
        store.Data[$"{JobRecords.Prefix}{job.Name}.json"] = Json.ToBytes(job);
        var service = GenerationCommitRegressionTests.Create(store, backend);
        using var worker = new GenerationWorker(service, NullLogger<GenerationWorker>.Instance);
        using var budget = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(watchdog.Token);
        await started.Task.WaitAsync(watchdog.Token);
        var stop = worker.StopAsync(budget.Token);
        await cleaning.Task.WaitAsync(watchdog.Token);
        try
        {
            budget.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(watchdog.Token));
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await worker.ExecuteTask!.WaitAsync(watchdog.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_shutdown_owns_and_drains_normal_and_recovered_cleanup(bool recovered)
    {
        var store = new CommitStore();
        var started = GenerationCommitRegressionTests.NewSignal();
        var cleaning = GenerationCommitRegressionTests.NewSignal();
        var release = GenerationCommitRegressionTests.NewSignal();
        var finished = false;
        var backend = new CommitBackend
        {
            Run = async ct =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    cleaning.SetResult();
                    await release.Task;
                    finished = true;
                }
            },
        };
        var service = GenerationCommitRegressionTests.Create(store, backend);
        if (recovered)
        {
            var job = GenerationCommitRegressionTests.Job("recovered", JobStatus.Running);
            job.RemoteId = "accepted-remote";
            store.Data[$"{JobRecords.Prefix}{job.Name}.json"] = Json.ToBytes(job);
        }
        else
        {
            await service.SubmitAsync(GenerationCommitRegressionTests.Request());
        }
        using var worker = new GenerationWorker(service, NullLogger<GenerationWorker>.Instance);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(budget.Token);
        await started.Task.WaitAsync(budget.Token);
        var stop = worker.StopAsync(budget.Token);
        await cleaning.Task.WaitAsync(budget.Token);
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.False(finished);
        }
        finally
        {
            release.TrySetResult();
            await stop.WaitAsync(budget.Token);
        }
        Assert.True(finished);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        if (recovered)
        {
            var durable = Json.From<GenerationJob>(store.Data[$"{JobRecords.Prefix}recovered.json"])!;
            Assert.Equal("accepted-remote", durable.RemoteId);
            Assert.Equal(JobStatus.Running, durable.Status);
        }
    }
}
