namespace Natter.Chat;

/// <summary>Conversation shape. Missing platform knowledge must fall back to <see cref="Unknown"/>.</summary>
public enum ConversationKind
{
    Direct,
    Group,
    Guild,
    Thread,
    Unknown,
}

/// <summary>
/// A message conversation: direct chat, group, guild channel or thread.
/// <see cref="Id"/> is only unique inside one <see cref="Source"/>.
/// </summary>
public sealed record Conversation(string Id, ConversationKind Kind, string? ParentId = null, string? GuildId = null, string? Title = null)
{
    public static Conversation Unknown(string id) => new(id, ConversationKind.Unknown);
}
