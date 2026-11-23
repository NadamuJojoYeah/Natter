using Microsoft.Data.Sqlite;
using Natter.Application.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Single owner of the <c>natter.db</c> schema (S2-B5). Every store that shares the database opens
/// connections through <see cref="Prepare"/> and the version counter is advanced only here.
/// </summary>
internal static class SqliteSchema
{
    public const int CurrentVersion = 8;

    private static void ReportWarning(string message) => MemoryMigrationDiagnostics.Report(message);

    /// <summary>Tokenizer actually used by memory_fts (trigram when the runtime supports it).</summary>
    public static string FtsTokenizer { get; private set; } = "trigram";

    /// <summary>Non-null when the preferred FTS mode was unavailable and we degraded.</summary>
    public static string? FtsWarning { get; private set; }

    /// <summary>pretokenized (default) | trigram | unicode61 (last-resort fallback).</summary>
    public static string FtsMode { get; private set; } = "pretokenized";

    /// <summary>Connection-level pragmas that must be identical for every store.</summary>
    public static void Prepare(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        command.ExecuteNonQuery();
    }

    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        Prepare(connection);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(versionCommand.ExecuteScalar());

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
                CREATE TABLE IF NOT EXISTS messages (
                    message_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id      TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                    role            TEXT NOT NULL,
                    text            TEXT NOT NULL,
                    source_event_id TEXT NULL,
                    created_at      TEXT NOT NULL,
                    UNIQUE(session_id, source_event_id)
                );
                CREATE INDEX IF NOT EXISTS ix_messages_session ON messages(session_id, message_id);
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
                    source_protocol TEXT NULL,
                    source_platform TEXT NULL,
                    source_self_id  TEXT NULL,
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

        if (current < 4)
        {
            // S2-B4: structured Source columns for databases created before this version. The
            // source_key column stays for compatibility, but reads no longer split it.
            if (!ColumnExists(connection, "agent_tasks", "source_protocol"))
                Execute(connection, "ALTER TABLE agent_tasks ADD COLUMN source_protocol TEXT NULL;");
            if (!ColumnExists(connection, "agent_tasks", "source_platform"))
                Execute(connection, "ALTER TABLE agent_tasks ADD COLUMN source_platform TEXT NULL;");
            if (!ColumnExists(connection, "agent_tasks", "source_self_id"))
                Execute(connection, "ALTER TABLE agent_tasks ADD COLUMN source_self_id TEXT NULL;");
            current = 4;
        }

        if (current < 5)
        {
            // P6 §4: FTS5 is host memory capability, so it lives in the single DDL owner. trigram
            // is built into SQLite >= 3.34; when unavailable degrade to unicode61 with a warning
            // instead of failing startup.
            var existing = ExistingFtsSql(connection);
            if (existing is null)
            {
                try
                {
                    Execute(connection, "CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text, tokenize='trigram');");
                    FtsTokenizer = "trigram";
                }
                catch (SqliteException)
                {
                    Execute(connection, "CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text, tokenize='unicode61');");
                    FtsTokenizer = "unicode61";
                    FtsWarning = "memory FTS trigram tokenizer unavailable; degraded to unicode61";
                }
            }
            else if (existing.Contains("trigram", StringComparison.OrdinalIgnoreCase))
            {
                FtsTokenizer = "trigram";
            }
            else
            {
                FtsTokenizer = "unicode61";
            }

            Execute(connection, """
                CREATE TRIGGER IF NOT EXISTS memory_fts_ai AFTER INSERT ON memory_items BEGIN
                    INSERT INTO memory_fts(memory_id, text) VALUES (new.memory_id, new.text);
                END;
                CREATE TRIGGER IF NOT EXISTS memory_fts_au AFTER UPDATE OF text ON memory_items BEGIN
                    DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                    INSERT INTO memory_fts(memory_id, text) VALUES (new.memory_id, new.text);
                END;
                CREATE TRIGGER IF NOT EXISTS memory_fts_ad AFTER DELETE ON memory_items BEGIN
                    DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                END;
                """);
            Execute(connection, """
                INSERT INTO memory_fts(memory_id, text)
                SELECT memory_id, text FROM memory_items
                WHERE memory_id NOT IN (SELECT memory_id FROM memory_fts);
                """);
            current = 5;
        }

