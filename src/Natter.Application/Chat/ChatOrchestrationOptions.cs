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

    /// <summary>Bounded queue capacity for inbound events (S1-06).</summary>
    public int QueueCapacity { get; init; } = 64;

    /// <summary>Fixed worker count; different conversations run in parallel up to this limit.</summary>
    public int WorkerCount { get; init; } = 2;

    /// <summary>Maximum time StopAsync waits for in-flight events before cancelling them.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
