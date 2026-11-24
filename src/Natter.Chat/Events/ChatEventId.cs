using System.Globalization;

namespace Natter.Chat;

/// <summary>Deterministic event ids for adapters that do not receive a platform event id.</summary>
public static class ChatEventId
{
    /// <summary>
    /// Derives an event id from the source and sequence (e.g. Satori <c>sn</c>), or from a
    /// random suffix when no sequence exists. The result is opaque to consumers.
    /// </summary>
    /// <summary>
    /// Stable id for one edit revision: distinct edits (different edited timestamp) get distinct
    /// ids, while a replay of the same edit (same timestamp) deduplicates. S2-A13.
    /// </summary>
    public static string DeriveEdit(Source source, string messageId, DateTimeOffset editedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"evt:{source.Protocol}:{source.Platform}:{source.SelfId}:edit:{messageId}:{editedAt.ToUnixTimeMilliseconds()}");
    }

    public static string Derive(Source source, string type, long? sequence = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        return sequence is { } sn
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"evt:{source.Protocol}:{source.Platform}:{source.SelfId}:{sn}")
            : $"evt:{source.Protocol}:{source.Platform}:{source.SelfId}:{type}:{Guid.NewGuid():N}";
    }
}