        if (current < 6)
        {
            current = UpgradeToPretokenizedFts(connection);
        }

        if (current < 7)
        {
            current = UpgradeToLayeredMemory(connection);
        }

        if (current < 8)
        {
            current = UpgradeToEntryOwnership(connection);
        }

        using var setVersion = connection.CreateCommand();
        setVersion.CommandText = $"PRAGMA user_version = {current};";
        setVersion.ExecuteNonQuery();
    }

    private static int UpgradeToPretokenizedFts(SqliteConnection connection)
    {
        // P6 Q8: trigram cannot index 2-character Chinese words at all. The default is now a
        // pre-tokenized copy column indexed with unicode61; trigram stays as the degraded mode.
        if (!ColumnExists(connection, "memory_items", "text_index"))
            Execute(connection, "ALTER TABLE memory_items ADD COLUMN text_index TEXT NULL;");

        try
        {
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT memory_id, text FROM memory_items WHERE text_index IS NULL OR text_index = '';";
                var updates = new List<(string Id, string Index)>();
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                        updates.Add((reader.GetString(0), MemoryScoring.BuildIndexText(reader.GetString(1))));
                }

                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE memory_items SET text_index = $index WHERE memory_id = $id;";
                var idParameter = update.Parameters.Add("$id", SqliteType.Text);
                var indexParameter = update.Parameters.Add("$index", SqliteType.Text);
                foreach (var (id, index) in updates)
                {
                    idParameter.Value = id;
                    indexParameter.Value = index;
                    update.ExecuteNonQuery();
                }
            }

            Execute(connection, """
                DROP TRIGGER IF EXISTS memory_fts_ai;
                DROP TRIGGER IF EXISTS memory_fts_au;
                DROP TRIGGER IF EXISTS memory_fts_ad;
                DROP TABLE IF EXISTS memory_fts;
                CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text_index, tokenize='unicode61');
                CREATE TRIGGER memory_fts_ai AFTER INSERT ON memory_items BEGIN
                    INSERT INTO memory_fts(memory_id, text_index) VALUES (new.memory_id, new.text_index);
                END;
                CREATE TRIGGER memory_fts_au AFTER UPDATE OF text, text_index ON memory_items BEGIN
                    DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                    INSERT INTO memory_fts(memory_id, text_index) VALUES (new.memory_id, new.text_index);
                END;
                CREATE TRIGGER memory_fts_ad AFTER DELETE ON memory_items BEGIN
                    DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                END;
                INSERT INTO memory_fts(memory_id, text_index)
                SELECT memory_id, text_index FROM memory_items WHERE text_index IS NOT NULL;
                """);
            FtsMode = "pretokenized";
            FtsTokenizer = "unicode61";
            FtsWarning = null;
            return 6;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Backfill failure must never fail startup: degrade to trigram over the raw text.
            FtsWarning = $"memory pre-tokenization backfill failed ({exception.GetType().Name}); degraded to trigram";
            try
            {
                Execute(connection, """
                    DROP TRIGGER IF EXISTS memory_fts_ai;
                    DROP TRIGGER IF EXISTS memory_fts_au;
                    DROP TRIGGER IF EXISTS memory_fts_ad;
                    DROP TABLE IF EXISTS memory_fts;
                    CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text, tokenize='trigram');
                    CREATE TRIGGER memory_fts_ai AFTER INSERT ON memory_items BEGIN
                        INSERT INTO memory_fts(memory_id, text) VALUES (new.memory_id, new.text);
                    END;
                    CREATE TRIGGER memory_fts_au AFTER UPDATE OF text ON memory_items BEGIN
                        DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                        INSERT INTO memory_fts(memory_id, text) VALUES (new.memory_id, new.text);
                    END;
                    CREATE TRIGGER memory_fts_ad AFTER DELETE ON memory_items BEGIN
                        DELETE FROM memory_fts WHERE memory_id = old.memory_id;
                    END;
                    INSERT INTO memory_fts(memory_id, text) SELECT memory_id, text FROM memory_items;
                    """);
                FtsMode = "trigram";
                FtsTokenizer = "trigram";
            }
            catch (SqliteException)
            {
                Execute(connection, """
                    DROP TRIGGER IF EXISTS memory_fts_ai;
                    DROP TRIGGER IF EXISTS memory_fts_au;
                    DROP TRIGGER IF EXISTS memory_fts_ad;
                    DROP TABLE IF EXISTS memory_fts;
                    CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text, tokenize='unicode61');
                    """);
                FtsMode = "unicode61";
                FtsTokenizer = "unicode61";
            }

            return 6;
        }
    }

    /// <summary>
    /// P6.5 (docs/P6_MEMORY_SYSTEM.md §11): layered memory tables. The legacy <c>memory_items</c>
    /// table is deliberately left in place — its rows are copied into <c>memory_entries</c> and it
    /// stays readable for one release so a rollback is a single DROP.
    /// </summary>
    private static int UpgradeToLayeredMemory(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS memory_entries (
                entry_id        TEXT PRIMARY KEY,
                type            TEXT NOT NULL,
                scope_type      TEXT NOT NULL,
                scope_id        TEXT NOT NULL,
                content         TEXT NOT NULL,
                content_hash    TEXT NOT NULL,
                text_index      TEXT NULL,
                stability_days  REAL NOT NULL,
                reinforce_count INTEGER NOT NULL DEFAULT 0,
                confidence      REAL NOT NULL,
                archived        INTEGER NOT NULL DEFAULT 0,
                supersedes_id   TEXT NULL,
                sensitivity     TEXT NOT NULL,
                source_message_ids_json TEXT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                last_recalled_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memory_entries_scope
                ON memory_entries(scope_type, scope_id, archived, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_memory_entries_type
                ON memory_entries(type, archived, updated_at DESC);
            -- Repeat detection (§6.2): the same normalized content in the same scope and type is
            -- the same memory. UNIQUE lets the writer do find-or-reinforce in one round trip.
            CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_entries_repeat
                ON memory_entries(scope_id, type, content_hash);

            -- Trace: indexes the existing messages table, it does not duplicate it (§4, §11).
            -- text_index is populated by the application because FTS cannot tokenize CJK in SQL.
            CREATE TABLE IF NOT EXISTS memory_turns (
                message_id  INTEGER PRIMARY KEY,
                session_id  TEXT NOT NULL,
                scope_key   TEXT NOT NULL,
                text_index  TEXT NOT NULL,
                created_at  TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memory_turns_scope ON memory_turns(scope_key, message_id DESC);

            CREATE TABLE IF NOT EXISTS memory_profiles (
                scope_key   TEXT PRIMARY KEY,
                version     INTEGER NOT NULL,
                summary     TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );

            -- Guarantees extraction is incremental and restartable (§11).
            CREATE TABLE IF NOT EXISTS memory_extraction_state (
                scope_key                  TEXT PRIMARY KEY,
                last_extracted_message_id  INTEGER NOT NULL,
                last_attempt_at            TEXT NULL,
                failure_count              INTEGER NOT NULL DEFAULT 0
            );
            """);

        // Pre-tokenized FTS. unicode61 over the application-built index text is the primary path
        // (v6 §Q8): trigram cannot match two-character Chinese words at all.
        Execute(connection, """
            DROP TRIGGER IF EXISTS memory_entries_fts_ai;
            DROP TRIGGER IF EXISTS memory_entries_fts_au;
            DROP TRIGGER IF EXISTS memory_entries_fts_ad;
            DROP TABLE IF EXISTS memory_entries_fts;
            CREATE VIRTUAL TABLE memory_entries_fts USING fts5(entry_id UNINDEXED, text_index, tokenize='unicode61');
            CREATE TRIGGER memory_entries_fts_ai AFTER INSERT ON memory_entries BEGIN
                INSERT INTO memory_entries_fts(entry_id, text_index) VALUES (new.entry_id, new.text_index);
            END;
            CREATE TRIGGER memory_entries_fts_au AFTER UPDATE OF text_index ON memory_entries BEGIN
                DELETE FROM memory_entries_fts WHERE entry_id = old.entry_id;
                INSERT INTO memory_entries_fts(entry_id, text_index) VALUES (new.entry_id, new.text_index);
            END;
            CREATE TRIGGER memory_entries_fts_ad AFTER DELETE ON memory_entries BEGIN
                DELETE FROM memory_entries_fts WHERE entry_id = old.entry_id;
            END;

            DROP TABLE IF EXISTS memory_turns_fts;
            CREATE VIRTUAL TABLE memory_turns_fts USING fts5(message_id UNINDEXED, text_index, tokenize='unicode61');
            """);

        try
        {
            MigrateLegacyMemoryItems(connection);
        }
        catch (SqliteException exception)
        {
            // Degrade to "v2 empty + warning" instead of failing the whole database open. The v1
            // rows stay untouched in memory_items, so nothing is lost and a later attempt can retry.
            ReportWarning(
                $"memory.migration_failed: v1 rows were not copied into memory_entries ({exception.Message}); "
                + "layered recall starts empty and v1 data is preserved.");
        }

        return 7;
    }

    /// <summary>
    /// Copies v1 <c>memory_items</c> rows into <c>memory_entries</c> as the mapped type. The legacy
    /// id is preserved so exports and cross references stay valid; <c>expires_at</c> in the past
    /// becomes <c>archived</c> because v2 never deletes (§5.3).
    /// </summary>
    private static void MigrateLegacyMemoryItems(SqliteConnection connection)
    {
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT memory_id, scope_type, scope_id, kind, text, confidence, sensitivity,
                   source_message_ids_json, created_at, updated_at, expires_at
            FROM memory_items;
            """;

        var rows = new List<(
            string Id,
            string ScopeType,
            string ScopeId,
            string Kind,
            string Text,
            double Confidence,
            string Sensitivity,
            string? Sources,
            string CreatedAt,
            string UpdatedAt,
            string? ExpiresAt)>();

        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetDouble(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        if (rows.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO memory_entries
                (entry_id, type, scope_type, scope_id, content, content_hash, text_index,
                 stability_days, reinforce_count, confidence, archived, supersedes_id,
                 sensitivity, source_message_ids_json, created_at, updated_at, last_recalled_at)
            VALUES ($id, $type, $scope_type, $scope, $content, $hash, $index,
                    $stability, 0, $confidence, $archived, NULL,
                    $sensitivity, $sources, $created, $updated, NULL);
            """;
        var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
        var typeParameter = insert.Parameters.Add("$type", SqliteType.Text);
        var scopeTypeParameter = insert.Parameters.Add("$scope_type", SqliteType.Text);
        var scopeParameter = insert.Parameters.Add("$scope", SqliteType.Text);
        var contentParameter = insert.Parameters.Add("$content", SqliteType.Text);
        var hashParameter = insert.Parameters.Add("$hash", SqliteType.Text);
        var indexParameter = insert.Parameters.Add("$index", SqliteType.Text);
        var stabilityParameter = insert.Parameters.Add("$stability", SqliteType.Real);
        var confidenceParameter = insert.Parameters.Add("$confidence", SqliteType.Real);
        var archivedParameter = insert.Parameters.Add("$archived", SqliteType.Integer);
        var sensitivityParameter = insert.Parameters.Add("$sensitivity", SqliteType.Text);
        var sourcesParameter = insert.Parameters.Add("$sources", SqliteType.Text);
        var createdParameter = insert.Parameters.Add("$created", SqliteType.Text);
        var updatedParameter = insert.Parameters.Add("$updated", SqliteType.Text);

        var unknownKinds = 0;
        foreach (var row in rows)
        {
            if (!MemoryEntryMapping.IsKnownLegacyKind(row.Kind))
                unknownKinds++;
            var type = MemoryEntryMapping.FromLegacyKind(row.Kind);
            var archived = row.ExpiresAt is not null
                           && DateTimeOffset.TryParse(
                               row.ExpiresAt,
                               System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.RoundtripKind,
                               out var expiresAt)
                           && expiresAt <= now;

            idParameter.Value = row.Id;
            typeParameter.Value = MemoryEntryMapping.ToStorageValue(type);
            scopeTypeParameter.Value = row.ScopeType;
            scopeParameter.Value = row.ScopeId;
            contentParameter.Value = row.Text;
            hashParameter.Value = MemoryEntryMapping.Hash(type, row.ScopeId, row.Text);
            indexParameter.Value = MemoryScoring.BuildIndexText(row.Text);
            stabilityParameter.Value = MemoryStability.InitialDays(type);
            confidenceParameter.Value = Math.Clamp(row.Confidence, 0, 1);
            archivedParameter.Value = archived ? 1 : 0;
            sensitivityParameter.Value = row.Sensitivity;
            sourcesParameter.Value = (object?)row.Sources ?? DBNull.Value;
            createdParameter.Value = row.CreatedAt;
            updatedParameter.Value = row.UpdatedAt;
            insert.ExecuteNonQuery();
        }

        if (unknownKinds > 0)
        {
            // Counted, not logged per row: the kind text comes from old user data and the log must
            // not carry identifiers (§10.2). Mapping is Fact, the most conservative long-lived type.
            ReportWarning(
                $"memory.migration_unknown_kind: {unknownKinds} v1 row(s) had an unrecognized kind and were mapped to fact.");
        }
    }

    /// <summary>
    /// P6.7 follow-up (task-66): restores the "only the creator may archive" guarantee that v1 had
    /// via <c>memory_items.subject_id</c>. The v7 copy did not carry that column over, but it is still
    /// recorded in the v1 table, so the backfill reads attribution instead of guessing it.
    /// </summary>
    private static int UpgradeToEntryOwnership(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "memory_entries", "owner_id"))
            Execute(connection, "ALTER TABLE memory_entries ADD COLUMN owner_id TEXT NULL;");

        Execute(connection, """
            CREATE INDEX IF NOT EXISTS ix_memory_entries_owner ON memory_entries(owner_id);
            """);

        // v1 recorded the author in memory_items.subject_id for every write (SqliteMemoryStore
        // passes request.User.Id). Rows that genuinely have no attribution stay NULL.
        try
        {
            Execute(connection, """
                UPDATE memory_entries
                SET owner_id = (
                    SELECT m.subject_id FROM memory_items m
                    WHERE m.memory_id = memory_entries.entry_id)
                WHERE owner_id IS NULL
                  AND EXISTS (
                    SELECT 1 FROM memory_items m
                    WHERE m.memory_id = memory_entries.entry_id
                      AND m.subject_id IS NOT NULL
                      AND m.subject_id <> '');
                """);
        }
        catch (SqliteException exception)
        {
            // Same policy as the v7 copy: never block startup, keep the migration diagnosable.
            ReportWarning(
                $"memory.owner_backfill_failed: existing entries keep owner_id = NULL ({exception.Message}).");
        }

        return 8;
    }

    private static string? ExistingFtsSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='memory_fts';";
        return command.ExecuteScalar() as string;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
