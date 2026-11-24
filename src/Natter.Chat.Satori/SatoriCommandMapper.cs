using System.Text;
using System.Text.Json;
using Natter.Chat.Safety;

namespace Natter.Chat.Satori;

/// <summary>Well-known Satori v1 API routes used by the adapter.</summary>
public static class SatoriApiContract
{
    public const string MessageCreate = "/v1/message.create";

    public const string MessageUpdate = "/v1/message.update";

    public const string MessageDelete = "/v1/message.delete";

    public static IReadOnlyDictionary<string, string> CommandRoutes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChatCommandTypes.MessageSend] = MessageCreate,
            [ChatCommandTypes.MessageUpdate] = MessageUpdate,
            [ChatCommandTypes.MessageDelete] = MessageDelete,
        };
}

/// <summary>One Satori HTTP API request produced from an NLMP command.</summary>
public sealed record SatoriApiRequest(
    string Method,
    string Route,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement Body);

/// <summary>
/// Maps NLMP commands to Satori HTTP requests. The request headers are derived from the
/// structured source; tokens never enter NLMP and are added by the transport.
/// </summary>
public sealed class SatoriCommandMapper
{
    public bool TryMap(ChatCommand? command, out SatoriApiRequest? request)
        => TryMap(command, policy: null, out request, out _);

    /// <summary>
    /// Maps a command and (when <paramref name="policy"/> is supplied) validates the mention policy
    /// against the mapped elements before producing the request. Text is always escaped, so a text
    /// that merely looks like an element can never become one.
    /// </summary>
    public bool TryMap(
        ChatCommand? command,
        ChatOutboundPolicy? policy,
        out SatoriApiRequest? request,
        out ChatError? error)
    {
        request = null;
        error = null;
        if (command is null)
            return false;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Satori-Platform"] = command.Source.Platform,
            ["Satori-User-ID"] = command.Source.SelfId,
        };

        switch (command)
        {
            case MessageSendCommand send:
            {
                if (!ValidateMentions(send.Payload.Content, policy, out error))
                    return false;

                var body = Body(
                    ("channel_id", send.Payload.Conversation.Id),
                    ("guild_id", send.Payload.Conversation.GuildId),
                    ("content", SatoriOutboundContent.Format(send.Payload.Content)),
                    ("reply_to", send.Payload.ReplyTo));
                request = new SatoriApiRequest("POST", SatoriApiContract.MessageCreate, headers, body);
                return true;
            }

            case MessageUpdateCommand update:
            {
                if (!ValidateMentions(update.Payload.Content, policy, out error))
                    return false;

                var body = Body(
                    ("message_id", update.Payload.MessageId),
                    ("content", SatoriOutboundContent.Format(update.Payload.Content)));
                request = new SatoriApiRequest("POST", SatoriApiContract.MessageUpdate, headers, body);
                return true;
            }

            case MessageDeleteCommand delete:
            {
                var body = Body(("message_id", delete.Payload.MessageId));
                request = new SatoriApiRequest("POST", SatoriApiContract.MessageDelete, headers, body);
                return true;
            }

            default:
                return false;
        }
    }

    public SatoriApiRequest Map(ChatCommand command)
        => TryMap(command, out var request)
            ? request!
            : throw new NotSupportedException($"Satori adapter cannot map command type '{command.Type}'.");

    private static bool ValidateMentions(Content content, ChatOutboundPolicy? policy, out ChatError? error)
    {
        error = policy is null
            ? null
            : SatoriOutboundContent.ValidateMentions(SatoriOutboundContent.Build(content), policy);
        return error is null;
    }

    private static JsonElement Body(params (string Key, object? Value)[] items)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            if (value is not null)
                values[key] = value;
        }

        return JsonSerializer.SerializeToElement(values, SatoriJson.Options);
    }
}

/// <summary>
/// One outbound Satori element. Text and structural elements are separate nodes: text is always
/// escaped when serialized, so it can never become an <c>&lt;at&gt;</c>, <c>&lt;img&gt;</c> or other
/// platform element by accident.
/// </summary>
public abstract record SatoriOutboundElement;

public sealed record SatoriTextElement(string Text) : SatoriOutboundElement;

