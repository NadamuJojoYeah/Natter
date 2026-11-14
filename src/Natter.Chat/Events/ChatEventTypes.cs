namespace Natter.Chat;

/// <summary>NLMP v0.1 event type names (dot-separated, lowercase).</summary>
public static class ChatEventTypes
{
    public const string MessageCreated = "message.created";
    public const string MessageUpdated = "message.updated";
    public const string MessageDeleted = "message.deleted";
    public const string InteractionCreated = "interaction.created";
    public const string UpstreamStatus = "upstream.status";
}

/// <summary>Connection state reported by <c>upstream.status</c> (diagnostics only).</summary>
public enum UpstreamState
{
    Ready,
    Disconnected,
    Error,
}
