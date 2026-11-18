using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

public sealed class SqliteTaskStore : ITaskStore
{
    private readonly SqliteContextDatabase _database;

    public SqliteTaskStore(string databasePath) => _database = new SqliteContextDatabase(databasePath);

    public async ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO agent_tasks
                (task_id, session_id, source_key, conversation_id, guild_id, actor_id, prompt, status,
                 lease_owner, lease_expires_at, result_text, error_code, error_message, created_at, updated_at, version)
            VALUES ($id, $session, $source, $conversation, $guild, $actor, $prompt, $status,
                    $lease_owner, $lease_expires, $result, $error_code, $error_message, $created, $updated, $version);
            """;
        AddParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await AddEventAsync(connection, task.TaskId, "created", new { task.Prompt }, cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM agent_tasks WHERE task_id = $id;";
        command.Parameters.AddWithValue("$id", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM agent_tasks WHERE session_id = $session
              AND status IN ('Pending','Running','WaitingForInput') ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var result = new List<AgentTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(Read(reader));
        return result;
    }

    public async ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_tasks SET lease_owner = $owner, lease_expires_at = $expires, updated_at = $now
            WHERE task_id = $id AND status NOT IN ('Completed','Failed','Cancelled','Expired')
              AND (lease_expires_at IS NULL OR lease_expires_at <= $now OR lease_owner = $owner);
            """;
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(ttl).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<AgentTask> TransitionAsync(
        string taskId,
        AgentTaskStatus next,
        int expectedVersion,
        string? resultText = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_tasks SET status = $status, result_text = COALESCE($result, result_text),
                error_code = COALESCE($error_code, error_code), error_message = COALESCE($error_message, error_message),
                updated_at = $updated, version = version + 1,
                lease_owner = CASE WHEN $terminal = 1 THEN NULL ELSE lease_owner END,
                lease_expires_at = CASE WHEN $terminal = 1 THEN NULL ELSE lease_expires_at END
            WHERE task_id = $id AND version = $version
              AND status NOT IN ('Completed','Failed','Cancelled','Expired');
            """;
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$status", next.ToString());
        command.Parameters.AddWithValue("$version", expectedVersion);
        command.Parameters.AddWithValue("$result", (object?)resultText ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$terminal", next is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException($"Task '{taskId}' version conflict or terminal.");
        await AddEventAsync(connection, taskId, next.ToString().ToLowerInvariant(), new { resultText, errorCode, errorMessage }, cancellationToken).ConfigureAwait(false);
        return (await GetAsync(taskId, cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_tasks SET status = 'Expired', updated_at = $now, version = version + 1
            WHERE status = 'Running' AND lease_expires_at IS NOT NULL AND lease_expires_at <= $now
            RETURNING task_id;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        var result = new List<AgentTask>();
        foreach (var id in ids)
            if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is { } task)
                result.Add(task);
        return result;
    }

    public async ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_tasks SET result_text = $result, updated_at = $now WHERE task_id = $id;";
        command.Parameters.AddWithValue("$result", resultText);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, seq, kind, payload_json, created_at FROM task_events WHERE task_id = $id ORDER BY seq;";
        command.Parameters.AddWithValue("$id", taskId);
        var events = new List<TaskEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(new TaskEvent(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return events;
    }

    private static async ValueTask AddEventAsync(SqliteConnection connection, string taskId, string kind, object payload, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO task_events (task_id, seq, kind, payload_json, created_at) VALUES ($id, (SELECT COALESCE(MAX(seq),0)+1 FROM task_events WHERE task_id=$id), $kind, $payload, $now);";
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(SqliteCommand command, AgentTask task)
    {
        command.Parameters.AddWithValue("$id", task.TaskId);
        command.Parameters.AddWithValue("$session", task.SessionId);
        command.Parameters.AddWithValue("$source", task.Source.Key);
        command.Parameters.AddWithValue("$conversation", task.ConversationId);
        command.Parameters.AddWithValue("$guild", (object?)task.GuildId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", (object?)task.ActorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prompt", task.Prompt);
        command.Parameters.AddWithValue("$status", task.Status.ToString());
        command.Parameters.AddWithValue("$lease_owner", (object?)task.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("$lease_expires", task.LeaseExpiresAt?.ToString("O", CultureInfo.InvariantCulture) is { } exp ? exp : DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)task.ResultText ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_code", (object?)task.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)task.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$version", task.Version);
    }

    private static AgentTask Read(SqliteDataReader reader)
    {
        var sourceKey = reader.GetString(2);
        var parts = sourceKey.Split(':', 3);
        var source = Source.Create(parts.Length > 0 ? parts[0] : "unknown", parts.Length > 1 ? parts[1] : "unknown", parts.Length > 2 ? parts[2] : "unknown");
        return new AgentTask
        {
            TaskId = reader.GetString(0),
            SessionId = reader.GetString(1),
            Source = source,
            ConversationId = reader.GetString(3),
            GuildId = reader.IsDBNull(4) ? null : reader.GetString(4),
            ActorId = reader.IsDBNull(5) ? null : reader.GetString(5),
            Prompt = reader.GetString(6),
            Status = Enum.Parse<AgentTaskStatus>(reader.GetString(7)),
            LeaseOwner = reader.IsDBNull(8) ? null : reader.GetString(8),
            LeaseExpiresAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            ResultText = reader.IsDBNull(10) ? null : reader.GetString(10),
            ErrorCode = reader.IsDBNull(11) ? null : reader.GetString(11),
            ErrorMessage = reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            Version = reader.GetInt32(15),
        };
    }
}