public sealed record SatoriMentionElement(Mention Mention) : SatoriOutboundElement;

public sealed record SatoriAttachmentElement(string Tag, string Url) : SatoriOutboundElement;

/// <summary>
/// Builds outbound Satori elements from NLMP content. Structured mentions and attachments become
/// elements; the remaining text is a text node and is XML-escaped only at serialization time.
/// </summary>
public static class SatoriOutboundContent
{
    public static IReadOnlyList<SatoriOutboundElement> Build(Content content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var elements = new List<SatoriOutboundElement>();
        if (!string.IsNullOrEmpty(content.Text))
            elements.Add(new SatoriTextElement(content.Text));

        foreach (var mention in content.Mentions ?? [])
        {
            if (mention is not null)
                elements.Add(new SatoriMentionElement(mention));
        }

        foreach (var attachment in content.Attachments ?? [])
        {
            if (attachment is null || !ChatAttachmentGuard.IsAllowedUrl(attachment.Url))
                continue;

            elements.Add(new SatoriAttachmentElement(TagFor(attachment.Kind), attachment.Url));
        }

        return elements;
    }

    /// <summary>Serializes elements with XML escaping; plain text without special characters is unchanged.</summary>
    public static string Format(Content content) => Serialize(Build(content));

    /// <summary>
    /// Mention policy check over the <b>mapped elements</b>, not just <see cref="Content.Mentions"/>.
    /// This is the adapter-side defence in depth for callers that bypass the dispatcher.
    /// </summary>
    public static ChatError? ValidateMentions(
        IReadOnlyList<SatoriOutboundElement> elements,
        ChatOutboundPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var mentions = elements
            .OfType<SatoriMentionElement>()
            .Select(element => element.Mention)
            .Where(mention => mention is not null)
            .ToArray();

        return mentions.Length == 0
            ? null
            : ChatMentionGuard.Validate(new Content(string.Empty, Mentions: mentions), policy);
    }

    private static string Serialize(IReadOnlyList<SatoriOutboundElement> elements)
    {
        var builder = new StringBuilder();
        foreach (var element in elements)
        {
            switch (element)
            {
                case SatoriTextElement text:
                    AppendText(builder, text.Text);
                    break;
                case SatoriMentionElement mention:
                    AppendMention(builder, mention.Mention);
                    break;
                case SatoriAttachmentElement attachment:
                    if (builder.Length > 0 && builder[^1] != '\n')
                        builder.Append('\n');
                    builder.Append('<').Append(attachment.Tag)
                        .Append(" src=\"").Append(EscapeAttribute(attachment.Url)).Append("\"")
                        .Append("/>");
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AppendMention(StringBuilder builder, Mention mention)
    {
        switch (mention.Kind)
        {
            case MentionKind.Everyone:
                builder.Append("<at type=\"all\"/>");
                return;

            case MentionKind.Here:
                builder.Append("<at type=\"here\"/>");
                return;

            case MentionKind.Channel:
            {
                if (string.IsNullOrWhiteSpace(mention.Id) && string.IsNullOrWhiteSpace(mention.Name))
                    return;

                builder.Append("<sharp");
                AppendAttribute(builder, "id", mention.Id);
                AppendAttribute(builder, "name", mention.Name);
                builder.Append("/>");
                return;
            }

            case MentionKind.Role:
            case MentionKind.User:
            {
                if (string.IsNullOrWhiteSpace(mention.Id) && string.IsNullOrWhiteSpace(mention.Name))
                    return;

                builder.Append("<at");
                AppendAttribute(builder, "id", mention.Id);
                if (mention.Kind is MentionKind.Role)
                    builder.Append(" type=\"role\"");
                AppendAttribute(builder, "name", mention.Name);
                builder.Append("/>");
                return;
            }
        }
    }

    private static void AppendAttribute(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder.Append(' ').Append(name).Append("=\"").Append(EscapeAttribute(value)).Append('"');
    }

    private static void AppendText(StringBuilder builder, string value)
    {
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
    }

    private static string TagFor(AttachmentKind? kind) => kind switch
    {
        AttachmentKind.Image => "img",
        AttachmentKind.Audio => "audio",
        AttachmentKind.Video => "video",
        _ => "file",
    };

    private static string EscapeAttribute(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
