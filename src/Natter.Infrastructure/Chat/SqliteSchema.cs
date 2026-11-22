using Microsoft.Data.Sqlite;
using Natter.Application.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Single owner of the <c>natter.db</c> schema (S2-B5). Every store that shares the database opens
/// connections through <see cref="Prepare"/> and the version counter is advanced only here.
/// </summary>
internal static class SqliteSchema
{
    public const int CurrentVersion = 6;

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
