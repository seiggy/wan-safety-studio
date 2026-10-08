using System.Text.Json;
using System.Text.Json.Nodes;
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
    TimeProvider time)
{
    public const int MaxEpochs = 1000;

    // One GPU job at a time, like the shared generation node.
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Task _current = Task.CompletedTask;

    /// <summary>Completes when the current run (if any) has finished. Surfaces unexpected failures; used by tests and graceful shutdown.</summary>
    public Task WhenIdleAsync() => _current;

    public IReadOnlyList<TrainerManifest> Trainers() => registry.All();

    private static string RunKey(string project, string run, string name) => VisionService.Key(project, "runs", run, name);

    public async Task<TrainingRun> StartAsync(string project, string dataset, string trainer, string? preset, int? epochs, CancellationToken ct = default)
    {
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

        if (!await _busy.WaitAsync(0, ct))
        {
            throw new ConflictException("The GPU node is busy with another job. Wait for it to finish.");
        }

        TrainingRun run;
        try
        {
            run = new TrainingRun
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
            await SaveAsync(run, ct);
        }
        catch
        {
            _busy.Release();
            throw;
        }

        // Fire and forget: the run owns the busy lock until it finishes.
        _current = Task.Run(() => ExecuteAsync(run, manifest), CancellationToken.None);
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
        try
        {
            var data = Path.Combine(temp, "data");
            var output = Path.Combine(temp, "out");
            Directory.CreateDirectory(output);
            await SetAsync(run, RunStatus.Preparing);
            await MaterializeAsync(run.Project, run.Dataset, data);
            List<string> args = ["all", "--preset", run.Preset];
            if (run.Epochs is { } epochs)
            {
                args.AddRange(["--epochs", epochs.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }

            await SetAsync(run, RunStatus.Running);
            var code = await compute.RunAsync(manifest.Image, args, data, output, Path.Combine(output, "train.log"));
            await SetAsync(run, RunStatus.Uploading);
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(output, file).Replace('\\', '/');
                await vision.Store.UploadAsync(RunKey(run.Project, run.Id, "out/" + relative), file, overwrite: true);
            }

            if (code != 0)
            {
                throw new InvalidOperationException($"Trainer exited with code {code}; see out/train.log.");
            }

            var metrics = Path.Combine(output, "metrics.json");
            run.Metrics = File.Exists(metrics) ? Json.From<RunMetrics>(await File.ReadAllBytesAsync(metrics)) : null;
            await SetAsync(run, RunStatus.Completed);
        }
        catch (Exception error)
        {
            run.Error = error.Message.Length > 500 ? error.Message[..500] : error.Message;
            await SetAsync(run, RunStatus.Failed);
        }
        finally
        {
            _busy.Release();
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private async Task SetAsync(TrainingRun run, string status)
    {
        run.Status = status;
        await SaveAsync(run, CancellationToken.None);
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
