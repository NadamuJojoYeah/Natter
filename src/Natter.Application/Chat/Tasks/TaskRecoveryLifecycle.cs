using Natter.Abstractions.Plugins;

namespace Natter.Application.Chat;

/// <summary>Requeues Pending tasks and expires stale Running leases at host start.</summary>
public sealed class TaskRecoveryLifecycle(ITaskStore store, ITaskDispatcher dispatcher) : IPluginLifecycle
{
    public async ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        foreach (var task in await store.ListRecoverableAsync(cancellationToken).ConfigureAwait(false))
        {
            if (task.Status == AgentTaskStatus.Pending)
                await dispatcher.EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
