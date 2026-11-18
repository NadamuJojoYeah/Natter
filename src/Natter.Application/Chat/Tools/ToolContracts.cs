using Natter.Chat;

namespace Natter.Application.Chat;

[Flags]
public enum ToolCapabilities
{
    None = 0,
    ReadOnly = 1,
    Network = 2,
    FileRead = 4,
    FileWrite = 8,
    Shell = 16,
}

public sealed record ToolRequest(string Input, IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record ToolResult(string Output, string? ErrorCode = null, string? ErrorMessage = null)
{
    public bool Ok => ErrorCode is null;
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolCapabilities Capabilities { get; }
    ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}

public interface IToolPolicy
{
    bool IsAllowed(ITool tool, RuntimeProfile profile);
    int MaxCallsPerTask { get; }
    TimeSpan Timeout { get; }
}

public sealed class ReadOnlyToolPolicy : IToolPolicy
{
    public bool IsAllowed(ITool tool, RuntimeProfile profile)
        => (tool.Capabilities & (ToolCapabilities.Shell | ToolCapabilities.FileWrite)) == 0
           && ((tool.Capabilities & ToolCapabilities.Network) == 0 || profile.Policy.AllowNetworkTools);

    public int MaxCallsPerTask => 3;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

public interface ITaskWorker
{
    ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record TaskExecutionContext(
    RuntimeProfile Profile,
    Conversation SessionConversation,
    IReadOnlyList<MemoryItem> Memories,
    int MaxToolCalls,
    int MaxTokens,
    TimeSpan Timeout);

public interface ITaskDispatcher
{
    ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default);
    ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default);
}
