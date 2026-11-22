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
        // S2-B5: foreign_keys + busy_timeout are set by the single schema owner for every store.
        SqliteSchema.Prepare(connection);
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
        SqliteSchema.Migrate(connection);
        SecureFiles();
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
                (memory_id, scope_type, scope_id, subject_id, kind, text, text_index, data_json, confidence,
                 sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
            VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $index, $data, $confidence,
                    $sensitivity, $confirmed, $sources, $created, $updated, $expires, $version);
            """;
        AddParameters(insert, item);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return item;
    }

    public async ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var scopes = new[]
        {
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.User),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Conversation),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Guild),
            MemoryScope.Id(query.Source, query.Conversation, query.User, MemoryScopeType.Global),
        };
        var limit = Math.Max(0, query.Limit);

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tokens = string.Equals(SqliteSchema.FtsMode, "pretokenized", StringComparison.Ordinal)
            ? MemoryScoring.TokenizeForIndex(query.Query)
            : MemoryScoring.Tokenize(query.Query);
        if (tokens.Count > 0)
        {
            try
            {
                var fts = await SearchFtsAsync(connection, query, scopes, tokens, now, limit, cancellationToken).ConfigureAwait(false);
                if (fts.Count > 0)
                    return fts;
                // No relevant hit: fall back to recent entries (legacy behaviour) instead of
                // returning an empty memory for a turn that used to see its recent memories.
            }
            catch (SqliteException)
            {
                // A runtime without FTS5 must not break retrieval; fall back to recent entries.
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT memory_id, scope_type, scope_id, subject_id, kind, text, data_json, confidence,
                   sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version
            FROM memory_items
            WHERE {ScopePredicate(query, scopes)}
              AND (expires_at IS NULL OR expires_at > $now)
              {(query.IncludePrivate ? string.Empty : "AND sensitivity <> 'private'")}
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        AddScopeParameters(command, query, scopes);
        command.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    private static async ValueTask<IReadOnlyList<MemoryItem>> SearchFtsAsync(
        SqliteConnection connection,
        MemoryQuery query,
        string[] scopes,
        IReadOnlyList<string> tokens,
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var match = string.Join(" OR ", tokens.Select(token => $"\"{token.Replace("\"", string.Empty, StringComparison.Ordinal)}\""));
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.memory_id, m.scope_type, m.scope_id, m.subject_id, m.kind, m.text, m.data_json, m.confidence,
                   m.sensitivity, m.user_confirmed, m.source_message_ids_json, m.created_at, m.updated_at, m.expires_at, m.version,
                   bm25(memory_fts) AS rank
            FROM memory_fts
            JOIN memory_items m ON m.memory_id = memory_fts.memory_id
            WHERE memory_fts MATCH $match
              AND {ScopePredicate(query, scopes, prefix: "m.")}
              AND (m.expires_at IS NULL OR m.expires_at > $now)
              {(query.IncludePrivate ? string.Empty : "AND m.sensitivity <> 'private'")}
            ORDER BY rank, m.updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        for (var index = 0; index < scopes.Length; index++)
            command.Parameters.AddWithValue("$s" + index, scopes[index]);
        if (query.Scope is { } scope)
        {
            command.Parameters.AddWithValue("$scope_type", scope.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$scope_key", scopes[ScopeIndex(scope)]);
        }
        command.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));

        var halfLife = query.DecayHalfLife > TimeSpan.Zero ? query.DecayHalfLife : TimeSpan.FromDays(30);
        var scored = new List<(MemoryItem Item, double Score)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = Read(reader);
                var bm25 = reader.IsDBNull(15) ? 0d : reader.GetDouble(15);
                var ageDays = Math.Max(0, (now - item.UpdatedAt).TotalDays);
                // v1 live path keeps historical exponential decay; the power law is the v2 shape.
                // Both modes live in MemoryDecay (LegacyRetention / Retention) so they stay comparable.
                var decay = MemoryDecay.LegacyRetention(ageDays, halfLife.TotalDays);
                scored.Add((item, (-bm25) * decay));
            }
        }

        if (scored.Count == 0)
            return [];

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Item.UpdatedAt)
            .Take(limit)
            .Select(pair => pair.Item)
            .ToArray();
    }

    private static string ScopePredicate(MemoryQuery query, string[] scopes, string prefix = "")
        => query.Scope is null
            ? $"{prefix}scope_id IN ($s0, $s1, $s2, $s3)"
            : $"{prefix}scope_type = $scope_type AND {prefix}scope_id = $scope_key";

    private static void AddScopeParameters(SqliteCommand command, MemoryQuery query, string[] scopes)
    {
        for (var index = 0; index < scopes.Length; index++)
            command.Parameters.AddWithValue("$s" + index, scopes[index]);
        if (query.Scope is { } scope)
        {
            command.Parameters.AddWithValue("$scope_type", scope.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$scope_key", scopes[ScopeIndex(scope)]);
        }
    }

    private static int ScopeIndex(MemoryScopeType scope) => scope switch
    {
        MemoryScopeType.User => 0,
        MemoryScopeType.Conversation => 1,
        MemoryScopeType.Guild => 2,
        MemoryScopeType.Global => 3,
        _ => 0,
    };

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
                      (memory_id, scope_type, scope_id, subject_id, kind, text, text_index, data_json, confidence,
                       sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
                  VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $index, $data, $confidence,
                          $sensitivity, $confirmed, $sources, $created, $updated, $expires, $version)
                  ON CONFLICT(memory_id) DO UPDATE SET
                      text=excluded.text, data_json=excluded.data_json, confidence=excluded.confidence,
                      sensitivity=excluded.sensitivity, updated_at=excluded.updated_at, expires_at=excluded.expires_at,
                      version=excluded.version;
                  """
                : """
                  INSERT OR IGNORE INTO memory_items
                      (memory_id, scope_type, scope_id, subject_id, kind, text, text_index, data_json, confidence,
                       sensitivity, user_confirmed, source_message_ids_json, created_at, updated_at, expires_at, version)
                  VALUES ($id, $scope_type, $scope, $subject, $kind, $text, $index, $data, $confidence,
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
        command.Parameters.AddWithValue("$index", MemoryScoring.BuildIndexText(item.Text));
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
