using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>Which tokenization the <c>memory_entries_fts</c> index uses.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public enum MemoryFtsTokenization
{
    /// <summary>Application pre-tokenizer (CJK bi-grams + singles) indexed with unicode61. Production default.</summary>
    Pretokenized,

    /// <summary>Raw content indexed with the FTS5 <c>trigram</c> tokenizer. Measurement only: trigram cannot
    /// match two-character Chinese words at all, which is exactly what the eval quantifies.</summary>
    RawTrigram,
}

/// <summary>
/// Measurement knobs for <see cref="SqliteMemoryEntryStore"/> (P6.9 calibration).
/// <b>Not part of profile configuration</b> and never exposed through <c>config.schema.json</c>:
/// a deployment must not be able to select "raw trigram" or disable decay.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed record MemoryEntryStoreOptions
{
    public static MemoryEntryStoreOptions Default { get; } = new();

    public MemoryFtsTokenization Tokenization { get; init; } = MemoryFtsTokenization.Pretokenized;

    /// <summary>False makes recall ignore the forgetting curve (retention = 1).</summary>
    public bool ApplyDecay { get; init; } = true;

    /// <summary>Multiplies every entry's stability; the eval uses it to sweep 30d vs 90d-like settings.</summary>
    public double StabilityScale { get; init; } = 1d;

    /// <summary>How many BM25 candidates enter the re-rank before <c>Limit</c> is applied.</summary>
    public int CandidatePoolMultiplier { get; init; } = 4;

    /// <summary>
    /// Pins "now" so decay and stability maths are deterministic. Measurement only: the eval seeds
    /// entries at fixed ages relative to this instant, which makes two runs byte-identical.
    /// </summary>
    public DateTimeOffset? FixedNow { get; init; }
}

