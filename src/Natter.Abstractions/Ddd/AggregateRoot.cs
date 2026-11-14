using Natter.Abstractions.Events;

namespace Natter.Abstractions.Ddd;

/// <summary>Implement this on a consistency boundary that buffers domain events.</summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>Non-generic aggregate root marker used by repositories and dispatchers.</summary>
public interface IAggregateRoot : IHasDomainEvents
{
}

/// <summary>An aggregate root is the only entry point to a consistency boundary.</summary>
public interface IAggregateRoot<out TId> : IEntity<TId>, IAggregateRoot
    where TId : notnull
{
}

/// <summary>
/// Base class for aggregate roots. Domain events are buffered until the surrounding
/// unit of work dispatches them through the domain event dispatcher port.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly IReadOnlyCollection<IDomainEvent> _readOnlyEvents;

    protected AggregateRoot(TId id)
        : base(id)
    {
        _readOnlyEvents = _domainEvents.AsReadOnly();
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _readOnlyEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>Records a domain event on this aggregate.</summary>
    protected void Raise(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
