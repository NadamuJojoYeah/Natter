using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Natter.Chat.Safety;

namespace Natter.Chat.Satori;

/// <summary>Result of mapping Satori message elements: content plus an optional quoted reply id.</summary>
public sealed record SatoriContentMapping(Content Content, string? ReplyTo);

/// <summary>
/// Lossy v0.1 mapping from Satori XHTML-like message elements to text, attachments and
/// semantic mentions. Unknown elements keep their inner text.
/// </summary>
public static partial class SatoriContentMapper
{
    public static SatoriContentMapping Map(string? elements, ChatAttachmentPolicy? policy = null)
    {
        if (string.IsNullOrEmpty(elements))
            return new SatoriContentMapping(new Content(string.Empty), null);

        if (!TryParse(elements, out var root))
            return new SatoriContentMapping(new Content(HtmlDecodeAndStrip(elements)), null);

        var text = new StringBuilder();
        var attachments = new List<Attachment>();
        var mentions = new List<Mention>();
        string? replyTo = null;

        AppendNodes(root.Nodes());

        var normalized = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var content = new Content(
            normalized,
            ContentFormat.Plain,
            attachments.Count > 0 ? attachments : null,
            mentions.Count > 0 ? mentions : null);

        return new SatoriContentMapping(content, replyTo);

        void AppendNodes(IEnumerable<XNode> nodes)
        {
            foreach (var node in nodes)
                AppendNode(node);
        }

        void AppendNode(XNode node)
        {
            if (node is XText raw)
            {
                text.Append(raw.Value);
                return;
            }

            if (node is not XElement element)
                return;

            var name = element.Name.LocalName.ToLowerInvariant();
            switch (name)
            {
                case "at":
                {
                    var id = Attribute(element, "id");
                    var display = Attribute(element, "name") ?? id ?? "unknown";
                    var kind = Attribute(element, "type")?.ToLowerInvariant() switch
                    {
                        "all" or "everyone" => MentionKind.Everyone,
                        "here" => MentionKind.Here,
                        _ => MentionKind.User,
                    };

                    if (kind is MentionKind.Everyone or MentionKind.Here)
                    {
                        mentions.Add(new Mention(kind));
                        text.Append(kind == MentionKind.Everyone ? "@everyone" : "@here");
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            mentions.Add(new Mention(kind, id, Attribute(element, "name")));
                        text.Append('@').Append(display);
                    }

                    break;
                }

                case "sharp":
                {
                    var id = Attribute(element, "id");
                    var display = Attribute(element, "name") ?? id ?? "unknown";
                    if (!string.IsNullOrWhiteSpace(id))
                        mentions.Add(new Mention(MentionKind.Channel, id, Attribute(element, "name")));
                    text.Append('#').Append(display);
                    break;
                }

                case "quote":
                    replyTo ??= Attribute(element, "id");
                    break;

                case "img":
                case "audio":
                case "video":
                case "file":
                    AppendAttachment(element, KindFor(name), policy, attachments);
                    break;

                case "a":
                {
                    AppendNodes(element.Nodes());
                    var href = Attribute(element, "href");
                    if (!string.IsNullOrWhiteSpace(href))
                        text.Append(" (").Append(href).Append(')');
                    break;
                }

                case "br":
                    AppendNewLine(text);
                    break;

                case "p":
                    AppendNodes(element.Nodes());
                    AppendNewLine(text);
                    break;

                case "emoji":
                {
                    var emojiName = Attribute(element, "name") ?? Attribute(element, "id");
                    if (!string.IsNullOrWhiteSpace(emojiName))
                        text.Append(':').Append(emojiName).Append(':');
                    break;
                }

                default:
                    AppendNodes(element.Nodes());
                    break;
            }
        }
    }

    private static void AppendAttachment(
        XElement element,
        AttachmentKind kind,
        ChatAttachmentPolicy? policy,
        List<Attachment> attachments)
    {
        var url = Attribute(element, "src");
        if (string.IsNullOrWhiteSpace(url))
            return;

        var attachment = new Attachment(
            url,
            Id: Attribute(element, "id"),
            FileName: Attribute(element, "title"),
            MimeType: Attribute(element, "mime") ?? Attribute(element, "mime_type"),
            Size: LongAttribute(element, "size"),
            Kind: kind,
            Width: IntAttribute(element, "width"),
            Height: IntAttribute(element, "height"));

        if (ChatAttachmentGuard.Validate(attachment, policy) is null)
            attachments.Add(attachment);
    }

    private static void AppendNewLine(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.Append('\n');
    }

    private static AttachmentKind KindFor(string tag) => tag switch
    {
        "img" => AttachmentKind.Image,
        "audio" => AttachmentKind.Audio,
        "video" => AttachmentKind.Video,
        "file" => AttachmentKind.File,
        _ => AttachmentKind.Unknown,
    };

    private static string? Attribute(XElement element, string name)
        => element.Attribute(name)?.Value;

    private static long? LongAttribute(XElement element, string name)
    {
        var value = Attribute(element, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? IntAttribute(XElement element, string name)
    {
        var value = Attribute(element, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryParse(string elements, out XElement root)
    {
        try
        {
            var document = XDocument.Parse($"<root>{elements}</root>", LoadOptions.PreserveWhitespace);
            root = document.Root!;
            return true;
        }
        catch (XmlException)
        {
            // A malformed fragment must not take the adapter down: fall back to text.
            root = null!;
            return false;
        }
    }

    private static string HtmlDecodeAndStrip(string value)
        => WebUtility.HtmlDecode(TagPattern().Replace(value, string.Empty)).Trim();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
