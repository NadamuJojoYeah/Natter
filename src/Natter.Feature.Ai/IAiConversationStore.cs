using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>Provider-neutral conversation memory boundary used by the shared responder.</summary>
public interface IAiConversationStore
{
    string GetKey(Source source, Conversation conversation, string senderId);

    IReadOnlyList<ChatMessage> GetMessages(string key);

    void AppendTurn(string key, string userText, string assistantText);
}
