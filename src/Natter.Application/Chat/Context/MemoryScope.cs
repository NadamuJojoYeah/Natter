using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Single source of truth for memory scope ids and visibility checks.</summary>
public static class MemoryScope
{
    public static string Id(Source source, Conversation conversation, User user, MemoryScopeType type)
        => type switch
        {
            MemoryScopeType.User => $"{source.Key}|user|{user.Id}",
            MemoryScopeType.Conversation => $"{source.Key}|conversation|{conversation.Id}",
            MemoryScopeType.Guild => $"{source.Key}|guild|{conversation.GuildId ?? conversation.Id}",
            MemoryScopeType.Global => $"{source.Key}|global",
            _ => source.Key,
        };

    public static bool IsVisible(MemoryItem item, Source source, Conversation conversation, User user, bool includePrivate)
    {
        if (!includePrivate && string.Equals(item.Sensitivity, "private", StringComparison.OrdinalIgnoreCase))
            return false;

        return item.ScopeType switch
        {
            MemoryScopeType.User => item.ScopeId == Id(source, conversation, user, MemoryScopeType.User),
            MemoryScopeType.Conversation => item.ScopeId == Id(source, conversation, user, MemoryScopeType.Conversation),
            MemoryScopeType.Guild => item.ScopeId == Id(source, conversation, user, MemoryScopeType.Guild),
            MemoryScopeType.Global => item.ScopeId == Id(source, conversation, user, MemoryScopeType.Global),
            _ => false,
        };
    }
}
