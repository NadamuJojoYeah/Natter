using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Natter.Plugin.StateVitals.Kernel;

namespace Natter.Plugin.StateVitals.Storage;

/// <summary>
/// The plugin's own SQLite database under <c>&lt;state&gt;/plugins/natter.feature.character.state-vitals/state.db</c>
/// (design §8.1). The host's <c>SqliteSchema</c> stays the single migration owner of
/// <c>natter.db</c>; this file owns its own <c>user_version</c> and never touches the host schema.
///
/// <para>
/// Values are persisted as 1/1000 scaled integers (§12.2) so a threshold decision survives a
/// platform or architecture change. Derived states are never stored: the row only holds vitals,
/// asserted state instances, versions and the last update time.
/// </para>
///
/// <para>
/// Concurrency: one connection per operation, WAL journal, <c>busy_timeout</c>, and a versioned
/// <c>UPDATE ... WHERE version = @expected</c>. A loser gets <c>null</c> (conflict) and recomputes.
/// </para>
/// </summary>
public sealed class SqliteStateVitalsStore : IStateVitalsStore
{
    private const int SchemaVersion = 2;
    private const int BusyTimeoutMilliseconds = 5000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly string _stateId;
    private readonly IReadOnlyDictionary<string, double> _seedValues;
    private readonly string _seedDefinitionVersion;
    private readonly IReadOnlyDictionary<string, string> _seedPackVersions;
    private readonly DateTimeOffset _seedTimestamp;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteStateVitalsStore(
        string databasePath,
        string stateId,
        IReadOnlyDictionary<string, double> seedValues,
        string seedDefinitionVersion,
        IReadOnlyDictionary<string, string> seedPackVersions,
        DateTimeOffset seedTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        ArgumentNullException.ThrowIfNull(seedValues);
        ArgumentNullException.ThrowIfNull(seedPackVersions);

        DatabasePath = Path.GetFullPath(databasePath);
        _stateId = stateId;
        _seedValues = seedValues;
        _seedDefinitionVersion = seedDefinitionVersion;
        _seedPackVersions = seedPackVersions;
        _seedTimestamp = seedTimestamp;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async ValueTask<StateSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<StateCommitResult?> TryCommitAsync(
        StateCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var expected = commit.Expected;
        var nextSeq = expected.NextTransitionSeq;
        var sequenced = new List<SequencedTransition>(commit.Transitions.Count);
        foreach (var transition in commit.Transitions)
            sequenced.Add(new SequencedTransition(nextSeq++, transition));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE state_snapshot
                   SET version = @nextVersion,
                       definition_version = @definitionVersion,
                       pack_versions_json = @packVersions,
                       updated_at = @updatedAt,
                       next_transition_seq = @nextTransitionSeq,
                       values_json = @values,
                       asserted_json = @asserted
                 WHERE state_id = @stateId
                   AND version = @expectedVersion;
                """;
            update.Parameters.AddWithValue("@nextVersion", expected.Version + 1);
            update.Parameters.AddWithValue("@definitionVersion", commit.DefinitionVersion);
            update.Parameters.AddWithValue("@packVersions", SerializePackVersions(commit.PackVersions));
            update.Parameters.AddWithValue("@updatedAt", FormatTimestamp(commit.UpdatedAt));
            update.Parameters.AddWithValue("@nextTransitionSeq", nextSeq);
            update.Parameters.AddWithValue("@values", SerializeValues(commit.Values));
            update.Parameters.AddWithValue("@asserted", SerializeAsserted(commit.AssertedStates));
            update.Parameters.AddWithValue("@stateId", expected.StateId);
            update.Parameters.AddWithValue("@expectedVersion", expected.Version);

            var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                // Another writer won the race: report a conflict instead of silently clobbering.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            foreach (var entry in sequenced)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO state_events (seq, state_id, kind, definition_id, level_id, previous_level_id, value_fixed, at)
                    VALUES (@seq, @stateId, @kind, @definitionId, @levelId, @previousLevelId, @value, @at);
                    """;
                insert.Parameters.AddWithValue("@seq", entry.Seq);
                insert.Parameters.AddWithValue("@stateId", expected.StateId);
                insert.Parameters.AddWithValue("@kind", entry.Transition.Kind);
                insert.Parameters.AddWithValue("@definitionId", entry.Transition.DefinitionId);
                insert.Parameters.AddWithValue("@levelId", (object?)entry.Transition.LevelId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@previousLevelId", (object?)entry.Transition.PreviousLevelId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@value", VitalFixedPoint.ToFixed(entry.Transition.Value));
                insert.Parameters.AddWithValue("@at", FormatTimestamp(commit.UpdatedAt));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var snapshot = new StateSnapshot(
                expected.StateId,
                expected.Version + 1,
                commit.DefinitionVersion,
                commit.PackVersions,
                commit.UpdatedAt,
                nextSeq,
                commit.Values,
                commit.AssertedStates);

            return new StateCommitResult(snapshot, sequenced);
        }
        catch (SqliteException exception) when (IsContention(exception))
        {
            // Write-lock contention is a conflict, not a failure: the caller retries with a re-read.
            return null;
        }
    }

