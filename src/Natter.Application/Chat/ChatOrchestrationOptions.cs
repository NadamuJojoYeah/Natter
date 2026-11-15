namespace Natter.Application.Chat;

/// <summary>Policy for which inbound NLMP messages reach a responder.</summary>
public sealed record ChatOrchestrationOptions
{
    public bool ProcessBotMessages { get; init; }

    public bool ProcessSystemMessages { get; init; }

    public bool ProcessEditedMessages { get; init; } = false;

    /// <summary>Only direct chats or messages that mention the bot trigger a reply.</summary>
    public bool RequireMentionOrDirect { get; init; } = true;

    public bool IgnoreSelfMessages { get; init; } = true;
}
