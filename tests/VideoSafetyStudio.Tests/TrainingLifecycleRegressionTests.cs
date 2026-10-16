using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Training;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Tests.Support;

namespace VideoSafetyStudio.Tests;

public sealed class TrainingLifecycleRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Shutdown_cancels_dataset_validation_before_acquiring_run_ownership()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var reached = Signal();
        workspace.Store.BeforeRead = async (key, ct) =>
        {
            if (key.EndsWith("/manifest.json", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var compute = new RecordingCompute();
        await using var service = Create(workspace, compute);
        var starting = service.StartAsync("demo", "v001", "test", null, null);
        await reached.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(0, compute.Calls);
    }

    [Fact]
    public async Task Shutdown_cancels_preparation_and_never_starts_compute()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var reached = Signal();
        var observed = Signal();
        workspace.Store.BeforeDownload = async (_, ct) =>
        {
            reached.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (ct.IsCancellationRequested) observed.TrySetResult(); }
        };
        var compute = new RecordingCompute();
        await using var service = Create(workspace, compute);
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await reached.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);

        Assert.True(observed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, compute.Calls);
        var failed = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("shutting down", failed.Error);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartAsync("demo", "v001", "test", null, null));
    }

    [Fact]
    public async Task Shutdown_owns_and_cancels_initial_record_persistence()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var reached = Signal();
        workspace.Store.BeforeWrite = async (key, _, ct) =>
        {
            if (key.Contains("/runs/", StringComparison.Ordinal))
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        var compute = new RecordingCompute();
        await using var service = Create(workspace, compute);
        var starting = service.StartAsync("demo", "v001", "test", null, null);
        await reached.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Equal(0, compute.Calls);
    }

    [Fact]
    public async Task Cancelled_trainer_salvages_its_log_and_checkpoint_with_independent_tokens()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var compute = new RecordingCompute(hang: true);
        await using var service = Create(workspace, compute);
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await compute.Entered.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);

        Assert.Equal("trainer diagnostics", System.Text.Encoding.UTF8.GetString(
            (await workspace.Store.ReadAsync($"vision/projects/demo/runs/{run.Id}/out/train.log"))!));
        Assert.Equal(new byte[] { 4, 5, 6 }, await workspace.Store.ReadAsync($"vision/projects/demo/runs/{run.Id}/out/checkpoint.bin"));
        var failed = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("shutting down", failed.Error);
    }

    [Fact]
    public async Task Stalled_upload_is_cancelled_and_salvage_has_a_bounded_separate_budget()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var reached = Signal();
        var cancellations = 0;
        workspace.Store.BeforeUpload = async (_, ct) =>
        {
            reached.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { if (ct.IsCancellationRequested) Interlocked.Increment(ref cancellations); }
        };
        await using var service = Create(workspace, new RecordingCompute(), cleanupSeconds: 0.2);
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await reached.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);

        Assert.Equal(2, cancellations);
        var failed = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("shutting down", failed.Error);
    }

    [Fact]
    public async Task Salvage_errors_never_replace_the_original_trainer_exit_failure()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        workspace.Store.BeforeUpload = (_, _) => Task.FromException(new IOException("salvage is unavailable"));
        await using var service = Create(workspace, new RecordingCompute(code: 23));
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        await service.WhenIdleAsync().WaitAsync(Watchdog);
        var failed = await service.GetAsync("demo", run.Id);
        Assert.Equal(RunStatus.Failed, failed.Status);
        Assert.Contains("code 23", failed.Error);
        Assert.DoesNotContain("salvage", failed.Error);
    }

    [Fact]
    public async Task Run_ownership_is_retained_until_salvage_and_terminal_save_finish()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var reached = Signal();
        var release = Signal();
        workspace.Store.BeforeUpload = async (_, ct) =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await using var service = Create(workspace, new RecordingCompute(code: 9));
        var run = await service.StartAsync("demo", "v001", "test", null, null);
        try
        {
            await reached.Task.WaitAsync(Watchdog);
            await Assert.ThrowsAsync<ConflictException>(() => service.StartAsync("demo", "v001", "test", null, null));
        }
        finally { release.TrySetResult(); }
        await service.WhenIdleAsync().WaitAsync(Watchdog);
        Assert.Equal(RunStatus.Failed, (await service.GetAsync("demo", run.Id)).Status);
        await service.StartAsync("demo", "v001", "test", null, null);
        await service.WhenIdleAsync().WaitAsync(Watchdog);
    }

    [Fact]
    public async Task Stalled_final_status_is_bounded_and_events_retain_the_original_failure()
    {
        using var workspace = new VisionRegressionWorkspace();
        await SeedAsync(workspace);
        var events = new FakeEvents();
        var reached = Signal();
        workspace.Store.BeforeWrite = async (key, data, ct) =>
        {
            if (key.Contains("/runs/", StringComparison.Ordinal) && Json.From<TrainingRun>(data.ToArray())!.Status == RunStatus.Failed)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        await using var service = Create(workspace, new RecordingCompute(code: 17), cleanupSeconds: 0.2, events: events);
        await service.StartAsync("demo", "v001", "test", null, null);
        await reached.Task.WaitAsync(Watchdog);
        await service.DisposeAsync().AsTask().WaitAsync(Watchdog);
        var final = Json.From<TrainingRun>(System.Text.Encoding.UTF8.GetBytes(events.Items.Last().Json))!;
        Assert.Equal(RunStatus.Failed, final.Status);
        Assert.Contains("code 17", final.Error);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task SeedAsync(VisionRegressionWorkspace workspace)
    {
        await workspace.SeedAsync(1);
        await workspace.Vision.FreezeDatasetAsync("demo", 0);
    }

    private static TrainingService Create(VisionRegressionWorkspace workspace, IComputeTarget compute, double cleanupSeconds = 1, FakeEvents? events = null) =>
        new(workspace.Vision, new Registry(), compute, events ?? new FakeEvents(), TimeProvider.System,
            Options.Create(new TrainingOptions { CleanupTimeoutSeconds = cleanupSeconds }));

    private sealed class Registry : ITrainerRegistry
    {
        private static readonly TrainerManifest Manifest = new("test", "1", "test:1", "MIT", "MIT", ["nano"], "nano");
        public IReadOnlyList<TrainerManifest> All() => [Manifest];
        public TrainerManifest Get(string name) => Manifest;
    }

    private sealed class RecordingCompute(bool hang = false, int code = 0) : IComputeTarget
    {
        public string Name => "recording";
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; } = Signal();
        public async Task<int> RunAsync(string image, IReadOnlyList<string> args, string dataDirectory, string outDirectory, string logPath, CancellationToken ct = default)
        {
            Calls++;
            await File.WriteAllTextAsync(logPath, "trainer diagnostics", ct);
            await File.WriteAllBytesAsync(Path.Combine(outDirectory, "checkpoint.bin"), [4, 5, 6], ct);
            Entered.TrySetResult();
            if (hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return code;
        }
    }
}