    public async ValueTask<StoredDescription?> GetDescriptionAsync(
        string stateId,
        long transitionSeq,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_id, transition_seq, definition_id, level_id, text, source, text_hash, created_at
              FROM state_descriptions
             WHERE state_id = @stateId AND transition_seq = @transitionSeq;
            """;
        command.Parameters.AddWithValue("@stateId", stateId);
        command.Parameters.AddWithValue("@transitionSeq", transitionSeq);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDescription(reader) : null;
    }

    public async ValueTask<StoredDescription?> FindDescriptionAsync(
        string stateId,
        string definitionId,
        string levelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(levelId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_id, transition_seq, definition_id, level_id, text, source, text_hash, created_at
              FROM state_descriptions
             WHERE state_id = @stateId AND definition_id = @definitionId AND level_id = @levelId
             ORDER BY transition_seq DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@stateId", stateId);
        command.Parameters.AddWithValue("@definitionId", definitionId);
        command.Parameters.AddWithValue("@levelId", levelId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDescription(reader) : null;
    }

    public async ValueTask<StoredDescription> SaveDescriptionAsync(
        StoredDescription description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO state_descriptions
                    (state_id, transition_seq, definition_id, level_id, text, source, text_hash, created_at)
                VALUES (@stateId, @transitionSeq, @definitionId, @levelId, @text, @source, @textHash, @createdAt)
                ON CONFLICT(state_id, transition_seq) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("@stateId", description.StateId);
            insert.Parameters.AddWithValue("@transitionSeq", description.TransitionSeq);
            insert.Parameters.AddWithValue("@definitionId", description.DefinitionId);
            insert.Parameters.AddWithValue("@levelId", description.LevelId);
            insert.Parameters.AddWithValue("@text", description.Text);
            insert.Parameters.AddWithValue("@source", description.Source);
            insert.Parameters.AddWithValue("@textHash", description.TextHash);
            insert.Parameters.AddWithValue("@createdAt", FormatTimestamp(description.CreatedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Read back: when a concurrent writer inserted first, the stored text wins (idempotent).
        var stored = await GetDescriptionAsync(description.StateId, description.TransitionSeq, cancellationToken)
            .ConfigureAwait(false);
        return stored ?? description;
    }

    public async ValueTask<IReadOnlyList<StateEventRecord>> ReadTransitionsAsync(
        string stateId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, kind, definition_id, level_id, previous_level_id, value_fixed, at
              FROM state_events
             WHERE state_id = @stateId
             ORDER BY seq DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@stateId", stateId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

        var records = new List<StateEventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new StateEventRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                VitalFixedPoint.FromFixed(reader.GetInt64(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return records;
    }

    public async ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL;";
                await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = """
                    CREATE TABLE IF NOT EXISTS state_snapshot (
                        state_id            TEXT PRIMARY KEY,
                        version             INTEGER NOT NULL,
                        definition_version  TEXT NOT NULL,
                        pack_versions_json  TEXT NOT NULL,
                        updated_at          TEXT NOT NULL,
                        next_transition_seq INTEGER NOT NULL,
                        values_json         TEXT NOT NULL,
                        asserted_json       TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS state_events (
                        seq               INTEGER NOT NULL,
                        state_id          TEXT NOT NULL,
                        kind              TEXT NOT NULL,
                        definition_id     TEXT NOT NULL,
                        level_id          TEXT NULL,
                        previous_level_id TEXT NULL,
                        value_fixed       INTEGER NOT NULL,
                        at                TEXT NOT NULL,
                        PRIMARY KEY(state_id, seq)
                    );

                    CREATE INDEX IF NOT EXISTS ix_state_events_definition
                        ON state_events(state_id, definition_id, seq DESC);

                    CREATE TABLE IF NOT EXISTS state_descriptions (
                        state_id       TEXT NOT NULL,
                        transition_seq INTEGER NOT NULL,
                        definition_id  TEXT NOT NULL,
                        level_id       TEXT NOT NULL,
                        text           TEXT NOT NULL,
                        source         TEXT NOT NULL,
                        text_hash      TEXT NOT NULL,
                        created_at     TEXT NOT NULL,
                        PRIMARY KEY(state_id, transition_seq)
                    );

                    CREATE INDEX IF NOT EXISTS ix_state_descriptions_level
                        ON state_descriptions(state_id, definition_id, level_id, transition_seq DESC);
                    """;
                await ddl.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Seed the one global character row. INSERT OR IGNORE keeps an existing state intact.
            await using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT OR IGNORE INTO state_snapshot
                        (state_id, version, definition_version, pack_versions_json, updated_at,
                         next_transition_seq, values_json, asserted_json)
                    VALUES (@stateId, 0, @definitionVersion, @packVersions, @updatedAt, 0, @values, '[]');
                    """;
                seed.Parameters.AddWithValue("@stateId", _stateId);
                seed.Parameters.AddWithValue("@definitionVersion", _seedDefinitionVersion);
                seed.Parameters.AddWithValue("@packVersions", SerializePackVersions(_seedPackVersions));
                seed.Parameters.AddWithValue("@updatedAt", FormatTimestamp(_seedTimestamp));
                seed.Parameters.AddWithValue("@values", SerializeValues(_seedValues));
                await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var version = connection.CreateCommand())
            {
                version.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private static async ValueTask<StateSnapshot> ReadSnapshotAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state_id, version, definition_version, pack_versions_json, updated_at,
                   next_transition_seq, values_json, asserted_json
              FROM state_snapshot
             LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("state_snapshot was not seeded; the plugin database is unusable.");

        return new StateSnapshot(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            DeserializePackVersions(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)),
            reader.GetInt64(5),
            DeserializeValues(reader.GetString(6)),
            DeserializeAsserted(reader.GetString(7)));
    }

