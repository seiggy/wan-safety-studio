using VideoSafetyStudio.Application.Generation;

namespace VideoSafetyStudio.Server.Workers;

/// <summary>Runs queued generation jobs, as many at once as the backend allows (ComfyUI: one GPU; Azure ML: the node queues them), and resumes jobs after a restart.</summary>
public sealed class GenerationWorker(GenerationService generation, ILogger<GenerationWorker> logger) : BackgroundService
{
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (ExecuteTask is { } execution)
        {
            // BackgroundService silently returns when the host budget expires. Never report a successful drain in that case.
            try
            {
                await execution.WaitAsync(cancellationToken);
            }
            catch when (execution.IsCompleted)
            {
                // The worker had already ended on its own and logged why (ExecuteAsync); that is not an undrained shutdown.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, generation.Backend.MaxConcurrency));
        var running = new HashSet<Task>();

        async Task RunAsync(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Accepted remote work retains its durable identity for the next host.
            }
            catch (Exception error)
            {
                logger.LogError(error, "Generation job {Job} stopped unexpectedly", name);
            }
            finally
            {
                slots.Release();
            }
        }

        async Task ScheduleAsync(Func<Task> action, string name)
        {
            await slots.WaitAsync(stoppingToken);
            running.RemoveWhere(task => task.IsCompleted);
            running.Add(RunAsync(action, name));
        }

        try
        {
            foreach (var job in await generation.RecoverAsync(stoppingToken))
            {
                await ScheduleAsync(() => generation.ResumeAsync(job, stoppingToken), job.Name);
            }
            await foreach (var item in generation.DequeueAllAsync(stoppingToken))
            {
                await ScheduleAsync(() => generation.ProcessAsync(item, stoppingToken), item.Job.Name);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception error)
        {
            logger.LogCritical(error, "Generation worker stopped unexpectedly");
            throw;
        }
        finally
        {
            await Task.WhenAll(running);
        }
    }
}
