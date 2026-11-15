namespace Natter.Chat.Discord;

/// <summary>Pure Discord DTO → NLMP mapping. Transport-independent and unit-testable.</summary>
public sealed class DiscordEventMapper
{
    private readonly Source _source;
    private readonly DiscordOptions _options;

    public DiscordEventMapper(Source source, DiscordOptions options)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Message? MapMessage(DiscordMessage message, bool isEdit)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_options.IgnoreBots && message.Author.IsBot)
            return null;

        if (_options.IgnoreSelf && string.Equals(message.Author.Id, _source.SelfId, StringComparison.Ordinal))
            return null;

        if (!_options.IsGuildAllowed(message.Guild?.Id) || !_options.IsChannelAllowed(message.Channel.Id))
            return null;

        if (!_options.IsUserAllowed(message.Author.Id))
            return null;

        var conversation = MapConversation(message.Channel);
        var selfMentioned = message.MentionedUsers?.Any(user =>
            string.Equals(user.Id, _source.SelfId, StringComparison.Ordinal)) == true;

        if (_options.RequireMention
            && conversation.Kind is ConversationKind.Guild or ConversationKind.Thread
            && !selfMentioned)
        {
            // @everyone is not equivalent to a direct bot mention. It may remain a structured
            // mention for downstream policy, but it must not pass the bot-addressing gate.
            return null;
        }

        var text = CleanContent(message.Content);
        var attachments = MapAttachments(message.Attachments);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            return null;

        var mentions = MapMentions(message);

        var nlmpMessage = new Message(
            message.Id,
            conversation,
            MapUser(message.Author),
            new Content(
                text,
                ContentFormat.Markdown,
                attachments.Count == 0 ? null : attachments,
                mentions.Count == 0 ? null : mentions),
            message.CreatedAt,
            message.ReplyToMessageId,
            isEdit ? message.EditedAt ?? message.CreatedAt : message.EditedAt);

        nlmpMessage.EnsureValid();
        return nlmpMessage;
    }

    public Interaction? MapInteraction(DiscordInteraction interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (!_options.SlashCommands)
            return null;

        if (_options.IgnoreBots && interaction.User.IsBot)
            return null;

        if (_options.IgnoreSelf && string.Equals(interaction.User.Id, _source.SelfId, StringComparison.Ordinal))
            return null;

        if (!_options.IsGuildAllowed(interaction.Guild?.Id) || !_options.IsChannelAllowed(interaction.Channel.Id))
            return null;

        if (!_options.IsUserAllowed(interaction.User.Id))
            return null;

        var kind = interaction.Kind switch
        {
            "slash" => InteractionKind.Slash,
            "button" => InteractionKind.Button,
            "select" => InteractionKind.Select,
            "modal" => InteractionKind.Modal,
            _ => InteractionKind.Unknown,
        };

        return new Interaction(
            interaction.Id,
            kind,
            MapUser(interaction.User),
            MapConversation(interaction.Channel),
            interaction.CreatedAt,
            interaction.Command,
            interaction.Options,
            interaction.Values,
            interaction.ReplyToken);
    }

    public Conversation MapConversation(DiscordChannel channel)
    {
        if (channel.IsDirect)
            return new Conversation(channel.Id, ConversationKind.Direct, Title: channel.Name);

        if (channel.IsGroup)
            return new Conversation(channel.Id, ConversationKind.Group, Title: channel.Name);

        if (channel.IsThread)
            return new Conversation(channel.Id, ConversationKind.Thread, channel.ParentId, channel.GuildId, channel.Name);

        if (channel.GuildId is not null)
            return new Conversation(channel.Id, ConversationKind.Guild, GuildId: channel.GuildId, Title: channel.Name);

        return Conversation.Unknown(channel.Id);
    }

    public static User MapUser(DiscordUser user)
        => new(
            user.Id,
            user.Username,
            user.IsBot ? UserKind.Bot : UserKind.Human,
            user.DisplayName);

    public string CleanContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var cleaned = content
            .Replace($"<@{_source.SelfId}>", string.Empty, StringComparison.Ordinal)
            .Replace($"<@!{_source.SelfId}>", string.Empty, StringComparison.Ordinal)
            .Trim();

        return cleaned;
    }

    private List<Mention> MapMentions(DiscordMessage message)
    {
        var mentions = new List<Mention>();

        foreach (var user in message.MentionedUsers ?? [])
        {
            // Keep self in the structured mention list: it is the only portable signal that
            // the inbound message addressed this bot. Outbound self-mention filtering belongs
            // to DiscordChatUpstream, not to the inbound mapper.
            mentions.Add(new Mention(MentionKind.User, user.Id, user.DisplayName));
        }

        if (message.MentionEveryone)
            mentions.Add(new Mention(MentionKind.Everyone));

        return mentions;
    }

    private static List<Attachment> MapAttachments(IReadOnlyList<DiscordAttachment>? attachments)
    {
        var result = new List<Attachment>();

        foreach (var attachment in attachments ?? [])
        {
            if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            result.Add(new Attachment(
                uri.AbsoluteUri,
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.Size,
                MapAttachmentKind(attachment.ContentType, attachment.FileName),
                attachment.Width,
                attachment.Height));
        }

        return result;
    }

    private static AttachmentKind MapAttachmentKind(string? contentType, string? fileName)
    {
        var value = contentType ?? string.Empty;
        if (value.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return AttachmentKind.Image;
        if (value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return AttachmentKind.Audio;
        if (value.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return AttachmentKind.Video;
        if (value.Length > 0)
            return AttachmentKind.File;

        var extension = Path.GetExtension(fileName ?? string.Empty);
        return extension.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => AttachmentKind.Image,
            ".mp3" or ".ogg" or ".wav" or ".flac" => AttachmentKind.Audio,
            ".mp4" or ".mov" or ".webm" => AttachmentKind.Video,
            _ => AttachmentKind.File,
        };
    }
}
