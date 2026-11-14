namespace Natter.Chat;

/// <summary>Mention target kind. <see cref="Everyone"/> and <see cref="Here"/> are high risk.</summary>
public enum MentionKind
{
    User,
    Role,
    Channel,
    Everyone,
    Here,
}

/// <summary>
/// Semantic mention. The <c>mentions</c> array — not the text — is the authority for
/// triggering and permission decisions.
/// </summary>
public sealed record Mention(MentionKind Kind, string? Id = null, string? Name = null);
