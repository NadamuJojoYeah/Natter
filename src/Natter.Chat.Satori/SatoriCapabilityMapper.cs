namespace Natter.Chat.Satori;

/// <summary>Host-provided knowledge that Satori cannot discover from <c>login.features</c>.</summary>
public sealed record SatoriCapabilityOptions
{
    /// <summary>High-frequency edit support is a platform/adapter policy decision.</summary>
    public bool StreamEdit { get; init; }

    public bool Typing { get; init; }

    /// <summary>Satori makes no Markdown rendering guarantee; keep false unless the bot converts.</summary>
    public bool Markdown { get; init; }

    public bool Mentions { get; init; }

    /// <summary>Overrides conversation kinds entirely; null derives them from features.</summary>
    public IReadOnlyList<ConversationKind>? ConversationKinds { get; init; }

    public int? MaxMessageLength { get; init; }

    public long? MaxAttachmentBytes { get; init; }

    public double? MessagesPerSecond { get; init; }
}

/// <summary>
/// Maps Satori <c>login.features</c> to NLMP capabilities. Unknown features are ignored and
/// absent features are never assumed to exist.
/// </summary>
public static class SatoriCapabilityMapper
{
    public static Capabilities Map(IEnumerable<string>? features, SatoriCapabilityOptions? options = null)
    {
        options ??= new SatoriCapabilityOptions();

        var set = new HashSet<string>(
            (features ?? []).Where(feature => !string.IsNullOrWhiteSpace(feature)),
            StringComparer.OrdinalIgnoreCase);

        var conversationKinds = options.ConversationKinds is { Count: > 0 }
            ? options.ConversationKinds.Distinct().ToList()
            : DeriveConversationKinds(set);

        var streamEdit = options.StreamEdit;
        var edit = set.Contains("message.update") || streamEdit;

        return new Capabilities
        {
            ConversationKinds = conversationKinds,
            MaxMessageLength = options.MaxMessageLength,
            Markdown = options.Markdown,
            Attachments = set.Contains("message.attachments"),
            Mentions = options.Mentions,
            Reply = set.Contains("message.reply"),
            Edit = edit,
            StreamEdit = streamEdit,
            Delete = set.Contains("message.delete"),
            Typing = options.Typing,
            Reactions = set.Contains("message.reactions"),
            Ephemeral = set.Contains("message.ephemeral"),
            SlashCommands = set.Any(feature => feature.StartsWith("interaction", StringComparison.OrdinalIgnoreCase)),
            Buttons = set.Contains("message.buttons") || set.Contains("interaction.button"),
            Modals = set.Contains("interaction.modal"),
            MaxAttachmentBytes = options.MaxAttachmentBytes,
            MessagesPerSecond = options.MessagesPerSecond,
        };
    }

    private static List<ConversationKind> DeriveConversationKinds(HashSet<string> features)
    {
        var kinds = new List<ConversationKind> { ConversationKind.Direct };

        if (features.Contains("guild.plain") || features.Contains("guild.get"))
            kinds.Add(ConversationKind.Guild);

        if (features.Contains("group.plain"))
            kinds.Add(ConversationKind.Group);

        return kinds;
    }
}
