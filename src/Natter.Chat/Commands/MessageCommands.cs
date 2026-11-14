namespace Natter.Chat;

/// <summary>Send a new message (possibly as a reply, possibly ephemeral).</summary>
public sealed record MessageSendCommand : ChatCommand<MessageSendPayload>
{
    public override string Type => ChatCommandTypes.MessageSend;
}

/// <summary>Replace the content of an existing message.</summary>
public sealed record MessageUpdateCommand : ChatCommand<MessageUpdatePayload>
{
    public override string Type => ChatCommandTypes.MessageUpdate;
}

/// <summary>Delete an existing message.</summary>
public sealed record MessageDeleteCommand : ChatCommand<MessageDeletePayload>
{
    public override string Type => ChatCommandTypes.MessageDelete;
}

/// <summary>Signal that the bot is typing in a conversation.</summary>
public sealed record TypingStartCommand : ChatCommand<TypingStartPayload>
{
    public override string Type => ChatCommandTypes.TypingStart;
}

/// <summary>Respond to a slash command / button / selector / modal interaction.</summary>
public sealed record InteractionRespondCommand : ChatCommand<InteractionRespondPayload>
{
    public override string Type => ChatCommandTypes.InteractionRespond;
}
