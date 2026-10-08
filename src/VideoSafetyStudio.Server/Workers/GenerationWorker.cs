using VideoSafetyStudio.Application.Generation;

namespace VideoSafetyStudio.Server.Workers;

/// <summary>Runs queued generation jobs, as many at once as the backend allows (ComfyUI: one GPU; Azure ML: the node queues them), and resumes jobs after a restart.</summary>
public sealed class GenerationWorker(GenerationService generation, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await generation.RecoverAsync(stoppingToken);
            var slots = new SemaphoreSlim(Math.Max(1, generation.Backend.MaxConcurrency));
            await foreach (var item in generation.DequeueAllAsync(stoppingToken))
            {
                await slots.WaitAsync(stoppingToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await generation.ProcessAsync(item, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        // shutting down
                    }
                    catch (Exception error)
                    {
                        logger.LogError(error, "Generation job {Job} stopped unexpectedly", item.Job.Name);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None);
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
    }
}
