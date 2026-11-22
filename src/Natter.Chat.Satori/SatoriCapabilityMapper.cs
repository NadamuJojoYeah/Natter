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

    /// <summary>Reply/quote support is an adapter policy; Satori exposes no standard feature.</summary>
    public bool Reply { get; init; }

    public bool SlashCommands { get; init; }

    public bool Modals { get; init; }

    public bool Reactions { get; init; }

    public bool Ephemeral { get; init; }

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

        var conversationKinds = options.ConversationKinds is { } configuredKinds
            ? configuredKinds.Distinct().ToList()
            : DeriveConversationKinds(set);

        var streamEdit = options.StreamEdit;
        var edit = set.Contains("message.update") || streamEdit;

        // Only the features listed in NLMP §8.3 are interpreted. Everything else is ignored
        // and must be supplied explicitly through SatoriCapabilityOptions when known.
        return new Capabilities
        {
            ConversationKinds = conversationKinds,
            MaxMessageLength = options.MaxMessageLength,
            Markdown = options.Markdown,
            Attachments = set.Contains("message.attachments"),
            Mentions = options.Mentions,
            Reply = options.Reply,
            Edit = edit,
            StreamEdit = streamEdit,
            Delete = set.Contains("message.delete"),
            Typing = options.Typing,
            Reactions = options.Reactions,
            Ephemeral = options.Ephemeral,
            SlashCommands = options.SlashCommands,
            Buttons = set.Contains("message.buttons"),
            Modals = options.Modals,
            MaxAttachmentBytes = options.MaxAttachmentBytes,
            MessagesPerSecond = options.MessagesPerSecond,
        };
    }

    private static List<ConversationKind> DeriveConversationKinds(HashSet<string> features)
    {
        var kinds = new List<ConversationKind>();

        // Satori always delivers direct messages, independent of login features. Advertise it so
        // the dispatcher accepts outbound DMs; this is a known platform capability, not a guess.
        kinds.Add(ConversationKind.Direct);

        // §8.3: guild.plain adjusts conversation kinds; nothing else is assumed.
        if (features.Contains("guild.plain"))
            kinds.Add(ConversationKind.Guild);

        return kinds;
    }
}
