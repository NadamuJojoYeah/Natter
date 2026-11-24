using Natter.Abstractions.Plugins;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Requeues Pending tasks and expires stale Running leases at host start.</summary>
public sealed class TaskRecoveryLifecycle(ITaskStore store, ITaskDispatcher dispatcher) : IPluginLifecycle
{
    private CancellationTokenSource? _cts;
    private Task? _watchdog;
    private Task? _recovery;

    public async ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var pending = (await store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false))
            .Where(task => task.Status == AgentTaskStatus.Pending)
            .ToArray();

        _cts = new CancellationTokenSource();
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token), CancellationToken.None);

        if (pending.Length > 0)
        {
            // S2-B7: upstreams are registered later (NPS activation happens after lifecycles), so
            // recovering immediately would enqueue tasks whose result cannot be delivered yet.
            // Wait (bounded) for the matching transport before the first dispatch.
            var registry = services.GetService(typeof(IChatUpstreamRegistry)) as IChatUpstreamRegistry;
            _recovery = Task.Run(() => RecoverAsync(registry, pending, _cts.Token), CancellationToken.None);
        }
    }

    public async ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        foreach (var task in new[] { _watchdog, _recovery })
        {
            if (task is null)
                continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _recovery = null;
        _cts?.Dispose();
        _cts = null;
        _watchdog = null;
    }

    private async Task RecoverAsync(
        IChatUpstreamRegistry? registry,
        IReadOnlyList<AgentTask> pending,
        CancellationToken cancellationToken)
    {
        foreach (var task in pending)
        {
            await WaitForTransportAsync(registry, task.Source, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                return;
            await dispatcher.EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForTransportAsync(
        IChatUpstreamRegistry? registry,
        Source source,
        CancellationToken cancellationToken)
    {
        if (registry is null)
            return;

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            if (registry.TryGet(source, out _))
                return;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
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
