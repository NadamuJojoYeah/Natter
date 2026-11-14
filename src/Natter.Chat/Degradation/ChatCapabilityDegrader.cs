using Natter.Chat.Safety;

namespace Natter.Chat;

/// <summary>
/// Applies the NLMP degradation table: capabilities that are absent change what is sent
/// instead of silently assuming support.
/// </summary>
public static class ChatCapabilityDegrader
{
    public static bool CanEdit(Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.SupportsEdit;
    }

    public static bool CanStream(Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.StreamEdit;
    }

    /// <summary>
    /// Downgrades markdown to plain text, appends attachment URLs to the text when the
    /// upstream cannot send attachments, drops oversized attachments and strips mentions
    /// when mentions are unsupported.
    /// </summary>
    public static Content DegradeContent(Content content, Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(capabilities);

        var text = content.Text ?? string.Empty;
        var format = capabilities.Markdown ? content.Format : ContentFormat.Plain;
        if (content.Format == ContentFormat.Markdown && !capabilities.Markdown)
            text = MarkdownToPlainText.Convert(text);

        var mentions = content.Mentions;
        if (!capabilities.Mentions && mentions is { Count: > 0 })
            mentions = null;

        var attachments = content.Attachments;
        if (attachments is { Count: > 0 })
        {
            var policy = new ChatAttachmentPolicy { MaxBytes = capabilities.MaxAttachmentBytes };
            var kept = new List<Attachment>(attachments.Count);
            var droppedUrls = new List<string>();

            foreach (var attachment in attachments)
            {
                var error = ChatAttachmentGuard.Validate(attachment, policy);
                if (error is null)
                {
                    kept.Add(attachment);
                }
                else if (!string.IsNullOrWhiteSpace(attachment.Url))
                {
                    droppedUrls.Add(attachment.Url);
                }
            }

            if (!capabilities.Attachments)
            {
                droppedUrls.AddRange(kept.Select(attachment => attachment.Url));
                kept.Clear();
            }

            if (droppedUrls.Count > 0)
                text = AppendUrls(text, droppedUrls);

            attachments = kept.Count == 0 ? null : kept;
        }

        return content with
        {
            Text = text,
            Format = format,
            Attachments = attachments,
            Mentions = mentions,
        };
    }

    /// <summary>Degrades a send command: content, reply, ephemeral flag and mention support.</summary>
    public static MessageSendCommand DegradeSend(MessageSendCommand command, Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);

        var payload = command.Payload;
        var content = DegradeContent(payload.Content ?? new Content(string.Empty), capabilities);

        return command with
        {
            Payload = payload with
            {
                Content = content,
                ReplyTo = capabilities.Reply ? payload.ReplyTo : null,
                Ephemeral = capabilities.Ephemeral && payload.Ephemeral,
            },
        };
    }

    public static MessageUpdateCommand DegradeUpdate(MessageUpdateCommand command, Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);

        return command with
        {
            Payload = command.Payload with
            {
                Content = DegradeContent(command.Payload.Content ?? new Content(string.Empty), capabilities),
            },
        };
    }

    public static InteractionRespondCommand DegradeRespond(InteractionRespondCommand command, Capabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capabilities);

        return command with
        {
            Payload = command.Payload with
            {
                Content = DegradeContent(command.Payload.Content ?? new Content(string.Empty), capabilities),
                Ephemeral = capabilities.Ephemeral && command.Payload.Ephemeral,
            },
        };
    }

    private static string AppendUrls(string text, IReadOnlyList<string> urls)
    {
        var lines = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (lines.Count == 0)
            return text;

        var suffix = string.Join('\n', lines);
        return string.IsNullOrEmpty(text) ? suffix : text + "\n" + suffix;
    }
}
