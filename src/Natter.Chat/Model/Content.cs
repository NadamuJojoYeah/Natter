using System.Text.Json.Serialization;
using Natter.Chat.Safety;

namespace Natter.Chat;

/// <summary>Text format guarantee. Markdown is only usable when the capability is present.</summary>
public enum ContentFormat
{
    Plain,
    Markdown,
}

/// <summary>
/// Text-first message content. Rich text element trees are deliberately outside v0.1.
/// </summary>
public sealed record Content(
    string Text,
    ContentFormat Format = ContentFormat.Plain,
    IReadOnlyList<Attachment>? Attachments = null,
    IReadOnlyList<Mention>? Mentions = null)
{
    [JsonIgnore]
    public bool HasAttachments => Attachments is { Count: > 0 };

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Text) && !HasAttachments;

    /// <summary>Returns every model violation; empty means the content is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Text is null)
            errors.Add("content.text must not be null.");

        if (string.IsNullOrEmpty(Text) && !HasAttachments)
            errors.Add("content must have non-empty text or at least one attachment.");

        if (Attachments is not null)
        {
            for (var index = 0; index < Attachments.Count; index++)
            {
                var error = ChatAttachmentGuard.Validate(Attachments[index]);
                if (error is not null)
                    errors.Add($"content.attachments[{index}]: {error}");
            }
        }

        if (Mentions is not null)
        {
            for (var index = 0; index < Mentions.Count; index++)
            {
                var mention = Mentions[index];
                if (mention is null)
                {
                    errors.Add($"content.mentions[{index}] must not be null.");
                    continue;
                }

                if (mention.Kind is MentionKind.User or MentionKind.Role or MentionKind.Channel
                    && string.IsNullOrWhiteSpace(mention.Id))
                {
                    errors.Add($"content.mentions[{index}].id is required for kind '{mention.Kind}'.");
                }
            }
        }

        return errors;
    }

    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new NlmpValidationException("Message content violates the NLMP model.", errors);
    }
}
