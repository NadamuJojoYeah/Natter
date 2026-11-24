namespace Natter.Application.Chat;

public readonly record struct ConversationKey(
    string SourceKey,
    string ConversationId,
    string? GuildId,
    string? ActorId);
