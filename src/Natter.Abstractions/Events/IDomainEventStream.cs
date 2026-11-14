using Natter.Abstractions.Ddd;

namespace Natter.Abstractions.Events;

/// <summary>
/// Envelope flowing through the in-process domain event stream. Carries transport
/// metadata (sequence, origin, observation time) next to the immutable domain event.
/// </summary>
public sealed record DomainEventEnvelope(
    long Sequence,
    IDomainEvent Event,
    string Origin,
    DateTimeOffset ObservedAt,
    IReadOnlyDictionary<string, string?>? Metadata = null)
{
    public Guid EventId => Event.EventId;

    public string EventName => Event.EventName;

    public DateTimeOffset OccurredAt => Event.OccurredAt;
}

/// <summary>Observable hot stream of domain events (Rx port).</summary>
public interface IDomainEventStream : IObservable<DomainEventEnvelope>
{
}

/// <summary>Outbound port used by adapters that push events into the stream.</summary>
public interface IDomainEventPublisher
{
    ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches buffered aggregate events to the event stream and clears the buffer.
/// </summary>
public interface IDomainEventDispatcher
{
    ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);

    ValueTask<int> DispatchAsync(IHasDomainEvents aggregate, CancellationToken cancellationToken = default);
}

/// <summary>Read-only replay buffer of event envelopes (diagnostics / CLI history).</summary>
public interface IEventJournal
{
    int Count { get; }

    IReadOnlyList<DomainEventEnvelope> Snapshot(int? limit = null);

    void Clear();
}

/// <summary>Runtime statistics for the domain event bus adapter.</summary>
public interface IDomainEventBusDiagnostics
{
    long PublishedCount { get; }

    int SubscriberCount { get; }
}