    private static StoredDescription ReadDescription(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)));

    private static bool IsContention(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6;

    /// <summary>Values are written as 1/1000 integers, sorted by id for a byte-stable payload.</summary>
    private static string SerializeValues(IReadOnlyDictionary<string, double> values)
    {
        var sorted = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in values)
            sorted[pair.Key] = VitalFixedPoint.ToFixed(pair.Value);

        return JsonSerializer.Serialize(sorted, SerializerOptions);
    }

    private static IReadOnlyDictionary<string, double> DeserializeValues(string json)
    {
        var parsed = JsonSerializer.Deserialize<SortedDictionary<string, long>>(json, SerializerOptions);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (parsed is null)
            return values;

        foreach (var pair in parsed)
            values[pair.Key] = VitalFixedPoint.FromFixed(pair.Value);

        return values;
    }

    private static string SerializePackVersions(IReadOnlyDictionary<string, string> versions)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in versions)
            sorted[pair.Key] = pair.Value;

        return JsonSerializer.Serialize(sorted, SerializerOptions);
    }

    private static IReadOnlyDictionary<string, string> DeserializePackVersions(string json)
    {
        var parsed = JsonSerializer.Deserialize<SortedDictionary<string, string>>(json, SerializerOptions);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parsed is null)
            return versions;

        foreach (var pair in parsed)
            versions[pair.Key] = pair.Value;

        return versions;
    }

    private sealed record AssertedPayload(string Id, string Since, string? ExpiresAt);

    private static string SerializeAsserted(IReadOnlyList<AssertedStateInstance> asserted)
    {
        var payload = asserted
            .OrderBy(state => state.StateId, StringComparer.Ordinal)
            .Select(state => new AssertedPayload(
                state.StateId,
                FormatTimestamp(state.Since),
                state.ExpiresAt is { } expiresAt ? FormatTimestamp(expiresAt) : null))
            .ToArray();

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static IReadOnlyList<AssertedStateInstance> DeserializeAsserted(string json)
    {
        var parsed = JsonSerializer.Deserialize<AssertedPayload[]>(json, SerializerOptions);
        if (parsed is null)
            return [];

        return parsed
            .Select(state => new AssertedStateInstance(
                state.Id,
                ParseTimestamp(state.Since),
                state.ExpiresAt is null ? (DateTimeOffset?)null : ParseTimestamp(state.ExpiresAt)))
            .ToArray();
    }

    private static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
