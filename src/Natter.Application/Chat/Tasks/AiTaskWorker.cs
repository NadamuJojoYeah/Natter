using Natter.Application.Admin;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// AI-backed worker. When the routed provider responder implements <see cref="ITaskModelGateway"/>
/// it runs the bounded P5.1 tool loop (model → read-only tool → model, at most
/// <see cref="ToolLoopOptions.MaxToolCallsHardCap"/> tool calls). Otherwise it keeps the legacy
/// single-step behaviour: send a synthetic event through the profile-aware responder, extract the
/// text and never dispatch that command itself. The deterministic read-only tool worker remains the
/// last fallback when no AI provider is available.
/// </summary>
public sealed class AiTaskWorker(
    IServiceProvider services,
    IEnumerable<ITool> tools,
    IToolPolicy policy,
    ToolLoopOptions? loopOptions = null) : ITaskWorker
{
    private readonly IReadOnlyList<ITool> _tools = tools.ToArray();
    private readonly IToolPolicy _policy = policy;
    private readonly ToolLoopOptions _loopOptions = loopOptions ?? new ToolLoopOptions();
    private readonly Lazy<IChatResponder> _responder = new(
        () => (IChatResponder)services.GetService(typeof(IChatResponder))!,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<IChatResponderRegistry?> _responders = new(
        () => services.GetService(typeof(IChatResponderRegistry)) as IChatResponderRegistry,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly DefaultTaskWorker _fallback = new(tools, policy);

    public async ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (ResolveGateway(context.Profile) is { } gateway)
            return await ExecuteToolLoopAsync(task, context, gateway, cancellationToken).ConfigureAwait(false);

        try
        {
            // Task context is its own conversation: AI responder history and summary stay
            // isolated from the user-visible chat session.
            var taskConversation = new Conversation(
                $"{context.SessionConversation.Id}#task:{task.TaskId}",
                context.SessionConversation.Kind,
                context.SessionConversation.GuildId,
                context.SessionConversation.Title);
            var message = new Message(
                task.TaskId,
                taskConversation,
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

    /// <summary>
    /// Bounded multi-step loop. Intermediate observations never touch conversation history or
    /// memory; only the returned <see cref="TaskResult"/> leaves the worker and the dispatcher
    /// remains the single platform sender.
    /// </summary>
    private async ValueTask<TaskResult> ExecuteToolLoopAsync(
        AgentTask task,
        TaskExecutionContext context,
        ITaskModelGateway gateway,
        CancellationToken cancellationToken)
    {
        var hardCap = Math.Max(1, _loopOptions.MaxToolCallsHardCap);
        var requestedCalls = context.MaxToolCalls <= 0 ? hardCap : context.MaxToolCalls;
        var maxToolCalls = Math.Clamp(Math.Min(requestedCalls, hardCap), 1, hardCap);
        var maxModelSteps = Math.Max(1, _loopOptions.MaxModelSteps);
        var observationLimit = Math.Max(128, _loopOptions.MaxObservationCharacters);

        var allowedTools = _tools
            .Where(tool => _policy.IsAllowed(tool, context.Profile))
            .ToArray();
        var descriptors = allowedTools
            .Select(tool => new TaskToolDescriptor(tool.Name, tool.Description))
            .ToArray();

        var deadline = DateTimeOffset.UtcNow + context.Timeout;
        var observations = new List<TaskToolObservation>();
        var toolCalls = 0;

        for (var stepIndex = 0; stepIndex < maxModelSteps; stepIndex++)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return BudgetExceeded(maxToolCalls);

            var step = new TaskModelStep(
                task.TaskId,
                task.Prompt,
                context.Profile.Ai.Model,
                descriptors,
                observations.ToArray(),
                stepIndex);

            TaskModelReply reply;
            try
            {
                using var stepBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stepBudget.CancelAfter(StepTimeout(remaining));
                using var profileScope = ProfileContext.Push(context.Profile);
                reply = await gateway.CompleteStepAsync(step, stepBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new TaskResult(
                    null,
                    TaskToolLoopCodes.ModelStepTimeout,
                    "The AI provider did not answer within the task step budget.");
            }
            catch (Exception ex)
            {
                return new TaskResult(null, TaskToolLoopCodes.ModelStepFailed, Describe(ex));
            }

            if (!reply.Ok)
                return new TaskResult(null, reply.ErrorCode, reply.ErrorMessage ?? "The AI provider failed.");

            if (reply.ToolCall is null)
            {
                var finalText = reply.Text?.Trim();
                return string.IsNullOrWhiteSpace(finalText)
                    ? new TaskResult(null, TaskToolLoopCodes.EmptyResponse, "The model returned no final answer.")
                    : new TaskResult(finalText);
            }

            if (toolCalls >= maxToolCalls)
                return BudgetExceeded(maxToolCalls);

            toolCalls++;
            var call = reply.ToolCall;
            var tool = allowedTools.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, call.Tool, StringComparison.Ordinal));
            if (tool is null)
            {
                observations.Add(new TaskToolObservation(
                    call.Tool,
                    call.Input,
                    Ok: false,
                    Output: string.Empty,
                    ErrorCode: TaskToolLoopCodes.ToolNotAllowed,
                    ErrorMessage: $"Tool '{call.Tool}' is not available under the current task policy."));
                continue;
            }

            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return BudgetExceeded(maxToolCalls);

            ToolResult toolResult;
            try
            {
                using var toolBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                toolBudget.CancelAfter(StepTimeout(remaining));
                toolResult = await tool
                    .ExecuteAsync(new ToolRequest(call.Input), toolBudget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new TaskResult(
                    null,
                    TaskToolLoopCodes.ToolTimeout,
                    $"Tool '{tool.Name}' timed out inside the task step budget.");
            }
            catch (Exception ex)
            {
                toolResult = new ToolResult(string.Empty, TaskToolLoopCodes.ToolFailed, Describe(ex));
            }

            observations.Add(new TaskToolObservation(
                tool.Name,
                call.Input,
                toolResult.Ok,
                SanitizeObservation(toolResult.Output, observationLimit),
                toolResult.ErrorCode,
                toolResult.ErrorMessage is null
                    ? null
                    : SanitizeObservation(toolResult.ErrorMessage, observationLimit)));
        }

        return new TaskResult(
            null,
            TaskToolLoopCodes.ToolLoopFailed,
            $"The tool loop exceeded {maxModelSteps} model step(s) without a final answer.");
    }

    private TimeSpan StepTimeout(TimeSpan remaining)
    {
        var step = _policy.Timeout <= TimeSpan.Zero ? remaining : _policy.Timeout;
        return step < remaining ? step : remaining;
    }

    private static TaskResult BudgetExceeded(int maxToolCalls)
        => new(
            null,
            TaskToolLoopCodes.ToolBudgetExceeded,
            $"The task exceeded the budget of {maxToolCalls} tool call(s).");

    private static string SanitizeObservation(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var redacted = AdminRedactor.Default.Redact(value);
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }

    private static string Describe(Exception exception)
    {
        var message = exception.Message;
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {message}";
    }

    private ITaskModelGateway? ResolveGateway(RuntimeProfile profile)
    {
        if (_responders.Value is not { } registry
            || string.IsNullOrWhiteSpace(profile.Ai.Provider)
            || !registry.TryGet(profile.Ai.Provider, out var registration)
            || registration is null)
        {
            return null;
        }

        try
        {
            return registration.Factory() as ITaskModelGateway;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
