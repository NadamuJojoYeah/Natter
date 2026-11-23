namespace Natter.Application.Chat;

/// <summary>
/// One structured task trace entry. This is the machine-readable form of the lines the chat
/// <c>/status</c> command prints, so the console never has to parse text (task-65).
/// </summary>
/// <param name="Kind">One of <see cref="TaskTraceKinds"/>: started / step / tool / final.</param>
/// <param name="At">When the event was appended (store clock, UTC).</param>
/// <param name="Name">Tool or step name when the payload carried one.</param>
/// <param name="Code">Stable code (error or transition code) when present.</param>
/// <param name="Step">Step ordinal when present.</param>
/// <param name="Status">Status reported by the event when present.</param>
public sealed record AgentTaskTraceEntry(
    string Kind,
    DateTimeOffset At,
    string? Name = null,
    string? Code = null,
    int? Step = null,
    string? Status = null);

/// <summary>Task-level header: the fields <c>/status</c> prints before its trace lines.</summary>
public sealed record AgentTaskTraceHeader(
    string TaskId,
    string SessionId,
    string Source,
    string Status,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long ElapsedMs,
    string? ErrorCode,
    string? ErrorMessage,
    string? ResultText,
    bool ResultTruncated);

/// <summary>A whole trace: header plus ordered entries.</summary>
public sealed record AgentTaskTrace(
    AgentTaskTraceHeader Task,
    IReadOnlyList<AgentTaskTraceEntry> Entries);

/// <summary>Compact task row for the "recent tasks" list (no entries).</summary>
public sealed record AgentTaskSummary(
    string TaskId,
    string Status,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long ElapsedMs,
    string? ErrorCode,
    int Version);

/// <summary>
/// Read-only task inspection port (task-65). The WebConsole plugin consumes it through the admin
/// bridge; it exposes only whitelisted, already-redacted task data (see <see cref="TaskTracePayload"/>),
/// never prompts or tool bodies.
/// </summary>
public interface IAgentTaskInspection
{
    /// <summary>Recent tasks, newest first. Used by the trace list and the overview counters.</summary>
    ValueTask<IReadOnlyList<AgentTaskSummary>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Full trace for one task, or null when the task does not exist.</summary>
    ValueTask<AgentTaskTrace?> GetTraceAsync(string taskId, CancellationToken cancellationToken = default);
}
