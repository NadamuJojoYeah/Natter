using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>Provider-neutral conversation memory boundary used by the shared responder.</summary>
public interface IAiConversationStore
{
    string GetKey(Source source, Conversation conversation, string senderId);

    ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(string key, CancellationToken cancellationToken = default);

    ValueTask AppendTurnAsync(string key, string userText, string assistantText, CancellationToken cancellationToken = default);

    ValueTask AppendTurnAsync(
        string key,
        string userText,
        string assistantText,
        string? sourceEventId,
        CancellationToken cancellationToken = default)
        => AppendTurnAsync(key, userText, assistantText, cancellationToken);
}
