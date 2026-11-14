namespace Natter.Chat;

/// <summary>Payload of <c>message.send</c>.</summary>
public sealed record MessageSendPayload(
    Conversation Conversation,
    Content Content,
    string? ReplyTo = null,
    bool Ephemeral = false);

/// <summary>Payload of <c>message.update</c>; replaces the message content.</summary>
public sealed record MessageUpdatePayload(Conversation Conversation, string MessageId, Content Content);

/// <summary>Payload of <c>message.delete</c>.</summary>
public sealed record MessageDeletePayload(Conversation Conversation, string MessageId);

/// <summary>Payload of <c>typing.start</c>.</summary>
public sealed record TypingStartPayload(Conversation Conversation);

/// <summary>
/// Payload of <c>interaction.respond</c>. <see cref="ReplyToken"/> is an opaque, short-lived
/// adapter secret: never log, persist or feed it to an AI prompt.
/// </summary>
public sealed record InteractionRespondPayload(
    string InteractionId,
    Content Content,
    string? ReplyToken = null,
    bool Ephemeral = false,
    bool UpdateOriginal = false)
{
    public override string ToString()
        => $"InteractionRespondPayload {{ InteractionId = {InteractionId}, ReplyToken = {(ReplyToken is null ? "null" : "***")} }}";
}
