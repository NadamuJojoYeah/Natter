namespace Natter.Application.Chat;

/// <summary>
/// One conversation as seen by a read-only consumer (the Conversation Console plugin). It is a
/// projection of host-owned data, never a second source of truth: no writer can be built from it.
/// <para>
/// Design §2.2 calls this type <c>ConversationSummary</c>; that name is already taken by the
/// summary-store record in this namespace (id/text/message-range of a rolled-up summary), so the
/// projection ships as <c>ConversationOverview</c> — the shape is unchanged.
/// </para>
/// </summary>
/// <param name="SessionId">Opaque host session id (also the <c>GetMessagesAsync</c> key).</param>
/// <param name="SourceKey">Structured source key, for example <c>discord:discord:123</c>.</param>
/// <param name="Protocol">Protocol part of the source key (v4 structured column).</param>
/// <param name="Platform">Platform part of the source key (v4 structured column).</param>
/// <param name="ConversationId">Platform conversation (channel/DM) id.</param>
/// <param name="GuildId">Guild/space id when the platform has one.</param>
/// <param name="ActorId">Actor (user) id when the conversation is actor-scoped.</param>
/// <param name="MessageCount">Number of stored messages, not the number of turns.</param>
/// <param name="FirstMessageAt">Timestamp of the oldest stored message (session creation time if empty).</param>
/// <param name="LastMessageAt">Timestamp of the newest stored message (last activity if empty).</param>
public sealed record ConversationOverview(
    string SessionId,
    string SourceKey,
    string Protocol,
    string Platform,
    string ConversationId,
    string? GuildId,
    string? ActorId,
    int MessageCount,
    DateTimeOffset FirstMessageAt,
    DateTimeOffset LastMessageAt);

/// <summary>
/// Read filter for <see cref="IConversationQuery.ListAsync"/>. All members are optional except the
/// page size, which the implementation clamps to <see cref="ConversationQueryLimits.MaxPageSize"/>.
/// </summary>
/// <param name="SourceKey">Exact source key match, for example <c>discord:discord:123</c>.</param>
/// <param name="ActorId">Exact actor id match.</param>
/// <param name="Since">Only conversations whose last activity is at or after this instant.</param>
/// <param name="Until">Only conversations whose last activity is at or before this instant.</param>
/// <param name="Query">Case-insensitive substring match against message text (P1 uses LIKE).</param>
/// <param name="Limit">Requested page size; clamped to [1, <see cref="ConversationQueryLimits.MaxPageSize"/>].</param>
/// <param name="Cursor">Opaque <see cref="ConversationPage.NextCursor"/> value; null starts at page one.</param>
public sealed record ConversationFilter(
    string? SourceKey = null,
    string? ActorId = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    string? Query = null,
    int Limit = ConversationQueryLimits.DefaultPageSize,
    string? Cursor = null);

/// <summary>One page of conversation summaries plus the opaque cursor for the next page.</summary>
/// <param name="Items">Summaries ordered by last activity, newest first.</param>
/// <param name="NextCursor">Null when there is no further page.</param>
public sealed record ConversationPage(
    IReadOnlyList<ConversationOverview> Items,
    string? NextCursor);

/// <summary>
/// Host surface limits. Kept in one place so the port, the SQLite implementation and the plugin
/// cannot drift apart (the plugin additionally clamps user input before calling the port).
/// </summary>
public static class ConversationQueryLimits
{
    /// <summary>Hard page-size ceiling for <see cref="IConversationQuery.ListAsync"/>.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Page size used when a caller passes 0, a negative value or omits the limit.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Hard page-size ceiling for <see cref="IConversationQuery.GetMessagesAsync"/>.</summary>
    public const int MaxMessagePageSize = 200;

    /// <summary>Message page size used when a caller passes 0, a negative value or omits the limit.</summary>
    public const int DefaultMessagePageSize = 50;

    /// <summary>Longest accepted content search term; longer input is truncated before matching.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Longest accepted source key / actor id filter value.</summary>
    public const int MaxFilterValueLength = 512;

    /// <summary>Clamps a requested conversation page size into [1, <see cref="MaxPageSize"/>].</summary>
    public static int ClampPageSize(int limit)
        => limit <= 0
            ? DefaultPageSize
            : Math.Min(limit, MaxPageSize);

    /// <summary>Clamps a requested message page size into [0, <see cref="MaxMessagePageSize"/>].</summary>
    /// <remarks>Zero is meaningful here: it asks for "no messages" and is used by probing clients.</remarks>
    public static int ClampMessagePageSize(int limit)
        => limit < 0
            ? 0
            : Math.Min(limit, MaxMessagePageSize);
}

/// <summary>
/// Read-only conversation query port (design §2.2). Deliberately exposes **two read methods and
/// nothing else**: there is no append/clear/delete overload, so a consumer cannot express a write
/// even by mistake. The host implementation owns the schema and the SQL; plugins never see
/// <c>Microsoft.Data.Sqlite</c>.
/// </summary>
public interface IConversationQuery
{
    /// <summary>
    /// False when the port is structurally incapable of returning data (for example the in-memory
    /// host without --state-dir). Health checks must not report this as "available".
    /// </summary>
    bool HasDataSource => true;

    /// <summary>Lists conversations newest-first, with cursor pagination and the filters above.</summary>
    ValueTask<ConversationPage> ListAsync(
        ConversationFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns messages of one session in ascending <see cref="StoredMessage.MessageId"/> order,
    /// strictly after <paramref name="afterMessageId"/>. Sessions never bleed into each other.
    /// </summary>
    ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same query, paged by the composite <c>(createdAt, messageId)</c> cursor (task-72 ⑤). A single
    /// timestamp is not unique — several messages can share a second — so an id-only or time-only
    /// cursor can skip or repeat rows. The default implementation falls back to the id cursor, which
    /// keeps existing hosts compiling; SQLite hosts override it with the keyset query.
    /// </summary>
    ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId,
        MessageCursor cursor,
        int limit,
        CancellationToken cancellationToken = default)
        => GetMessagesAsync(sessionId, cursor.MessageId, limit, cancellationToken);
}

/// <summary>
/// Keyset cursor for message pagination: the last row's <c>created_at</c> plus its numeric id, base64url
/// encoded so clients treat it as an opaque token.
/// </summary>
public sealed record MessageCursor(DateTimeOffset CreatedAt, long MessageId)
{
    public static MessageCursor Start { get; } = new(DateTimeOffset.MinValue, 0);

    public string Encode()
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + "\n"
                + MessageId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Decodes a cursor; a forged or stale value returns null so the caller starts over.</summary>
    public static MessageCursor? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separator = payload.LastIndexOf('\n');
            if (separator <= 0)
                return null;

            if (!DateTimeOffset.TryParse(
                    payload[..separator],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var createdAt))
            {
                return null;
            }

            return long.TryParse(
                payload[(separator + 1)..],
                System.Globalization.CultureInfo.InvariantCulture,
                out var messageId)
                ? new MessageCursor(createdAt, messageId)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Query surface for hosts that keep conversations in memory only (no <c>--state-dir</c>). The
/// in-memory store has no enumerable history by design, so the port answers empty pages instead of
/// pretending there is data. A console installed against such a host reports "no data" in its UI.
/// </summary>
public sealed class EmptyConversationQuery : IConversationQuery
{
    public bool HasDataSource => false;

    public ValueTask<ConversationPage> ListAsync(
        ConversationFilter filter,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ConversationPage([], null));

    public ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StoredMessage>>([]);
}
