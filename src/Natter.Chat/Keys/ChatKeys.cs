namespace Natter.Chat;

/// <summary>
/// Structured conversation scope key. Storage, routing and deduplication MUST use this
/// (or the structured fields directly) and never reverse-parse a display key.
/// </summary>
public readonly record struct ConversationKey(Source Source, string ConversationId)
{
    public static ConversationKey From(Source source, Conversation conversation) => new(source, conversation.Id);

    public override string ToString() => $"{Source.Key}/{ConversationId}";
}

/// <summary>Structured message scope key: message ids are only unique per conversation.</summary>
public readonly record struct MessageKey(Source Source, string ConversationId, string MessageId)
{
    public static MessageKey From(Source source, Message message) => new(source, message.Conversation.Id, message.Id);

    public override string ToString() => $"{Source.Key}/{ConversationId}/{MessageId}";
}

/// <summary>Structured interaction scope key.</summary>
public readonly record struct InteractionKey(Source Source, string InteractionId)
{
    public static InteractionKey From(Source source, Interaction interaction) => new(source, interaction.Id);

    public override string ToString() => $"{Source.Key}/interaction/{InteractionId}";
}
