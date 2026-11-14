namespace Natter.Chat;

/// <summary>A new message arrived.</summary>
public sealed record MessageCreatedEvent : ChatEvent<MessagePayload>
{
    public override string Type => ChatEventTypes.MessageCreated;
}

/// <summary>A message was edited; the payload is a complete replacement snapshot.</summary>
public sealed record MessageUpdatedEvent : ChatEvent<MessagePayload>
{
    public override string Type => ChatEventTypes.MessageUpdated;
}

/// <summary>A message was deleted; consumers treat this as a tombstone.</summary>
public sealed record MessageDeletedEvent : ChatEvent<MessageDeletedPayload>
{
    public override string Type => ChatEventTypes.MessageDeleted;
}
