using VideoSafetyStudio.Infrastructure;
using VideoSafetyStudio.Infrastructure.Operations;

namespace VideoSafetyStudio.Server.Workers;

/// <summary>
/// Reconciles interrupted operations once at start, then keeps the submission gate's durable snapshot fresh. Until recovery
/// succeeds (and whenever the store is unreachable long enough for the snapshot to go stale) generation is fenced.
/// </summary>
public sealed class StudioStateService(StudioOperations operations, HostedGenerationHolder hosted, ILogger<StudioStateService> logger) : BackgroundService
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (operations.Lifecycle is not { } lifecycle) return;
        var recovered = false;
        using var timer = new PeriodicTimer(Refresh);
        do
        {
            try
            {
                if (!recovered)
                {
                    await lifecycle.RecoverAsync(stoppingToken);
                    recovered = true;
                }
                else
                {
                    await lifecycle.RefreshAsync(stoppingToken);
                }

                if (hosted.Value is { } generation)
                {
                    await generation.Settings.RefreshAsync(stoppingToken);
                    await generation.Trainers.RefreshAsync(stoppingToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Operational state is unavailable; submissions remain fenced.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
