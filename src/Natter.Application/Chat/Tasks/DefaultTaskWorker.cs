namespace Natter.Application.Chat;

/// <summary>P5.1 deterministic worker used before an AI-backed task worker is wired.</summary>
public sealed class DefaultTaskWorker(IEnumerable<ITool> tools, IToolPolicy policy) : ITaskWorker
{
    private readonly IReadOnlyList<ITool> _tools = tools.ToArray();
    private readonly IToolPolicy _policy = policy;

    public async ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var tool = _tools.FirstOrDefault(item =>
            _policy.IsAllowed(item, context.Profile)
            && (item.Capabilities & ToolCapabilities.ReadOnly) != 0);
        if (tool is null)
            return new TaskResult("No read-only tool is available for this task.", "no_tool", "No allowed tool.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.Timeout);
        try
        {
            var result = await tool.ExecuteAsync(new ToolRequest(task.Prompt), timeout.Token).ConfigureAwait(false);
            return result.Ok
                ? new TaskResult($"{task.Prompt}\n\n[{tool.Name}] {result.Output}")
                : new TaskResult(null, result.ErrorCode, result.ErrorMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TaskResult(null, "timeout", "Task tool timed out.");
        }
        catch (Exception ex)
        {
            return new TaskResult(null, "tool_failed", ex.Message);
        }
    }
}
