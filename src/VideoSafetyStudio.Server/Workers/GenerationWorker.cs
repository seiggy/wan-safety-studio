using VideoSafetyStudio.Application.Generation;

namespace VideoSafetyStudio.Server.Workers;

/// <summary>Runs queued generation jobs one at a time (like the single GPU node) and resumes jobs after a restart.</summary>
public sealed class GenerationWorker(GenerationService generation, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await generation.RecoverAsync(stoppingToken);
            await foreach (var item in generation.DequeueAllAsync(stoppingToken))
            {
                await generation.ProcessAsync(item, stoppingToken);
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
