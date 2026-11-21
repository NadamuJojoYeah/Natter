using Natter.Abstractions.Plugins;

namespace Natter.Application.Chat;

/// <summary>Requeues Pending tasks and expires stale Running leases at host start.</summary>
public sealed class TaskRecoveryLifecycle(ITaskStore store, ITaskDispatcher dispatcher) : IPluginLifecycle
{
    private CancellationTokenSource? _cts;
    private Task? _watchdog;

    public async ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (var task in await store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (task.Status == AgentTaskStatus.Pending)
                await dispatcher.EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
        }

        _cts = new CancellationTokenSource();
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token), CancellationToken.None);
    }

    public async ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        if (_watchdog is not null)
        {
            try
            {
                await _watchdog.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        _cts?.Dispose();
        _cts = null;
        _watchdog = null;
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // The next tick retries; a lease sweep must not kill the host.
            }
        }
    }
}
