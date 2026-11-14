namespace Natter.Chat;

/// <summary>Hot, in-process stream of NLMP events.</summary>
public interface IChatEventSource
{
    IObservable<ChatEvent> Events { get; }
}

/// <summary>Sink used by adapters/ingress to fan events out to subscribers.</summary>
public interface IChatEventPublisher
{
    void Publish(ChatEvent chatEvent);
}

/// <summary>Diagnostics for the in-process event hub.</summary>
public interface IChatEventDiagnostics
{
    long PublishedCount { get; }

    int SubscriberCount { get; }
}

/// <summary>
/// Deduplicating admission point between adapters and the event hub. Returns false for a
/// replayed/duplicate event, which must not trigger a second AI reply.
/// </summary>
public interface IChatEventIngress
{
    bool TryPublish(ChatEvent chatEvent);
}
