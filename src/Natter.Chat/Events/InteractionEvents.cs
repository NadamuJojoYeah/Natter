namespace Natter.Chat;

/// <summary>A slash command, button, selector or modal interaction was triggered.</summary>
public sealed record InteractionCreatedEvent : ChatEvent<InteractionPayload>
{
    public override string Type => ChatEventTypes.InteractionCreated;
}

/// <summary>Connection state changed. Used for diagnostics/health checks only.</summary>
public sealed record UpstreamStatusEvent : ChatEvent<UpstreamStatusPayload>
{
    public override string Type => ChatEventTypes.UpstreamStatus;
}