/// <summary>
/// P6.5 layered memory store (docs/P6_MEMORY_SYSTEM.md §11). Owns <c>memory_entries</c> and the
/// Trace index; shares the single <c>natter.db</c> schema owner through <see cref="SqliteSchema"/>.
/// </summary>
public sealed class SqliteMemoryEntryStore : IMemoryEntryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SqliteContextDatabase _database;
    private readonly MemoryEntryStoreOptions _options;

    public SqliteMemoryEntryStore(string databasePath)
        : this(databasePath, MemoryEntryStoreOptions.Default)
    {
    }

    public SqliteMemoryEntryStore(string databasePath, MemoryEntryStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _database = new SqliteContextDatabase(databasePath);
        ConfigureFtsTokenization();
    }

    /// <summary>Current time: pinned by <see cref="MemoryEntryStoreOptions.FixedNow"/> for measurement.</summary>
    private DateTimeOffset UtcNow => _options.FixedNow ?? DateTimeOffset.UtcNow;

    /// <summary>Test/ops hook: which tokenizer the shared schema settled on.</summary>
    public static string FtsTokenizer => SqliteSchema.FtsTokenizer;

    public async ValueTask<MemoryEntry> WriteAsync(
        MemoryEntryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

        var scopeType = request.Scope ?? MemoryScope.DefaultWriteScope();
        var scopeId = MemoryScope.Id(request.Source, request.Conversation, request.User, scopeType);
        var content = request.Content.Trim();
        var hash = MemoryEntryMapping.Hash(request.Type, scopeId, content);
        var textIndex = MemoryScoring.BuildIndexText(content);
        var now = UtcNow;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Repeat detection (§6.2): same scope + type + normalized content is the same memory.
        // A repeat reinforces the existing row instead of creating a rival copy.
        await using (var existing = connection.CreateCommand())
        {
            existing.CommandText = """
                SELECT entry_id, stability_days, reinforce_count, confidence, created_at, last_recalled_at
                FROM memory_entries
                WHERE scope_id = $scope AND type = $type AND content_hash = $hash
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$scope", scopeId);
            existing.Parameters.AddWithValue("$type", MemoryEntryMapping.ToStorageValue(request.Type));
            existing.Parameters.AddWithValue("$hash", hash);

            string? existingId = null;
            double stability = 0;
            int reinforceCount = 0;
            double confidence = 0;
            string? createdAt = null;
            string? lastRecalledAt = null;

            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingId = reader.GetString(0);
                    stability = reader.GetDouble(1);
                    reinforceCount = reader.GetInt32(2);
                    confidence = reader.GetDouble(3);
                    createdAt = reader.GetString(4);
                    lastRecalledAt = reader.IsDBNull(5) ? null : reader.GetString(5);
                }
            }

            if (existingId is not null)
            {
                // §6.1: S × (1 + α·S^-0.0658). Always greater than 1, and grows less for stable entries.
                var reinforced = MemoryStability.Reinforce(stability, MemoryDecay.DefaultAlpha);
                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE memory_entries
                    SET stability_days = $stability,
                        reinforce_count = $count,
                        confidence = $confidence,
                        archived = 0,
                        updated_at = $updated
                    WHERE entry_id = $id;
                    """;
                update.Parameters.AddWithValue("$stability", reinforced);
                update.Parameters.AddWithValue("$count", reinforceCount + 1);
                update.Parameters.AddWithValue("$confidence", Math.Clamp(Math.Max(confidence, request.Confidence), 0, 1));
                update.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$id", existingId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return new MemoryEntry(
                    existingId,
                    request.Type,
                    scopeType,
                    scopeId,
                    content,
                    hash,
                    reinforced,
                    reinforceCount + 1,
                    Math.Clamp(Math.Max(confidence, request.Confidence), 0, 1),
                    Archived: false,
                    SupersedesId: null,
                    request.SourceMessageIds,
                    request.Sensitivity,
                    DateTimeOffset.Parse(createdAt!, CultureInfo.InvariantCulture),
                    now,
                    lastRecalledAt is null ? null : DateTimeOffset.Parse(lastRecalledAt, CultureInfo.InvariantCulture));
            }
        }

        var entry = new MemoryEntry(
            "me_" + Guid.NewGuid().ToString("N")[..16],
            request.Type,
            scopeType,
            scopeId,
            content,
            hash,
            MemoryStability.InitialDays(request.Type),
            ReinforceCount: 0,
            Math.Clamp(request.Confidence, 0, 1),
            Archived: false,
            SupersedesId: null,
            request.SourceMessageIds,
            string.IsNullOrWhiteSpace(request.Sensitivity) ? "normal" : request.Sensitivity,
            now,
            now,
            LastRecalledAt: null);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory_entries
                (entry_id, type, scope_type, scope_id, content, content_hash, text_index,
                 stability_days, reinforce_count, confidence, archived, supersedes_id,
                 sensitivity, source_message_ids_json, created_at, updated_at, last_recalled_at)
            VALUES ($id, $type, $scope_type, $scope, $content, $hash, $index,
                    $stability, $count, $confidence, 0, $supersedes,
                    $sensitivity, $sources, $created, $updated, $recalled);
            """;
        AddParameters(insert, entry);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(
        MemoryEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var scopes = AllScopeIds(query.Source, query.Conversation, query.User);
        var limit = Math.Max(1, query.Limit);
        var pool = Math.Max(limit, limit * Math.Max(1, _options.CandidatePoolMultiplier));

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tokens = BuildQueryTokens(query.Query);
        if (tokens.Count > 0)
        {
            try
            {
                var ranked = await SearchFtsAsync(connection, query, scopes, tokens, now, pool, cancellationToken)
                    .ConfigureAwait(false);
                if (ranked.Count > 0)
                    return ranked.Take(limit).ToArray();
            }
            catch (SqliteException)
            {
                // A runtime without FTS5 must not break retrieval.
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM memory_entries m
            WHERE {ScopePredicate(query, scopes, "m.")}
              {(query.IncludeArchived ? string.Empty : "AND m.archived = 0")}
              {(query.IncludePrivate ? string.Empty : "AND m.sensitivity <> 'private'")}
              {TypePredicate(query, "m.")}
            ORDER BY m.updated_at DESC LIMIT $limit;
            """;
        AddScopeParameters(command, query, scopes);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    public async ValueTask<MemoryEntry?> GetAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default)
    {
        var scopes = AllScopeIds(source, conversation, user);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM memory_entries m
            WHERE m.entry_id = $id AND m.scope_id IN ($s0, $s1, $s2, $s3);
            """;
        command.Parameters.AddWithValue("$id", id);
        for (var index = 0; index < scopes.Length; index++)
            command.Parameters.AddWithValue("$s" + index, scopes[index]);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async ValueTask<bool> ArchiveAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default)
    {
        var scopes = AllScopeIds(source, conversation, user);
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_entries SET archived = 1, updated_at = $now
            WHERE entry_id = $id AND scope_id IN ($s0, $s1, $s2, $s3);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", UtcNow.ToString("O", CultureInfo.InvariantCulture));
        for (var index = 0; index < scopes.Length; index++)
            command.Parameters.AddWithValue("$s" + index, scopes[index]);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask ReinforceRecallAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return;

        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_entries
            SET stability_days = stability_days * $factor, last_recalled_at = $now
            WHERE entry_id = $id;
            """;
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.AddWithValue("$factor", 1 + MemoryStability.RecallGrowth);
        command.Parameters.AddWithValue("$now", UtcNow.ToString("O", CultureInfo.InvariantCulture));
        foreach (var id in ids)
        {
            idParameter.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// v2 export. Version policy (§11): write the current version, read one version back, and
    /// refuse a newer envelope instead of dropping the fields this build does not know about.
    /// </summary>
    public async ValueTask<MemoryEntryExport> ExportAsync(
        Source source,
        Conversation conversation,
        User user,
        bool includePrivate,
        CancellationToken cancellationToken = default)
    {
        var items = await SearchAsync(
            new MemoryEntryQuery(source, conversation, user)
            {
                IncludeArchived = true,
                IncludePrivate = includePrivate,
                Limit = int.MaxValue,
            },
            cancellationToken).ConfigureAwait(false);

        return MemoryEntryExport.Create(items, UtcNow);
    }

    /// <summary>Imports a v2 envelope after validating its version.</summary>
    public async ValueTask<int> ImportAsync(
        Source source,
        Conversation conversation,
        User user,
        MemoryEntryExport export,
        CancellationToken cancellationToken = default)
    {
        export.EnsureReadable();

        var visibleScopes = AllScopeIds(source, conversation, user);
        var imported = 0;
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var item in export.Items)
        {
            // Import must not break scope isolation: an entry is only accepted when it belongs to
            // a scope the caller can see.
            if (!ScopesMatch(item.ScopeId, visibleScopes))
                continue;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO memory_entries
                    (entry_id, type, scope_type, scope_id, content, content_hash, text_index,
                     stability_days, reinforce_count, confidence, archived, supersedes_id,
                     sensitivity, source_message_ids_json, created_at, updated_at, last_recalled_at)
                VALUES ($id, $type, $scope_type, $scope, $content, $hash, $index,
                        $stability, $count, $confidence, $archived, $supersedes,
                        $sensitivity, $sources, $created, $updated, $recalled)
                ON CONFLICT(entry_id) DO UPDATE SET
                    content = excluded.content, content_hash = excluded.content_hash,
                    text_index = excluded.text_index, stability_days = excluded.stability_days,
                    reinforce_count = excluded.reinforce_count, confidence = excluded.confidence,
                    archived = excluded.archived, updated_at = excluded.updated_at;
                """;
            AddParameters(command, item);
            try
            {
                imported += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Duplicate (scope, type, hash) owned by a different id: keep the existing entry.
            }
        }

        return imported;
    }

    /// <summary>
    /// Backfills the Trace index from the existing <c>messages</c> table (§11: index, do not copy).
    /// Tokenization happens here because SQL cannot run the CJK pre-tokenizer. Returns the number
    /// of newly indexed turns; safe to call repeatedly.
    /// </summary>
    public async ValueTask<int> IndexUnindexedTurnsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);

        var pending = new List<(long MessageId, string SessionId, string ScopeKey, string Text, string CreatedAt)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT m.message_id, m.session_id, s.source_key, s.actor_id, m.text, m.created_at
                FROM messages m
                JOIN sessions s ON s.session_id = m.session_id
                WHERE m.message_id NOT IN (SELECT message_id FROM memory_turns)
                ORDER BY m.message_id;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sourceKey = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var actorId = reader.IsDBNull(3) ? null : reader.GetString(3);
                pending.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    string.IsNullOrEmpty(actorId) ? sourceKey : $"{sourceKey}|user|{actorId}",
                    reader.GetString(4),
                    reader.GetString(5)));
            }
        }

        if (pending.Count == 0)
            return 0;

        using var transaction = connection.BeginTransaction();
        await using var insertTurn = connection.CreateCommand();
        insertTurn.Transaction = transaction;
        insertTurn.CommandText = """
            INSERT OR IGNORE INTO memory_turns (message_id, session_id, scope_key, text_index, created_at)
            VALUES ($id, $session, $scope, $index, $created);
            """;
        var idParameter = insertTurn.Parameters.Add("$id", SqliteType.Integer);
        insertTurn.Parameters.Add("$session", SqliteType.Text);
        insertTurn.Parameters.Add("$scope", SqliteType.Text);
        insertTurn.Parameters.Add("$index", SqliteType.Text);
        insertTurn.Parameters.Add("$created", SqliteType.Text);

        await using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = "INSERT INTO memory_turns_fts(message_id, text_index) VALUES ($id, $index);";
        var ftsIdParameter = insertFts.Parameters.Add("$id", SqliteType.Integer);
        insertFts.Parameters.Add("$index", SqliteType.Text);

        foreach (var row in pending)
        {
            var index = MemoryScoring.BuildIndexText(row.Text);
            idParameter.Value = row.MessageId;
            insertTurn.Parameters["$session"].Value = row.SessionId;
            insertTurn.Parameters["$scope"].Value = row.ScopeKey;
            insertTurn.Parameters["$index"].Value = index;
            insertTurn.Parameters["$created"].Value = row.CreatedAt;
            insertTurn.ExecuteNonQuery();

            ftsIdParameter.Value = row.MessageId;
            insertFts.Parameters["$index"].Value = index;
            insertFts.ExecuteNonQuery();
        }

        transaction.Commit();
        return pending.Count;
    }

    private async ValueTask<List<MemoryEntry>> SearchFtsAsync(
        SqliteConnection connection,
        MemoryEntryQuery query,
        string[] scopes,
        IReadOnlyList<string> tokens,
        DateTimeOffset now,
        int pool,
        CancellationToken cancellationToken)
    {
        var match = string.Join(" OR ", tokens.Select(Quote));
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}, bm25(memory_entries_fts) AS rank
            FROM memory_entries_fts
            JOIN memory_entries m ON m.entry_id = memory_entries_fts.entry_id
            WHERE memory_entries_fts MATCH $match
              AND {ScopePredicate(query, scopes, "m.")}
              {(query.IncludeArchived ? string.Empty : "AND m.archived = 0")}
              {(query.IncludePrivate ? string.Empty : "AND m.sensitivity <> 'private'")}
              {TypePredicate(query, "m.")}
            ORDER BY rank, m.updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        AddScopeParameters(command, query, scopes);
        command.Parameters.AddWithValue("$limit", pool);

        var scored = new List<(MemoryEntry Entry, double Score)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var entry = Read(reader);
                // bm25 is the trailing column of the 16-column projection above.
                var bm25 = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetDouble(16) : 0d;
                // FTS5 bm25 is negative and smaller is better. The power-law retention is applied here
                // (not in SQL) so the candidate pool stays BM25 wide (§9.1). There is no category
                // multiplier: the per-type decay exponent already carries the prior (§9.1.1).
                var retention = _options.ApplyDecay
                    ? MemoryDecay.Retention(
                        Math.Max(0, (now - entry.UpdatedAt).TotalDays),
                        entry.StabilityDays * _options.StabilityScale,
                        MemoryDecay.ProfileFor(entry.Type).DecayD)
                    : 1d;
                scored.Add((entry, (-bm25) * retention));
            }
        }

        if (scored.Count == 0)
            return [];

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Entry.UpdatedAt)
            .Select(pair => pair.Entry)
            .ToList();
    }

    private const string Columns = """
        m.entry_id, m.type, m.scope_type, m.scope_id, m.content, m.content_hash,
        m.stability_days, m.reinforce_count, m.confidence, m.archived, m.supersedes_id,
        m.source_message_ids_json, m.sensitivity, m.created_at, m.updated_at, m.last_recalled_at
        """;

    /// <summary>
    /// Query-side tokenization must match the index side, otherwise nothing ever matches.
    /// Pretokenized: same CJK bi-gram + single algorithm as the stored <c>text_index</c>.
    /// RawTrigram: three-character sliding windows (the shortest unit a trigram index can match).
    /// </summary>
    private IReadOnlyList<string> BuildQueryTokens(string? query)
    {
        if (_options.Tokenization == MemoryFtsTokenization.Pretokenized)
            return MemoryScoring.TokenizeForIndex(query);

        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalized = query.Trim();
        if (normalized.Length < 3)
            return [];   // a trigram index cannot match a query shorter than its window

        var windows = new List<string>(normalized.Length - 2);
        for (var index = 0; index + 2 < normalized.Length; index++)
            windows.Add(normalized.Substring(index, 3));

        return windows.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Rebuilds <c>memory_entries_fts</c> for the configured tokenization. Production keeps the
    /// schema-created pretokenized index; <see cref="MemoryFtsTokenization.RawTrigram"/> is a
    /// measurement mode that indexes raw content with the trigram tokenizer.
    /// </summary>
    private void ConfigureFtsTokenization()
    {
        if (_options.Tokenization == MemoryFtsTokenization.Pretokenized)
            return;

        using var connection = new SqliteConnection(_database.ConnectionString);
        connection.Open();
        SqliteSchema.Prepare(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS memory_entries_fts_ai;
            DROP TRIGGER IF EXISTS memory_entries_fts_au;
            DROP TRIGGER IF EXISTS memory_entries_fts_ad;
            DROP TABLE IF EXISTS memory_entries_fts;
            CREATE VIRTUAL TABLE memory_entries_fts USING fts5(entry_id UNINDEXED, content, tokenize='trigram');
            CREATE TRIGGER memory_entries_fts_ai AFTER INSERT ON memory_entries BEGIN
                INSERT INTO memory_entries_fts(entry_id, content) VALUES (new.entry_id, new.content);
            END;
            CREATE TRIGGER memory_entries_fts_au AFTER UPDATE OF content ON memory_entries BEGIN
                DELETE FROM memory_entries_fts WHERE entry_id = old.entry_id;
                INSERT INTO memory_entries_fts(entry_id, content) VALUES (new.entry_id, new.content);
            END;
            CREATE TRIGGER memory_entries_fts_ad AFTER DELETE ON memory_entries BEGIN
                DELETE FROM memory_entries_fts WHERE entry_id = old.entry_id;
            END;
            INSERT INTO memory_entries_fts(entry_id, content) SELECT entry_id, content FROM memory_entries;
            """;
        command.ExecuteNonQuery();
    }

    private static string Quote(string token) => "\"" + token.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    private static string[] AllScopeIds(Source source, Conversation conversation, User user) =>
    [
        MemoryScope.Id(source, conversation, user, MemoryScopeType.User),
        MemoryScope.Id(source, conversation, user, MemoryScopeType.Conversation),
        MemoryScope.Id(source, conversation, user, MemoryScopeType.Guild),
        MemoryScope.Id(source, conversation, user, MemoryScopeType.Global),
    ];

    private static bool ScopesMatch(string scopeId, string[] scopes)
        => scopes.Contains(scopeId, StringComparer.Ordinal);

    private static string ScopePredicate(MemoryEntryQuery query, string[] scopes, string prefix = "")
        => query.Scope is null
            ? $"{prefix}scope_id IN ($s0, $s1, $s2, $s3)"
            : $"{prefix}scope_type = $scope_type AND {prefix}scope_id = $scope_key";

    private static string TypePredicate(MemoryEntryQuery query, string prefix = "")
    {
        if (query.Types is null || query.Types.Count == 0)
            return string.Empty;

        var values = string.Join(", ", query.Types.Select(type => "'" + MemoryEntryMapping.ToStorageValue(type) + "'"));
        return $"AND {prefix}type IN ({values})";
    }

    private static void AddScopeParameters(SqliteCommand command, MemoryEntryQuery query, string[] scopes)
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

    private static void AddParameters(SqliteCommand command, MemoryEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$type", MemoryEntryMapping.ToStorageValue(entry.Type));
        command.Parameters.AddWithValue("$scope_type", entry.ScopeType.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$scope", entry.ScopeId);
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$hash", entry.ContentHash);
        command.Parameters.AddWithValue("$index", MemoryScoring.BuildIndexText(entry.Content));
        command.Parameters.AddWithValue("$stability", entry.StabilityDays);
        command.Parameters.AddWithValue("$count", entry.ReinforceCount);
        command.Parameters.AddWithValue("$confidence", entry.Confidence);
        command.Parameters.AddWithValue("$archived", entry.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$supersedes", (object?)entry.SupersedesId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sensitivity", entry.Sensitivity);
        command.Parameters.AddWithValue("$sources", entry.SourceMessageIds is null
            ? DBNull.Value
            : JsonSerializer.Serialize(entry.SourceMessageIds, JsonOptions));
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$recalled", entry.LastRecalledAt is null
            ? DBNull.Value
            : entry.LastRecalledAt.Value.ToString("O", CultureInfo.InvariantCulture));
    }

    private static MemoryEntry Read(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            MemoryEntryMapping.TryParseStorageValue(reader.GetString(1), out var type) ? type : MemoryType.Fact,
            Enum.TryParse<MemoryScopeType>(reader.GetString(2), ignoreCase: true, out var scopeType)
                ? scopeType
                : MemoryScopeType.User,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDouble(6),
            reader.GetInt32(7),
            reader.GetDouble(8),
            reader.GetInt32(9) != 0,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(11), JsonOptions),
            reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15), CultureInfo.InvariantCulture));
}
