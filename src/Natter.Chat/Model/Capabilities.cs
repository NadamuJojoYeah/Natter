using System.Text.Json.Serialization;

namespace Natter.Chat;

/// <summary>
/// What a <c>(source, conversation?)</c> supports. Missing booleans mean unknown/unsupported:
/// features must degrade instead of assuming support.
/// </summary>
public sealed record Capabilities
{
    public IReadOnlyList<ConversationKind> ConversationKinds { get; init; } = [];

    public int? MaxMessageLength { get; init; }

    public bool Markdown { get; init; }

    public bool Attachments { get; init; }

    public bool Mentions { get; init; }

    public bool Reply { get; init; }

    public bool Edit { get; init; }

    /// <summary>High-frequency edit support. Implies <see cref="Edit"/>.</summary>
    public bool StreamEdit { get; init; }

    public bool Delete { get; init; }

    public bool Typing { get; init; }

    public bool Reactions { get; init; }

    public bool Ephemeral { get; init; }

    public bool SlashCommands { get; init; }

    public bool Buttons { get; init; }

    public bool Modals { get; init; }

    public long? MaxAttachmentBytes { get; init; }

    public double? MessagesPerSecond { get; init; }

    /// <summary><c>streamEdit=true</c> implies edit support per the specification.</summary>
    [JsonIgnore]
    public bool SupportsEdit => Edit || StreamEdit;

    [JsonIgnore]
    public bool SupportsStreaming => StreamEdit;

    public bool SupportsConversation(ConversationKind kind)
        => ConversationKinds.Contains(kind) || ConversationKinds.Contains(ConversationKind.Unknown);

    public static Capabilities TextOnly { get; } = new()
    {
        ConversationKinds =
        [
            ConversationKind.Direct,
            ConversationKind.Group,
            ConversationKind.Guild,
            ConversationKind.Thread,
        ],
        Mentions = true,
        Reply = true,
    };
}
