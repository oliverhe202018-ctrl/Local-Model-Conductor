using LocalLLMManager.Guard.Service.Control;

namespace LocalLLMManager.Guard.Service.Workers;

/// <summary>
/// Periodic read-only discovery worker. Continuously probes OS processes, TCP ports, GPU/RAM/Commit,
/// and llama-swap state without ever interfering with external processes.
/// </summary>
public sealed class PeriodicDiscoveryWorker : BackgroundService
{
    private readonly GuardControlCoordinator _coordinator;
    private readonly TimeSpan _interval;

    public PeriodicDiscoveryWorker(GuardControlCoordinator coordinator, TimeSpan? interval = null)
    {
        _coordinator = coordinator;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _coordinator.RefreshObservationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Non-fatal; next tick retries
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
