namespace Natter.Abstractions.Events;

/// <summary>A fact that happened inside a domain aggregate.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }

    string EventName { get; }
}

/// <summary>
/// Convenience base record for domain events. Derive as
/// <c>public sealed record OrderPlaced(Guid OrderId) : DomainEvent;</c>
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    protected DomainEvent(DateTimeOffset? occurredAt = null)
        => OccurredAt = occurredAt ?? DateTimeOffset.UtcNow;

    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset OccurredAt { get; init; }

    public string EventName => GetType().Name;
}
