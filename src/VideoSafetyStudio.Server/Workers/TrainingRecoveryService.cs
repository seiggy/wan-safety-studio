using VideoSafetyStudio.Application.Training;

namespace VideoSafetyStudio.Server.Workers;

/// <summary>Settles or follows training runs a previous process left in flight. Retried until the run store is readable; never creates remote work.</summary>
public sealed class TrainingRecoveryService(TrainingService training, ILogger<TrainingRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await training.RecoverAsync(stoppingToken);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Training runs could not be recovered yet; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}