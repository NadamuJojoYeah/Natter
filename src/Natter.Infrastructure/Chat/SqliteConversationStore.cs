using Microsoft.Data.Sqlite;
using Natter.Application.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>SQLite host-owned conversation store (WAL, transactional append, event idempotency).</summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private readonly string _databasePath;

    public SqliteConversationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        Initialize();
    }

    public async ValueTask<ConversationState> GetOrCreateAsync(
        ConversationKey key,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetOrCreateCoreAsync(connection, null, key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StoredMessage>> GetRecentAsync(
        ConversationKey key,
        int maxMessages,
        int maxCharacters,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetOrCreateCoreAsync(connection, null, key, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, text, source_event_id, created_at, message_id
            FROM messages WHERE session_id = $session
            ORDER BY message_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", session.SessionId);
        command.Parameters.AddWithValue("$limit", Math.Max(1, maxMessages) * 2);

        var descending = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            descending.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(4)));
        }

        var result = new List<StoredMessage>();
        var characters = 0;
        foreach (var message in descending)
        {
            if (result.Count >= Math.Max(0, maxMessages))
                break;
            characters += message.Text.Length;
            if (characters > Math.Max(0, maxCharacters) && result.Count > 0)
                break;
            result.Add(message);
        }
        result.Reverse();
        return result;
    }

    public async ValueTask AppendTurnAsync(
        ConversationKey key,
        StoredMessage userMessage,
        StoredMessage assistantMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetOrCreateCoreAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(userMessage.SourceEventId))
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM messages WHERE session_id = $session AND source_event_id = $event LIMIT 1;";
            exists.Parameters.AddWithValue("$session", session.SessionId);
            exists.Parameters.AddWithValue("$event", userMessage.SourceEventId);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await InsertAsync(connection, transaction, session.SessionId, userMessage, cancellationToken).ConfigureAwait(false);
        await InsertAsync(connection, transaction, session.SessionId, assistantMessage, cancellationToken).ConfigureAwait(false);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE sessions SET updated_at = $now, version = version + 1 WHERE session_id = $session;";
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$session", session.SessionId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(ConversationKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE source_key = $source AND conversation_id = $conversation AND COALESCE(guild_id, '') = COALESCE($guild, '') AND COALESCE(actor_id, '') = COALESCE($actor, '');";
        command.Parameters.AddWithValue("$source", key.SourceKey);
        command.Parameters.AddWithValue("$conversation", key.ConversationId);
        command.Parameters.AddWithValue("$guild", (object?)key.GuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)key.ActorId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<StoredMessage>> GetAfterAsync(
        ConversationKey key,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var session = await GetOrCreateCoreAsync(connection, null, key, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, text, source_event_id, created_at, message_id
            FROM messages WHERE session_id = $session AND message_id > $after
            ORDER BY message_id ASC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", session.SessionId);
        command.Parameters.AddWithValue("$after", afterMessageId);
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        var result = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new StoredMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetInt64(4)));
        }
        return result;
    }

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // S2-B5: one migration owner for the shared natter.db schema.
        SqliteSchema.Migrate(connection);
        SecureFiles();
    }

    private void SecureFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
            }
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        SecureFiles();
        SqliteSchema.Prepare(connection);
        return connection;
    }

    private static async ValueTask<ConversationState> GetOrCreateCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ConversationKey key,
        CancellationToken cancellationToken)
    {
        var sessionId = InMemoryConversationStore.SessionId(key);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO sessions
                (session_id, source_key, protocol, platform, conversation_id, guild_id, actor_id, created_at, updated_at, version)
            VALUES ($id, $source, $protocol, $platform, $conversation, $guild, $actor, $now, $now, 0);
            SELECT session_id, version FROM sessions
            WHERE source_key = $source AND conversation_id = $conversation
              AND COALESCE(guild_id, '') = COALESCE($guild, '') AND COALESCE(actor_id, '') = COALESCE($actor, '')
            LIMIT 1;
            """;
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$source", key.SourceKey);
        var parts = key.SourceKey.Split(':', 3);
        command.Parameters.AddWithValue("$protocol", parts.Length > 0 ? parts[0] : string.Empty);
        command.Parameters.AddWithValue("$platform", parts.Length > 1 ? parts[1] : string.Empty);
        command.Parameters.AddWithValue("$conversation", key.ConversationId);
        command.Parameters.AddWithValue("$guild", (object?)key.GuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)key.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The conversation session could not be created.");
        return new ConversationState(reader.GetString(0), key, reader.GetInt32(1));
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        StoredMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO messages (session_id, role, text, source_event_id, created_at)
            VALUES ($session, $role, $text, $event, $created);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$text", message.Text);
        command.Parameters.AddWithValue("$event", (object?)message.SourceEventId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
