using System.Text.Json.Serialization;

namespace Natter.Chat;

/// <summary>
/// Identifies one bot account attached through one protocol adaptor: the
/// <c>(protocol, platform, selfId)</c> triple. All platform ids are opaque strings.
/// </summary>
public sealed record Source(string Protocol, string Platform, string SelfId)
{
    /// <summary>
    /// Readable identifier for logs and diagnostics. Never parse it back: routing,
    /// storage and deduplication must use the structured fields.
    /// </summary>
    [JsonIgnore]
    public string Key => $"{Protocol}:{Platform}:{SelfId}";

    /// <summary>Creates a validated source; all three fields MUST be non-empty.</summary>
    public static Source Create(string protocol, string platform, string selfId)
    {
        NlmpGuard.NotNullOrWhiteSpace(protocol, nameof(protocol));
        NlmpGuard.NotNullOrWhiteSpace(platform, nameof(platform));
        NlmpGuard.NotNullOrWhiteSpace(selfId, nameof(selfId));

        return new Source(protocol.Trim(), platform.Trim(), selfId.Trim());
    }

    public override string ToString() => Key;
}
