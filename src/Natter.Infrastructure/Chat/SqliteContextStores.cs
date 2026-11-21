using ConversationKey = Natter.Application.Chat.ConversationKey;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>Shared P3 schema migration for summaries/memory and file permission hardening.</summary>
internal sealed class SqliteContextDatabase
{
    public SqliteContextDatabase(string databasePath)
    {
        Path = System.IO.Path.GetFullPath(databasePath);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path }.ToString();
        Migrate();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        SecureFiles();
        return connection;
    }

    public void SecureFiles()
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
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

    private void Migrate()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(version.ExecuteScalar());

        if (current < 1)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id      TEXT PRIMARY KEY,
                    source_key      TEXT NOT NULL,
                    protocol        TEXT NOT NULL,
                    platform        TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    guild_id        TEXT NULL,
                    actor_id        TEXT NULL,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    version         INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(source_key, conversation_id, guild_id, actor_id)
                );
                CREATE TABLE IF NOT EXISTS summaries (
                    summary_id      TEXT PRIMARY KEY,
                    session_id      TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                    from_message_id INTEGER NOT NULL,
                    to_message_id   INTEGER NOT NULL,
                    text            TEXT NOT NULL,
                    version         INTEGER NOT NULL,
                    prompt_version  TEXT NOT NULL,
                    created_at      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_summaries_session ON summaries(session_id, version DESC);
                """);
            current = 1;
        }

        if (current < 2)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS memory_items (
                    memory_id       TEXT PRIMARY KEY,
                    scope_type      TEXT NOT NULL,
                    scope_id        TEXT NOT NULL,
                    subject_id      TEXT NULL,
                    kind            TEXT NOT NULL,
                    text            TEXT NOT NULL,
                    data_json       TEXT NULL,
                    confidence      REAL NOT NULL,
                    sensitivity     TEXT NOT NULL,
                    user_confirmed  INTEGER NOT NULL,
                    source_message_ids_json TEXT NULL,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    expires_at      TEXT NULL,
                    version         INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_memory_scope ON memory_items(scope_type, scope_id, updated_at DESC);
                """);
            current = 2;
        }

        if (current < 3)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS agent_tasks (
                    task_id         TEXT PRIMARY KEY,
                    session_id      TEXT NOT NULL,
                    source_key      TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    guild_id        TEXT NULL,
                    actor_id        TEXT NULL,
                    prompt          TEXT NOT NULL,
                    status          TEXT NOT NULL,
                    lease_owner     TEXT NULL,
                    lease_expires_at TEXT NULL,
                    result_text     TEXT NULL,
                    error_code      TEXT NULL,
                    error_message   TEXT NULL,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL,
                    version         INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_agent_tasks_session ON agent_tasks(session_id, status);
                CREATE TABLE IF NOT EXISTS task_events (
                    task_id     TEXT NOT NULL,
                    seq         INTEGER NOT NULL,
                    kind        TEXT NOT NULL,
                    payload_json TEXT NULL,
                    created_at  TEXT NOT NULL,
                    PRIMARY KEY(task_id, seq)
                );
                """);
            current = 3;
        }

        using var setVersion = connection.CreateCommand();
        setVersion.CommandText = $"PRAGMA user_version = {current};";
        setVersion.ExecuteNonQuery();
        SecureFiles();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly SqliteContextDatabase _database;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public SqliteMemoryStore(string databasePath) => _database = new SqliteContextDatabase(databasePath);

    public async ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var now = DateTimeOffset.UtcNow;
        var scopeId = MemoryScope.Id(request.Source, request.Conversation, request.User, request.ScopeType);
        var text = request.Text.Trim();

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                   sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version
            FROM memory_items WHERE scope_id = $scope AND kind = $kind AND lower(text) = lower($text) LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$scope", scopeId);
        existing.Parameters.AddWithValue("$kind", request.Kind);
        existing.Parameters.AddWithValue("$text", text);
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Read(reader);
        }

        var item = new MemoryItem(
            "mem_" + Guid.NewGuid().ToString("N")[..16],
            request.ScopeType,
            scopeId,
            request.User.Id,
            request.Kind,
            text,
            request.DataJson,
            Math.Clamp(request.Confidence, 0, 1),
            string.IsNullOrWhiteSpace(request.Sensitivity) ? "normal" : request.Sensitivity,
            request.UserConfirmed,
            null,
            now,
            now,
            request.ExpiresAt,
            1);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory_items
                (memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                 sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
            VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $data, $confidence,
                    $sensitivity, $confirmed, $sources, $created, $updated, $expires, $version);
            """;
        AddParameters(insert, item);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var scopes = new[]
        {
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.User),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Conversation),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Guild),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Global),
        };

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                   sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version
            FROM memory_items
            WHERE scope_id IN ($s0, $s1, $s2, $s3)
              AND (expires_at IS NULL OR expires_at > $now)
              {(query.IncludePrivate ? string.Empty : "AND sensitivity <> 'private'")}
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$s0", scopes[0]);
        command.Parameters.AddWithValue("$s1", scopes[1]);
        command.Parameters.AddWithValue("$s2", scopes[2]);
        command.Parameters.AddWithValue("$s3", scopes[3]);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Max(0, query.Limit));

        var result = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    public async ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (!string.IsNullOrWhiteSpace(request.MemoryId))
        {
            command.CommandText = """
                DELETE FROM memory_items WHERE memory_id = $id
                  AND scope_id IN ($s0, $s1, $s2, $s3)
                  AND (scope_type = 'user' OR subject_id = $actor);
                """;
            command.Parameters.AddWithValue("$id", request.MemoryId);
            command.Parameters.AddWithValue("$actor", request.User.Id);
            command.Parameters.AddWithValue("$s0", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.User));
            command.Parameters.AddWithValue("$s1", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Conversation));
            command.Parameters.AddWithValue("$s2", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Guild));
            command.Parameters.AddWithValue("$s3", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Global));
        }
        else if (!string.IsNullOrWhiteSpace(request.Text))
        {
            command.CommandText = """
                DELETE FROM memory_items WHERE lower(text) = lower($text)
                  AND scope_id IN ($s0, $s1, $s2, $s3)
                  AND (scope_type = 'user' OR subject_id = $actor);
                """;
            command.Parameters.AddWithValue("$text", request.Text);
            command.Parameters.AddWithValue("$actor", request.User.Id);
            command.Parameters.AddWithValue("$s0", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.User));
            command.Parameters.AddWithValue("$s1", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Conversation));
            command.Parameters.AddWithValue("$s2", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Guild));
            command.Parameters.AddWithValue("$s3", MemoryScope.Id(request.Source, request.Conversation, request.User, MemoryScopeType.Global));
        }
        else
        {
            return false;
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default)
    {
        var items = await SearchAsync(
            new MemoryQuery(
                request.Source,
                request.Conversation,
                request.User,
                10_000,
                IncludePrivate: request.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group),
            cancellationToken)
            .ConfigureAwait(false);
        return new MemoryExport(1, DateTimeOffset.UtcNow, items);
    }

    public async ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default)
    {
        var imported = 0;
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in request.Export.Items)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = request.Overwrite
                ? """
                  INSERT INTO memory_items
                      (memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                       sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
                  VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $data, $confidence,
                          $sensitivity, $confirmed, $sources, $created, $updated, $expires, $version)
                  ON CONFLICT(memory_id) DO UPDATE SET
                      text=excluded.text, data_json=excluded.data_json, confidence=excluded.confidence,
                      sensitivity=excluded.sensitivity, updated_at=excluded.updated_at, expires_at=excluded.expires_at,
                      version=excluded.version;
                  """
                : """
                  INSERT OR IGNORE INTO memory_items
                      (memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                       sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
                  VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $data, $confidence,
                          $sensitivity, $confirmed, $sources, $created, $updated, $expires, $version);
                  """;
            AddParameters(command, item);
            imported += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return imported;
    }

    private static void AddParameters(SqliteCommand command, MemoryItem item)
    {
        command.Parameters.AddWithValue("$id", item.MemoryId);
        command.Parameters.AddWithValue("$scope_type", item.ScopeType.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$scope", item.ScopeId);
        command.Parameters.AddWithValue("$subject", (object?)item.SubjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$text", item.Text);
        command.Parameters.AddWithValue("$data", (object?)item.DataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", item.Confidence);
        command.Parameters.AddWithValue("$sensitivity", item.Sensitivity);
        command.Parameters.AddWithValue("$confirmed", item.UserConfirmed ? 1 : 0);
        command.Parameters.AddWithValue("$sources", item.SourceMessageIds is null
            ? DBNull.Value : JsonSerializer.Serialize(item.SourceMessageIds, JsonOptions));
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expires", item.ExpiresAt is null
            ? DBNull.Value : item.ExpiresAt.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$version", item.Version);
    }

    private static MemoryItem Read(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            Enum.TryParse<MemoryScopeType>(reader.GetString(1), ignoreCase: true, out var scopeType)
                ? scopeType : MemoryScopeType.User,
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetDouble(7),
            reader.GetString(8),
            reader.GetInt32(9) != 0,
            reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(10), JsonOptions),
            DateTimeOffset.Parse(reader.GetString(11), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(14));
}

public sealed class SqliteSummaryStore : ISummaryStore
{
    private readonly SqliteContextDatabase _database;
    public SqliteSummaryStore(string databasePath) => _database = new SqliteContextDatabase(databasePath);

    public async ValueTask<ConversationSummary?> GetLatestAsync(ConversationKey key, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT summary_id, from_message_id, to_message_id, text, version, prompt_version, created_at
            FROM summaries WHERE session_id = $session ORDER BY version DESC, created_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", InMemoryConversationStore.SessionId(key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new ConversationSummary(
            reader.GetString(0), key, reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async ValueTask SaveAsync(ConversationSummary summary, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var session = connection.CreateCommand())
        {
            var parts = summary.Key.SourceKey.Split(':', 3);
            session.CommandText = """
                INSERT OR IGNORE INTO sessions
                    (session_id, source_key, protocol, platform, conversation_id, guild_id, actor_id, created_at, updated_at, version)
                VALUES ($id, $source, $protocol, $platform, $conversation, $guild, $actor, $now, $now, 0);
                """;
            session.Parameters.AddWithValue("$id", InMemoryConversationStore.SessionId(summary.Key));
            session.Parameters.AddWithValue("$source", summary.Key.SourceKey);
            session.Parameters.AddWithValue("$protocol", parts.Length > 0 ? parts[0] : string.Empty);
            session.Parameters.AddWithValue("$platform", parts.Length > 1 ? parts[1] : string.Empty);
            session.Parameters.AddWithValue("$conversation", summary.Key.ConversationId);
            session.Parameters.AddWithValue("$guild", (object?)summary.Key.GuildId ?? DBNull.Value);
            session.Parameters.AddWithValue("$actor", (object?)summary.Key.ActorId ?? DBNull.Value);
            session.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO summaries
                (summary_id, session_id, from_message_id, to_message_id, text, version, prompt_version, created_at)
            VALUES ($id, $session, $from, $to, $text, $version, $prompt, $created)
            ON CONFLICT(summary_id) DO UPDATE SET
                text=excluded.text, version=excluded.version, prompt_version=excluded.prompt_version;
            """;
        command.Parameters.AddWithValue("$id", summary.SummaryId);
        command.Parameters.AddWithValue("$session", InMemoryConversationStore.SessionId(summary.Key));
        command.Parameters.AddWithValue("$from", summary.FromMessageId);
        command.Parameters.AddWithValue("$to", summary.ToMessageId);
        command.Parameters.AddWithValue("$text", summary.Text);
        command.Parameters.AddWithValue("$version", summary.Version);
        command.Parameters.AddWithValue("$prompt", summary.PromptVersion);
        command.Parameters.AddWithValue("$created", summary.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask InvalidateAsync(ConversationKey key, string reason, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM summaries WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", InMemoryConversationStore.SessionId(key));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
