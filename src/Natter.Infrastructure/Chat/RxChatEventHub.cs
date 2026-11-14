using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Natter.Chat;
using Natter.Infrastructure.Events;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// In-process NLMP event hub (hot Rx stream). Subscriber failures are isolated, so a broken
/// feature plugin cannot terminate the stream for other subscribers.
/// </summary>
public sealed class RxChatEventHub
    : IChatEventSource, IChatEventPublisher, IChatEventDiagnostics, IObservable<ChatEvent>, IDisposable
{
    private readonly Subject<ChatEvent> _subject = new();
    private readonly ISubject<ChatEvent> _synchronizedSubject;
    private readonly ILogger<RxChatEventHub> _logger;
    private long _publishedCount;
    private int _subscriberCount;
    private bool _disposed;

    public RxChatEventHub(ILogger<RxChatEventHub> logger)
    {
        _logger = logger;
        _synchronizedSubject = Subject.Synchronize(_subject);
    }

    public long PublishedCount => Interlocked.Read(ref _publishedCount);

    public int SubscriberCount => Volatile.Read(ref _subscriberCount);

    public IObservable<ChatEvent> Events => this;

    public IDisposable Subscribe(IObserver<ChatEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _subscriberCount);
        var subscription = _synchronizedSubject.Subscribe(new SafeObserver<ChatEvent>(observer, _logger));

        return new Subscription(() =>
        {
            subscription.Dispose();
            Interlocked.Decrement(ref _subscriberCount);
        });
    }

    public void Publish(ChatEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sequence = Interlocked.Increment(ref _publishedCount);
        _logger.LogTrace(
            "Publishing NLMP event {EventType} from {Source} (seq {Sequence}).",
            chatEvent.Type,
            chatEvent.Source.Key,
            sequence);

        _synchronizedSubject.OnNext(chatEvent);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _subject.Dispose();
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
