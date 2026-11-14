using Microsoft.Extensions.Logging;
using Natter.Abstractions.Events;

namespace Natter.Infrastructure.Events;

/// <summary>Dispatch adapter connecting aggregates to the event stream port.</summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IDomainEventPublisher _publisher;
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(IDomainEventPublisher publisher, ILogger<DomainEventDispatcher> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        => _publisher.PublishAsync(domainEvent, cancellationToken);

    public async ValueTask<int> DispatchAsync(
        Natter.Abstractions.Ddd.IHasDomainEvents aggregate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        var events = aggregate.DomainEvents.ToArray();
        if (events.Length == 0)
            return 0;

        // Clear first: publishing must not be repeated if a downstream adapter fails.
        aggregate.ClearDomainEvents();

        foreach (var domainEvent in events)
            await _publisher.PublishAsync(domainEvent, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Dispatched {Count} domain event(s).", events.Length);
        return events.Length;
    }
}
