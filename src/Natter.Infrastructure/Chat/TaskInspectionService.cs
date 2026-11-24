using Natter.Application.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Structured task inspection over <see cref="ITaskStore"/> (task-65). The trace entries come from
/// the persisted <c>agent_tasks</c>/<c>task_events</c> tables, so the console reads the same rows the
/// chat <c>/status</c> command renders instead of parsing its text.
/// </summary>
public sealed class TaskInspectionService : IAgentTaskInspection
{
    /// <summary>Result text is clipped in the console; the trace reports that it happened.</summary>
    public const int MaxResultCharacters = 4000;

    private readonly ITaskStore _store;
    private readonly TimeProvider _clock;

    public TaskInspectionService(ITaskStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyList<AgentTaskSummary>> GetRecentAsync(
        int limit,
        AgentTaskFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= AgentTaskFilter.Empty;
        var tasks = await _store.ListRecentAsync(
            Math.Clamp(limit, 1, 200),
            filter.Source,
            filter.Status,
            filter.SessionId,
            filter.Since,
            filter.Until,
            cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        return tasks
            .Select(task => new AgentTaskSummary(
                task.TaskId,
                task.Status.ToString(),
                task.Source.Key,
                task.CreatedAt,
                task.UpdatedAt,
                ElapsedMs(task, now),
                task.ErrorCode,
                task.Version))
            .ToArray();
    }

    public async ValueTask<AgentTaskTrace?> GetTraceAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return null;

        var task = await _store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return null;

        var events = await _store.GetEventsAsync(taskId, cancellationToken).ConfigureAwait(false);
        var entries = events
            .Select(TaskTraceEntry.From)
            .Where(entry => entry.Kind is TaskTraceKinds.Started or TaskTraceKinds.Step
                or TaskTraceKinds.Tool or TaskTraceKinds.Final)
            .Select(entry => new AgentTaskTraceEntry(
                entry.Kind,
                entry.At,
                entry.Name,
                entry.Code,
                entry.Step,
                entry.Status,
                entry.DurationMs))
            .ToArray();

        var now = _clock.GetUtcNow();
        var resultText = task.ResultText;
        var truncated = resultText is { Length: > MaxResultCharacters };

        var header = new AgentTaskTraceHeader(
            task.TaskId,
            task.SessionId,
            task.Source.Key,
            task.Status.ToString(),
            task.Version,
            task.CreatedAt,
            task.UpdatedAt,
            ElapsedMs(task, now),
            task.ErrorCode,
            task.ErrorMessage,
            truncated ? resultText![..MaxResultCharacters] : resultText,
            truncated);

        return new AgentTaskTrace(header, entries);
    }

    private static long ElapsedMs(AgentTask task, DateTimeOffset now)
    {
        var endedAt = task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed
            or AgentTaskStatus.Cancelled
            ? task.UpdatedAt
            : now;

        return Math.Max(0, (long)(endedAt - task.CreatedAt).TotalMilliseconds);
    }
}
