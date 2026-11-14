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
    {
        request = null;
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

/// <summary>Formats outbound content as Satori message elements (text + attachment tags).</summary>
public static class SatoriOutboundContent
{
    public static string Format(Content content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = content.Text ?? string.Empty;
        if (content.Attachments is not { Count: > 0 } attachments)
            return text;

        var builder = new StringBuilder(text);

        foreach (var attachment in attachments)
        {
            if (!ChatAttachmentGuard.IsAllowedUrl(attachment.Url))
                continue;

            if (builder.Length > 0 && builder[^1] != '\n')
                builder.Append('\n');

            var tag = attachment.Kind switch
            {
                AttachmentKind.Image => "img",
                AttachmentKind.Audio => "audio",
                AttachmentKind.Video => "video",
                _ => "file",
            };

            builder.Append('<').Append(tag)
                .Append(" src=\"").Append(EscapeAttribute(attachment.Url)).Append("\"")
                .Append("/>");
        }

        return builder.ToString();
    }

    private static string EscapeAttribute(string value)
        => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
