using Natter.Abstractions.Ddd;

namespace Natter.Abstractions.Ports;

/// <summary>
/// Outbound persistence port for aggregate roots. Adapters decide whether the
/// implementation is in-memory, relational, document based, remote, ...
/// </summary>
/// <typeparam name="TAggregate">Aggregate root type.</typeparam>
/// <typeparam name="TId">Identity type of the aggregate.</typeparam>
public interface IRepository<TAggregate, in TId>
    where TAggregate : class, IAggregateRoot<TId>
    where TId : notnull
{
    Task<TAggregate?> FindAsync(TId id, CancellationToken cancellationToken = default);

    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);

    Task UpdateAsync(TAggregate aggregate, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(TId id, CancellationToken cancellationToken = default);
}

/// <summary>Transactional boundary port (commit unit of work).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
