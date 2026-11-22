using System.Text.Json;

namespace Natter.Application.Chat;

public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly Dictionary<string, AgentTask> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskEvent>> _events = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _sequence;

    public ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _tasks[task.TaskId] = task;
            AddEvent(task.TaskId, "created", new { });
            return ValueTask.FromResult(task);
        }
    }

    public ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_tasks.TryGetValue(taskId, out var task) ? task : null);
    }

    public ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<AgentTask>>(_tasks.Values
                .Where(task => task.SessionId == sessionId && task.Status is AgentTaskStatus.Pending or AgentTaskStatus.Running)
                .OrderBy(task => task.CreatedAt)
                .ToArray());
    }

    public ValueTask<IReadOnlyList<AgentTask>> ListRecoverableAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<AgentTask>>(_tasks.Values
                .Where(task => task.Status is AgentTaskStatus.Pending or AgentTaskStatus.Running)
                .OrderBy(task => task.CreatedAt)
                .ToArray());
    }

    public ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task)
                || task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired)
                return ValueTask.FromResult(false);
            if (task.LeaseExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow
                && !string.Equals(task.LeaseOwner, owner, StringComparison.Ordinal))
                return ValueTask.FromResult(false);
            _tasks[taskId] = task with { LeaseOwner = owner, LeaseExpiresAt = DateTimeOffset.UtcNow.Add(ttl), UpdatedAt = DateTimeOffset.UtcNow };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<AgentTask> TransitionAsync(
        string taskId,
        AgentTaskStatus next,
        int expectedVersion,
        string? resultText = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
                throw new InvalidOperationException($"Task '{taskId}' was not found.");
            if (task.Version != expectedVersion)
                throw new InvalidOperationException($"Task '{taskId}' version conflict.");
            if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired)
                throw new InvalidOperationException($"Task '{taskId}' is already terminal.");

            var updated = task with
            {
                Status = next,
                ResultText = resultText ?? task.ResultText,
                ErrorCode = errorCode ?? task.ErrorCode,
                ErrorMessage = errorMessage ?? task.ErrorMessage,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = task.Version + 1,
                LeaseOwner = next is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired ? null : task.LeaseOwner,
                LeaseExpiresAt = next is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired ? null : task.LeaseExpiresAt,
            };
            _tasks[taskId] = updated;
            AddEvent(taskId, next.ToString().ToLowerInvariant(), new { resultText, errorCode, errorMessage });
            return ValueTask.FromResult(updated);
        }
    }

    public ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var expired = _tasks.Values
                .Where(task => task.Status == AgentTaskStatus.Running && task.LeaseExpiresAt is { } expiry && expiry <= now)
                .Select(task => task with
                {
                    Status = AgentTaskStatus.Expired,
                    LeaseOwner = null,
                    LeaseExpiresAt = null,
                    UpdatedAt = now,
                    Version = task.Version + 1,
                })
                .ToArray();
            foreach (var task in expired)
                _tasks[task.TaskId] = task;
            return ValueTask.FromResult<IReadOnlyList<AgentTask>>(expired);
        }
    }

    public ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskId, out var task))
                _tasks[taskId] = task with { ResultText = resultText, UpdatedAt = DateTimeOffset.UtcNow };
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendEventAsync(
        string taskId,
        string kind,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        TaskTracePayload.EnsureSafe(payloadJson);

        lock (_gate)
        {
            AddEventJson(taskId, kind, payloadJson);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<TaskEvent>>(
                _events.TryGetValue(taskId, out var events) ? events.ToArray() : []);
    }

    private void AddEvent(string taskId, string kind, object payload)
        => AddEventJson(taskId, kind, JsonSerializer.Serialize(payload));

    private void AddEventJson(string taskId, string kind, string? payloadJson)
    {
        if (!_events.TryGetValue(taskId, out var events))
        {
            events = [];
            _events[taskId] = events;
        }
        events.Add(new TaskEvent(taskId, ++_sequence, kind, payloadJson, DateTimeOffset.UtcNow));
    }
}
