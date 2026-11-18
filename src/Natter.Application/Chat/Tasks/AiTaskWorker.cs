using Natter.Chat;
namespace Natter.Application.Chat;

/// <summary>
/// AI-backed worker: sends a synthetic chat event through the same ProfileAware responder,
/// extracts the generated text, and never dispatches that command itself. If no provider is
/// available, it degrades to the deterministic read-only tool worker.
/// </summary>
public sealed class AiTaskWorker(
    IServiceProvider services,
    IEnumerable<ITool> tools,
    IToolPolicy policy) : ITaskWorker
{
    private readonly Lazy<IChatResponder> _responder = new(
        () => (IChatResponder)services.GetService(typeof(IChatResponder))!,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly DefaultTaskWorker _fallback = new(tools, policy);

    public async ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new Message(
                task.TaskId,
                context.SessionConversation,
                new User(task.ActorId ?? "task", "Task User"),
                new Content(task.Prompt),
                DateTimeOffset.UtcNow);
            message.EnsureValid();
            var chatEvent = new MessageCreatedEvent
            {
                Id = "task:" + task.TaskId,
                Source = task.Source,
                Sequence = 1,
                OccurredAt = message.CreatedAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                Payload = new MessagePayload(message),
            };
            var command = await _responder.Value.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
            if (command is MessageSendCommand send
                && !string.IsNullOrWhiteSpace(send.Payload.Content.Text))
                return new TaskResult(send.Payload.Content.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall back to the deterministic tool worker; task failure must remain explicit only
            // when the fallback also fails.
        }

        return await _fallback.ExecuteAsync(task, context, cancellationToken).ConfigureAwait(false);
    }
}
