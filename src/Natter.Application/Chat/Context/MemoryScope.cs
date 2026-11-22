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

    /// <summary>
    /// Explicit query filter only. Default retrieval must NOT set <see cref="MemoryQuery.Scope"/>:
    /// visibility is the union of user + conversation + guild + global, isolated per actor by the
    /// scope id itself. Strict equality here is for callers like a future <c>recall --scope actor</c>.
    /// </summary>
    public static bool IsVisibleToQuery(MemoryItem item, MemoryQuery query)
        => query.Scope is { } scope
            ? item.ScopeType == scope
              && string.Equals(item.ScopeId, Id(query.Source, query.Conversation, query.User, scope), StringComparison.Ordinal)
              && (query.IncludePrivate || !string.Equals(item.Sensitivity, "private", StringComparison.OrdinalIgnoreCase))
            : IsVisible(item, query.Source, query.Conversation, query.User, query.IncludePrivate);

    /// <summary>
    /// Default scope for new memories (P6 §3): profile.memory.scope, defaulting to the acting
    /// user's actor scope.
    /// </summary>
    public static MemoryScopeType DefaultWriteScope(RuntimeProfile? profile = null)
    {
        var configured = profile?.Memory.Scope;
        if (string.IsNullOrWhiteSpace(configured))
            return MemoryScopeType.User;

        return configured.Trim().ToLowerInvariant() switch
        {
            "conversation" => MemoryScopeType.Conversation,
            "guild" => MemoryScopeType.Guild,
            // Global is visible to every user of the source: it is never a silent default.
            // An operator must choose it explicitly (and gets a startup warning).
            "global" => MemoryScopeType.User,
            "actor" or "user" => MemoryScopeType.User,
            _ => MemoryScopeType.User,
        };
    }

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
