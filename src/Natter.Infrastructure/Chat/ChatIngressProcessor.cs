using Microsoft.Extensions.Logging;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>Deduplicates replayed events before they reach the in-process hub.</summary>
public sealed class ChatIngressProcessor : IChatEventIngress
{
    private readonly IChatEventPublisher _publisher;
    private readonly ChatEventDeduplicator _deduplicator;
    private readonly ILogger<ChatIngressProcessor> _logger;

    public ChatIngressProcessor(
        IChatEventPublisher publisher,
        ChatEventDeduplicator deduplicator,
        ILogger<ChatIngressProcessor> logger)
    {
        _publisher = publisher;
        _deduplicator = deduplicator;
        _logger = logger;
    }

    public bool TryPublish(ChatEvent chatEvent)
    {
        if (!_deduplicator.TryAccept(chatEvent))
        {
            _logger.LogDebug(
                "Ignoring duplicate NLMP event {EventId} ({EventType}) from {Source}.",
                chatEvent.Id,
                chatEvent.Type,
                chatEvent.Source.Key);
            return false;
        }

        _publisher.Publish(chatEvent);
        return true;
    }
}
