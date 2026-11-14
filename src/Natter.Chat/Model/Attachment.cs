namespace Natter.Chat;

/// <summary>Attachment media kind.</summary>
public enum AttachmentKind
{
    Image,
    Audio,
    Video,
    File,
    Unknown,
}

/// <summary>
/// Attachment descriptor. v0.1 accepts only absolute http(s) URLs; base64 and local
/// paths are rejected by <see cref="Safety.ChatAttachmentGuard"/>.
/// </summary>
public sealed record Attachment(
    string Url,
    string? Id = null,
    string? FileName = null,
    string? MimeType = null,
    long? Size = null,
    AttachmentKind? Kind = null,
    int? Width = null,
    int? Height = null,
    DateTimeOffset? ExpiresAt = null);
