using System.Text.Json.Serialization;

namespace Natter.Chat;

/// <summary>
/// What a <c>(source, conversation?)</c> supports. Missing booleans mean unknown/unsupported:
/// features must degrade instead of assuming support.
/// </summary>
public sealed record Capabilities
{
    public IReadOnlyList<ConversationKind> ConversationKinds { get; init; } = [];

    /// <summary>
    /// Outbound text bound in UTF-16 code units, or <see langword="null"/> when the real limit is
    /// unknown (an aggregation protocol whose platform varies) or chunking is deliberately disabled.
    /// <para>
    /// <b>Declaring a value creates an obligation:</b> the adapter must split outbound content at this
    /// bound. <see langword="null"/> waives that obligation, and the waiver has to be observable
    /// (log/health) — see docs/CONTRIBUTION_OBLIGATIONS.md §3.1/§3.2.
    /// </para>
    /// <para>
    /// Note the direction of failure: <c>ChatTextChunker.Chunk</c> treats an absent bound as "do not
    /// split", so a forgotten value fails <i>open</i> — the platform then rejects the whole message.
    /// </para>
    /// </summary>
    public int? MaxMessageLength { get; init; }

    public bool Markdown { get; init; }

    public bool Attachments { get; init; }

    public bool Mentions { get; init; }

    public bool Reply { get; init; }

    private readonly bool _edit;

    /// <summary>Message editing. <c>streamEdit=true</c> implies this is true.</summary>
    public bool Edit
    {
        get => _edit || StreamEdit;
        init => _edit = value;
    }

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
