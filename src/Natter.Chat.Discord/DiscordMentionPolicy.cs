using Discord;

namespace Natter.Chat.Discord;

/// <summary>
/// Outbound mention policy (S2-A10). Discord only honours mentions listed in
/// <see cref="AllowedMentions.AllowedTypes"/>; the previous implementation set
/// <c>UserIds</c> but left <c>AllowedTypes</c> null, so an explicit user mention never actually
/// pinged anyone. We now always allow the <c>Users</c> type so configured ids work, while
/// <c>@everyone</c>/<c>@here</c> and role mentions stay disabled.
/// </summary>
public static class DiscordMentionPolicy
{
    /// <summary>Mention types that must never be enabled by outbound replies.</summary>
    public const AllowedMentionTypes NeverAllowed = AllowedMentionTypes.Everyone | AllowedMentionTypes.Roles;

    /// <summary>Mention types used when the reply carries explicit user mentions.</summary>
    public const AllowedMentionTypes ExplicitUsersOnly = AllowedMentionTypes.Users;

    /// <summary>
    /// Builds the allowed-mention payload for a reply. No ids means "mentions off"; ids means
    /// "these users only" — never everyone/here/roles.
    /// </summary>
    public static AllowedMentions Build(IReadOnlyList<string>? mentionUserIds)
    {
        if (mentionUserIds is not { Count: > 0 })
            return AllowedMentions.None;

        var ids = new List<ulong>(mentionUserIds.Count);
        foreach (var id in mentionUserIds)
        {
            if (ulong.TryParse(id, out var parsed))
                ids.Add(parsed);
        }

        if (ids.Count == 0)
            return AllowedMentions.None;

        return new AllowedMentions(ExplicitUsersOnly)
        {
            UserIds = ids,
            MentionRepliedUser = false,
        };
    }
}
