using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Natter.Application.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Read-only <see cref="IConversationQuery"/> over the shared <c>natter.db</c>.
/// <para>
/// Three invariants shape this class:
/// </para>
/// <list type="number">
/// <item>
/// <b>No DDL.</b> <see cref="SqliteSchema"/> stays the only migration owner; this store only opens
/// connections through <see cref="SqliteSchema.Prepare"/> and reads the v4 shape (structured
/// <c>protocol</c>/<c>platform</c> columns).
/// </item>
/// <item>
/// <b>Separate read connection.</b> The console must not share the host writer's connection: reads
/// open <see cref="SqliteOpenMode.ReadOnly"/> connections, keep the shared <c>busy_timeout</c>
/// (5000 ms, the <see cref="SqliteTaskStore"/> precedent) and additionally set
/// <c>PRAGMA query_only = ON</c> so a future refactor cannot accidentally write here.
/// </item>
/// <item>
/// <b>Bounded results.</b> Every call clamps its page size to
/// <see cref="ConversationQueryLimits.MaxPageSize"/> and paginates by keyset cursor, so a caller
/// can never pull the whole table into memory.
/// </item>
/// </list>
/// </summary>
public sealed class SqliteConversationQuery : IConversationQuery
{
    private const int BusyTimeoutMilliseconds = 5000;

    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteConversationQuery(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            // The host process may be writing; a reader must never upgrade the file.
            Cache = SqliteCacheMode.Private,
        }.ToString();
    }

    /// <summary>Absolute path of the database file this query reads.</summary>
    public string DatabasePath => _databasePath;

    public async ValueTask<ConversationPage> ListAsync(
        ConversationFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var limit = ConversationQueryLimits.ClampPageSize(filter.Limit);
        var cursor = ConversationCursor.TryDecode(filter.Cursor);

        if (!File.Exists(_databasePath))
            return new ConversationPage([], null);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var sourceKey = NormalizeFilterValue(filter.SourceKey, ConversationQueryLimits.MaxFilterValueLength);
        var actorId = NormalizeFilterValue(filter.ActorId, ConversationQueryLimits.MaxFilterValueLength);
        var search = NormalizeSearchTerm(filter.Query);

        // Content search is expressed with EXISTS over the same session_id: a session is included
        // because *its own* rows match, so a hit in another conversation can never surface it.
        //
        // Ordering, window filters and the cursor all compare the raw `updated_at` text: every host
        // writer stores an ISO-8601 UTC round-trip string ("O"), which is fixed-width and therefore
        // sorts chronologically while keeping sub-second precision. Filter instants are normalised
        // to the same UTC format below so the comparison is apples-to-apples.
        command.CommandText = """
            SELECT
                s.session_id,
                s.source_key,
                s.protocol,
                s.platform,
                s.conversation_id,
                s.guild_id,
                s.actor_id,
                (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.session_id),
                COALESCE((SELECT MIN(m.created_at) FROM messages m WHERE m.session_id = s.session_id), s.created_at),
                COALESCE((SELECT MAX(m.created_at) FROM messages m WHERE m.session_id = s.session_id), s.updated_at),
                s.updated_at
            FROM sessions s
            WHERE ($source IS NULL OR s.source_key = $source)
              AND ($actor IS NULL OR s.actor_id = $actor)
              AND ($since IS NULL OR s.updated_at >= $since)
              AND ($until IS NULL OR s.updated_at <= $until)
              AND ($pattern IS NULL OR EXISTS (
                    SELECT 1 FROM messages mq
                    WHERE mq.session_id = s.session_id AND mq.text LIKE $pattern ESCAPE '\'))
              AND ($cursorAt IS NULL
                   OR s.updated_at < $cursorAt
                   OR (s.updated_at = $cursorAt AND s.session_id < $cursorId))
            ORDER BY s.updated_at DESC, s.session_id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$source", (object?)sourceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)actorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$since", (object?)NormalizeInstant(filter.Since) ?? DBNull.Value);
        command.Parameters.AddWithValue("$until", (object?)NormalizeInstant(filter.Until) ?? DBNull.Value);
        command.Parameters.AddWithValue("$pattern", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("$cursorAt", (object?)cursor?.UpdatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$cursorId", (object?)cursor?.SessionId ?? DBNull.Value);
        // One extra row tells us whether another page exists without a second COUNT query.
        command.Parameters.AddWithValue("$limit", limit + 1);

        var items = new List<ConversationOverview>(Math.Min(limit, 32));
        string? lastUpdatedAt = null;
        string? lastSessionId = null;
        var hasMore = false;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (items.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                items.Add(new ConversationOverview(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt32(7),
                    ParseTimestamp(reader.GetString(8)),
                    ParseTimestamp(reader.GetString(9))));

                lastUpdatedAt = reader.GetString(10);
                lastSessionId = reader.GetString(0);
            }
        }

        var nextCursor = hasMore && lastUpdatedAt is not null && lastSessionId is not null
            ? ConversationCursor.Encode(lastUpdatedAt, lastSessionId)
            : null;

        return new ConversationPage(items, nextCursor);
    }

    public async ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var clamped = ConversationQueryLimits.ClampMessagePageSize(limit);
        if (clamped == 0)
            return [];

        if (!File.Exists(_databasePath))
            return [];

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, text, source_event_id, created_at, message_id
            FROM messages
            WHERE session_id = $session AND message_id > $after
            ORDER BY message_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$after", Math.Max(0, afterMessageId));
        command.Parameters.AddWithValue("$limit", clamped);

        var result = new List<StoredMessage>(Math.Min(clamped, 32));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.GetInt64(4)));
        }

        return result;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Same pragmas as every other store on this database (single-owner rule), plus a
            // connection-level write ban so the read-only intent is enforced by SQLite, not only
            // by the interface shape.
            SqliteSchema.Prepare(connection);

            await using var readOnly = connection.CreateCommand();
            readOnly.CommandText = $"PRAGMA query_only = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Normalises a filter instant to the exact wire format the host writes into
    /// <c>sessions.updated_at</c>, so the comparison stays exact instead of dropping to SQLite's
    /// one-second <c>datetime()</c> resolution (which made a same-second boundary ambiguous).
    /// </summary>
    private static string? NormalizeInstant(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string? NormalizeFilterValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>
    /// Turns a raw search term into a LIKE pattern. User input is escaped so <c>%</c> and <c>_</c>
    /// behave as literal characters instead of widening the match.
    /// </summary>
    private static string? NormalizeSearchTerm(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var trimmed = query.Trim();
        if (trimmed.Length > ConversationQueryLimits.MaxQueryLength)
            trimmed = trimmed[..ConversationQueryLimits.MaxQueryLength];

        var escaped = new StringBuilder(trimmed.Length + 2);
        foreach (var character in trimmed)
        {
            if (character is '%' or '_' or '\\')
                escaped.Append('\\');
            escaped.Append(character);
        }

        return "%" + escaped + "%";
    }
}

/// <summary>
/// Opaque keyset cursor for conversation pages: the last row's <c>updated_at</c> text plus its
/// session id, base64url encoded so consumers treat it as a token instead of a query fragment.
/// </summary>
internal sealed record ConversationCursor(string UpdatedAt, string SessionId)
{
    private const char Separator = '\n';

    public static string Encode(string updatedAt, string sessionId)
    {
        var payload = updatedAt + Separator + sessionId;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static ConversationCursor? TryDecode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var separator = payload.IndexOf(Separator, StringComparison.Ordinal);
            if (separator <= 0 || separator == payload.Length - 1)
                return null;

            return new ConversationCursor(payload[..separator], payload[(separator + 1)..]);
        }
        catch (FormatException)
        {
            // A forged or stale cursor is not an error: fall back to the first page.
            return null;
        }
    }
}
