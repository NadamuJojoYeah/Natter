namespace Natter.Chat;

/// <summary>Read-only NLMP event consumer (audit, metrics, ...). Must be idempotent.</summary>
public interface IChatEventSubscriber
{
    ValueTask HandleAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default);
}
