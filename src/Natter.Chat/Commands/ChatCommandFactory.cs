using Natter.Abstractions.Ports;

namespace Natter.Chat;

/// <summary>
/// Builds commands with ids, timestamps and idempotency keys. Features should use this
/// factory instead of filling envelopes by hand.
/// </summary>
public sealed class ChatCommandFactory(IClock clock)
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public MessageSendCommand Send(
        Source source,
        Conversation conversation,
        Content content,
        string? replyTo = null,
        bool ephemeral = false,
        string? idempotencyKey = null)
    {
        var id = NewCommandId();
        return new MessageSendCommand
        {
            Id = id,
            Source = source,
            IssuedAt = _clock.UtcNow,
            IdempotencyKey = idempotencyKey ?? id,
            Payload = new MessageSendPayload(conversation, content, replyTo, ephemeral),
        };
    }

    public MessageUpdateCommand Update(Source source, Conversation conversation, string messageId, Content content)
        => new()
        {
            Id = NewCommandId(),
            Source = source,
            IssuedAt = _clock.UtcNow,
            Payload = new MessageUpdatePayload(conversation, messageId, content),
        };

    public MessageDeleteCommand Delete(Source source, Conversation conversation, string messageId)
        => new()
        {
            Id = NewCommandId(),
            Source = source,
            IssuedAt = _clock.UtcNow,
            Payload = new MessageDeletePayload(conversation, messageId),
        };

    public TypingStartCommand Typing(Source source, Conversation conversation)
        => new()
        {
            Id = NewCommandId(),
            Source = source,
            IssuedAt = _clock.UtcNow,
            Payload = new TypingStartPayload(conversation),
        };

    public InteractionRespondCommand Respond(
        Source source,
        Interaction interaction,
        Content content,
        bool ephemeral = false,
        bool updateOriginal = false)
        => new()
        {
            Id = NewCommandId(),
            Source = source,
            IssuedAt = _clock.UtcNow,
            Payload = new InteractionRespondPayload(
                interaction.Id,
                content,
                interaction.ReplyToken,
                ephemeral,
                updateOriginal),
        };

    private static string NewCommandId() => $"cmd:{Guid.NewGuid():N}";
}
