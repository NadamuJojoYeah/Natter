using Natter.Chat;
namespace Natter.Application.Chat;

public enum AgentTaskStatus
{
    Pending,
    Running,
    WaitingForInput,
    Completed,
    Failed,
    Cancelled,
    Expired,
}

public sealed record AgentTask
{
    public required string TaskId { get; init; }
    public required string SessionId { get; init; }
    public required Source Source { get; init; }
    public required string ConversationId { get; init; }
    public string? GuildId { get; init; }
    public string? ActorId { get; init; }
    public required string Prompt { get; init; }
    public required AgentTaskStatus Status { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public string? ResultText { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Version { get; init; }
}

public sealed record TaskEvent(
    string TaskId,
    long Seq,
    string Kind,
    string? PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record TaskResult(string? Text, string? ErrorCode = null, string? ErrorMessage = null)
{
    public bool Ok => ErrorCode is null;
}

public interface ITaskStore
{
    ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default);

    ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentTask>> ListRecoverableAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default);

    ValueTask<AgentTask> TransitionAsync(
        string taskId,
        AgentTaskStatus next,
        int expectedVersion,
        string? resultText = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default);
}
