namespace Natter.Application.Chat;

public sealed record StoredMessage(
    string Role,
    string Text,
    string? SourceEventId,
    DateTimeOffset CreatedAt,
    long MessageId = 0);
