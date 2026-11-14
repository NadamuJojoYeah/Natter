using System.Collections.Concurrent;
using Natter.Abstractions.Ddd;
using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Adapters;

/// <summary>
/// Open-generic in-memory repository adapter. Registered as
/// <c>IRepository&lt;TAggregate, TId&gt;</c>, so every aggregate gets a working
/// persistence port without extra wiring. Production hosts replace it with a real adapter.
/// </summary>
public sealed class InMemoryRepository<TAggregate, TId> : IRepository<TAggregate, TId>
    where TAggregate : class, IAggregateRoot<TId>
    where TId : notnull
{
    private readonly ConcurrentDictionary<TId, TAggregate> _store = new();

    public Task<TAggregate?> FindAsync(TId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.TryGetValue(id, out var aggregate) ? aggregate : null);
    }

    public Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_store.TryAdd(aggregate.Id, aggregate))
            throw new InvalidOperationException($"Aggregate '{aggregate.Id}' already exists.");

        return Task.CompletedTask;
    }

    public Task UpdateAsync(TAggregate aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_store.ContainsKey(aggregate.Id))
            throw new InvalidOperationException($"Aggregate '{aggregate.Id}' does not exist.");

        _store[aggregate.Id] = aggregate;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(TId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.TryRemove(id, out _));
    }

    /// <summary>Test/diagnostic helper; not part of the port.</summary>
    public int Count => _store.Count;
}
