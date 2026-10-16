using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application.Abstractions;
using VideoSafetyStudio.Application.Vision;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Domain.Vision;

namespace VideoSafetyStudio.Application.Training;

/// <summary>Materializes a frozen dataset, runs a trainer image on a compute target, and records the run.</summary>
public sealed class TrainingService(
    VisionService vision,
    ITrainerRegistry registry,
    IComputeTarget compute,
    IEventPublisher events,
    TimeProvider time,
    IOptions<TrainingOptions>? options = null) : IAsyncDisposable
{
    public const int MaxEpochs = 1000;

    // One GPU job at a time, like the shared generation node.
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycle = new();
    private Task _current = Task.CompletedTask;
    private Task? _shutdown;

    private TimeSpan MaxRun => TimeSpan.FromHours(Math.Clamp(options?.Value.MaxRunHours ?? new TrainingOptions().MaxRunHours, 0.01, 168));
    private TimeSpan CleanupPhase => TimeSpan.FromSeconds(Math.Clamp(options?.Value.CleanupTimeoutSeconds ?? 30, 0.1, 300) / 2);

    /// <summary>Host shutdown: cancels the running trainer (the compute target kills its container) and waits for the run record to settle.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            return new ValueTask(_shutdown ??= StopAsync(_current));
        }
    }

    private async Task StopAsync(Task current)
    {
        await _stopping.CancelAsync();
        try
        {
            await current;
        }
        catch (Exception)
        {
            // Shutting down; nothing useful to do with a failed bookkeeping write.
        }

        _stopping.Dispose();
    }

    /// <summary>Completes when the current run (if any) has finished. Surfaces unexpected failures; used by tests and graceful shutdown.</summary>
    public Task WhenIdleAsync()
    {
        lock (_lifecycle)
        {
            return _current;
        }
    }

    public IReadOnlyList<TrainerManifest> Trainers() => registry.All();

    private static string RunKey(string project, string run, string name) => VisionService.Key(project, "runs", run, name);

    public async Task<TrainingRun> StartAsync(string project, string dataset, string trainer, string? preset, int? epochs, CancellationToken ct = default)
    {
        CancellationTokenSource request;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            request = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        }

        using var requestLifetime = request;
        ct = request.Token;
        var manifest = registry.Get(trainer);
        preset ??= manifest.DefaultPreset;
        if (!manifest.Presets.Contains(preset))
        {
            throw new ValidationException($"Preset must be one of [{string.Join(", ", manifest.Presets)}].");
        }

        if (epochs is < 1 or > MaxEpochs)
        {
            throw new ValidationException($"Epochs must be 1-{MaxEpochs}.");
        }

        if (!(await vision.ListDatasetsAsync(project, ct)).Any(d => d.Version == dataset))
        {
            throw new NotFoundException("Dataset version not found.");
        }

        var run = new TrainingRun
        {
            Id = "r" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)),
            Project = project,
            Dataset = dataset,
            Trainer = trainer,
            TrainerVersion = manifest.Version,
            Preset = preset,
            Epochs = epochs,
            Compute = compute.Name,
            CreatedAt = time.GetUtcNow().ToString("o"),
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            ct.ThrowIfCancellationRequested();
            if (!_busy.Wait(0))
            {
                throw new ConflictException("The GPU node is busy with another job. Wait for it to finish.");
            }

            // Publish ownership before yielding: shutdown must also own an in-flight initial status save.
            _current = Task.Run(async () =>
            {
                try
                {
                    using var starting = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
                    await SaveAsync(run, starting.Token);
                    started.SetResult();
                    await ExecuteAsync(run, manifest);
                }
                catch (Exception error)
                {
                    started.TrySetException(error);
                    throw;
                }
                finally
                {
                    _busy.Release();
                }
            }, CancellationToken.None);
        }

        await started.Task;
        return run;
    }

    public async Task<TrainingRun> GetAsync(string project, string runId, CancellationToken ct = default)
    {
        var data = await vision.Store.ReadAsync(RunKey(project, runId, "run.json"), ct) ?? throw new NotFoundException("Run not found.");
        return Json.From<TrainingRun>(data)!;
    }

    public async Task<IReadOnlyList<TrainingRun>> ListAsync(string project, CancellationToken ct = default)
    {
        var runs = new List<TrainingRun>();
        foreach (var key in (await vision.Store.ListAsync(VisionService.Key(project, "runs"), ct)).Where(k => k.EndsWith("/run.json", StringComparison.Ordinal)))
        {
            runs.Add(Json.From<TrainingRun>((await vision.Store.ReadAsync(key, ct))!)!);
        }

        return [.. runs.OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal)];
    }

    private async Task ExecuteAsync(TrainingRun run, TrainerManifest manifest)
    {
        var temp = Path.Combine(Path.GetTempPath(), "vision-train-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(temp, "out");
        using var deadline = new CancellationTokenSource(MaxRun, time);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, deadline.Token);
        var ct = limit.Token;
        try
        {
            var data = Path.Combine(temp, "data");
            Directory.CreateDirectory(output);
            await SetAsync(run, RunStatus.Preparing, ct);
            await MaterializeAsync(run.Project, run.Dataset, data, ct);
            List<string> args = ["all", "--preset", run.Preset];
            if (run.Epochs is { } epochs)
            {
                args.AddRange(["--epochs", epochs.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }

            await SetAsync(run, RunStatus.Running, ct);
            ct.ThrowIfCancellationRequested();
            var code = await compute.RunAsync(manifest.Image, args, data, output, Path.Combine(output, "train.log"), ct);

            // Record the trainer failure before upload so a salvage failure cannot hide its exit code.
            if (code != 0)
            {
                throw new InvalidOperationException($"Trainer exited with code {code}; see out/train.log.");
            }

            await SetAsync(run, RunStatus.Uploading, ct);
            await UploadArtifactsAsync(run, output, ct);
            var metrics = Path.Combine(output, "metrics.json");
            run.Metrics = File.Exists(metrics) ? Json.From<RunMetrics>(await File.ReadAllBytesAsync(metrics, ct)) : null;
            await SetAsync(run, RunStatus.Completed, ct);
        }
        catch (Exception error)
        {
            var message = error is OperationCanceledException && limit.IsCancellationRequested
                ? _stopping.IsCancellationRequested
                    ? "Training was cancelled because the server is shutting down."
                    : $"Training exceeded the maximum run time of {MaxRun.TotalHours:0.##} hours and was stopped."
                : error.Message;
            run.Error = message.Length > 500 ? message[..500] : message;
            // Reserve half the cleanup budget for status even when artifact storage stalls.
            using (var salvage = new CancellationTokenSource(CleanupPhase, time))
            {
                try
                {
                    await UploadArtifactsAsync(run, output, salvage.Token, bestEffort: true);
                }
                catch (Exception)
                {
                    // Preserve the original training outcome.
                }
            }

            using var status = new CancellationTokenSource(CleanupPhase, time);
            try
            {
                await SetAsync(run, RunStatus.Failed, status.Token);
            }
            catch (Exception)
            {
                // The store may be unavailable through shutdown; keep the original failure in the event.
                events.Publish("run", run);
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A locked temp file must not replace the run's real outcome.
            }
        }
    }

    private async Task UploadArtifactsAsync(TrainingRun run, string output, CancellationToken ct, bool bestEffort = false)
    {
        if (!Directory.Exists(output))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(output, file).Replace('\\', '/');
            try
            {
                await vision.Store.UploadAsync(RunKey(run.Project, run.Id, "out/" + relative), file, overwrite: true, ct);
            }
            catch (Exception) when (bestEffort && !ct.IsCancellationRequested)
            {
                // One inaccessible checkpoint should not prevent salvaging the remaining diagnostics.
            }
        }
    }

    private async Task SetAsync(TrainingRun run, string status, CancellationToken ct)
    {
        run.Status = status;
        await SaveAsync(run, ct);
    }

    private async Task SaveAsync(TrainingRun run, CancellationToken ct)
    {
        await vision.Store.WriteAsync(RunKey(run.Project, run.Id, "run.json"), Json.ToBytes(run), true, ct);
        events.Publish("run", run);
    }

    /// <summary>Writes the shared trainer dataset layout (see trainers/sdk.py): classes.json, train.json, val.json, images/.</summary>
    public async Task MaterializeAsync(string project, string dataset, string destination, CancellationToken ct = default)
    {
        var classes = (await vision.GetProjectAsync(project, ct)).Classes;
        var baseKey = VisionService.Key(project, "datasets", dataset);
        var images = Path.Combine(destination, "images");
        Directory.CreateDirectory(images);
        foreach (var split in new[] { "train", "val" })
        {
            var raw = await vision.Store.ReadAsync($"{baseKey}/{split}.json", ct) ?? throw new NotFoundException($"Dataset {dataset} has no {split} split.");
            var coco = JsonNode.Parse(raw)!.AsObject();
            foreach (var image in coco["images"]!.AsArray().Select(n => n!.AsObject()))
            {
                var fileName = image["file_name"]!.GetValue<string>();
                var name = $"{image["video"]!.GetValue<string>()}_{Path.GetFileName(fileName)}";
                await vision.Store.DownloadAsync(VisionService.Root + project + "/" + fileName, Path.Combine(images, name), ct);
                image["file_name"] = "images/" + name;
            }

            await File.WriteAllTextAsync(Path.Combine(destination, split + ".json"), coco.ToJsonString(), ct);
        }

        await File.WriteAllTextAsync(Path.Combine(destination, "classes.json"), JsonSerializer.Serialize(classes), ct);
    }
}
