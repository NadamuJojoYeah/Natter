using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Events;

namespace Natter.Infrastructure.Events;

/// <summary>
/// Rx implementation of the domain event stream port. A synchronized <see cref="Subject{T}"/>
/// provides the hot, in-process multicast stream; a safe observer wrapper prevents a single
/// faulty subscriber from terminating the stream for everyone else.
/// </summary>
public sealed class RxDomainEventBus : IDomainEventStream, IDomainEventPublisher, IDomainEventBusDiagnostics, IDisposable
{
    private readonly Subject<DomainEventEnvelope> _subject = new();
    private readonly ISubject<DomainEventEnvelope> _synchronizedSubject;
    private readonly ILogger<RxDomainEventBus> _logger;
    private long _publishedCount;
    private int _subscriberCount;
    private bool _disposed;

    public RxDomainEventBus(ILogger<RxDomainEventBus> logger)
    {
        _logger = logger;
        _synchronizedSubject = Subject.Synchronize(_subject);
    }

    public string Origin { get; init; } = "in-process";

    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    public int SubscriberCount => Volatile.Read(ref _subscriberCount);

    public IDisposable Subscribe(IObserver<DomainEventEnvelope> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _subscriberCount);
        var subscription = _synchronizedSubject.Subscribe(
            new SafeObserver<DomainEventEnvelope>(observer, _logger));

        return new Subscription(() =>
        {
            subscription.Dispose();
            Interlocked.Decrement(ref _subscriberCount);
        });
    }

    public ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var sequence = Interlocked.Increment(ref _publishedCount);
        var envelope = new DomainEventEnvelope(
            sequence,
            domainEvent,
            Origin,
            DateTimeOffset.UtcNow);

        _logger.LogTrace("Publishing domain event {EventName} (seq {Sequence}).", envelope.EventName, sequence);
        _synchronizedSubject.OnNext(envelope);

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subject.Dispose();
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
