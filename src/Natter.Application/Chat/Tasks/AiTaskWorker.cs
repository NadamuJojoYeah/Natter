using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Natter.Application.Admin;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// AI-backed worker. When the routed provider responder implements <see cref="ITaskModelGateway"/>
/// it runs the bounded P5.x tool loop (model → tool → model), negotiating native function calling
/// against the text fallback; both transports execute tools through <see cref="ToolInvoker"/>.
/// Otherwise it keeps the legacy single-step behaviour: send a synthetic event through the
/// profile-aware responder, extract the text and never dispatch that command itself. The
/// deterministic read-only tool worker remains the last fallback when no AI provider is available.
/// </summary>
public sealed class AiTaskWorker(
    IServiceProvider services,
    IEnumerable<ITool> tools,
    IToolPolicy policy,
    ToolLoopOptions? loopOptions = null,
    IToolRegistry? toolRegistry = null,
    ToolInvoker? toolInvoker = null) : ITaskWorker
{
    private readonly IReadOnlyList<ITool> _tools = tools.ToArray();
    private readonly IToolPolicy _policy = policy;
    private readonly ToolLoopOptions _loopOptions = loopOptions ?? new ToolLoopOptions();
    private readonly IToolRegistry _registry = toolRegistry ?? new ToolRegistry(tools);
    private readonly ToolInvoker _invoker = toolInvoker ?? new ToolInvoker(toolRegistry ?? new ToolRegistry(tools), policy);
    private readonly Lazy<IChatResponder> _responder = new(
        () => (IChatResponder)services.GetService(typeof(IChatResponder))!,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<IChatResponderRegistry?> _responders = new(
        () => services.GetService(typeof(IChatResponderRegistry)) as IChatResponderRegistry,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly DefaultTaskWorker _fallback = new(tools, policy);
    private readonly Lazy<ITaskStore?> _stores = new(
        () => services.GetService(typeof(ITaskStore)) as ITaskStore,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<TaskMetrics?> _metrics = new(
        () => services.GetService(typeof(TaskMetrics)) as TaskMetrics,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<ILogger<AiTaskWorker>?> _logger = new(
        () => services.GetService(typeof(ILogger<AiTaskWorker>)) as ILogger<AiTaskWorker>,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var store = _stores.Value;
        var budget = ComputeToolBudget(context);
        var state = new LoopState();
        var startedAt = Stopwatch.GetTimestamp();

        await TraceAsync(
            store,
            task.TaskId,
            TaskTraceKinds.Started,
            TaskTracePayload.Build(("status", "running"), ("budget", budget))).ConfigureAwait(false);

        TaskResult result;
        try
        {
            result = await ExecuteCoreAsync(task, context, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TraceFinalAsync(store, task.TaskId, "cancelled", "cancelled", startedAt, state).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await TraceFinalAsync(store, task.TaskId, "failed", "task_failed", startedAt, state).ConfigureAwait(false);
            throw;
        }

        var status = result.ErrorCode is null ? "completed" : "failed";
        await TraceFinalAsync(store, task.TaskId, status, result.ErrorCode, startedAt, state).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<TaskResult> ExecuteCoreAsync(
        AgentTask task,
        TaskExecutionContext context,
        LoopState state,
        CancellationToken cancellationToken)
    {
        if (ResolveGateway(context.Profile) is { } gateway)
            return await ExecuteToolLoopAsync(task, context, gateway, state, cancellationToken).ConfigureAwait(false);

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
    /// Bounded multi-step loop. The worker owns budgets and observation sanitising; tools are
    /// executed by <see cref="ToolInvoker"/> (policy, permissions, JSON-schema arguments, per-tool
    /// timeout, output bounds, audit). Intermediate observations never touch conversation history
    /// or memory, and the dispatcher remains the single platform sender.
    /// </summary>
    private async ValueTask<TaskResult> ExecuteToolLoopAsync(
        AgentTask task,
        TaskExecutionContext context,
        ITaskModelGateway gateway,
        LoopState state,
        CancellationToken cancellationToken)
    {
        var negotiation = ToolProtocolNegotiator.Negotiate(
            gateway.ProtocolCapabilities,
            _registry.Descriptors);
        if (!negotiation.Ok)
        {
            return new TaskResult(
                null,
                TaskToolLoopCodes.ProtocolUnsupported,
                negotiation.Reason);
        }

        var protocol = negotiation.Protocol;
        var maxToolCalls = ComputeToolBudget(context);
        var maxModelSteps = Math.Max(1, _loopOptions.MaxModelSteps);
        var observationLimit = Math.Max(128, _loopOptions.MaxObservationCharacters);

        var allowed = _registry.Allowed(context.Profile, _policy);
        var descriptors = allowed
            .Select(descriptor => descriptor.ToTaskToolDescriptor())
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
                stepIndex,
                protocol);

            TaskModelReply reply;
            var stepStartedAt = Stopwatch.GetTimestamp();
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

            await TraceAsync(
                _stores.Value,
                task.TaskId,
                TaskTraceKinds.Step,
                TaskTracePayload.Build(
                    ("step", stepIndex),
                    ("protocol", protocol == ToolCallProtocol.Native ? "native" : "text"),
                    ("toolCall", reply.ToolCall is not null),
                    // Real model-call duration: without it a step's cost is only inferable from the
                    // gap between events, which also contains queueing and network time.
                    ("ms", (long)Stopwatch.GetElapsedTime(stepStartedAt).TotalMilliseconds))).ConfigureAwait(false);

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
            remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return BudgetExceeded(maxToolCalls);

            var call = reply.ToolCall;
            var arguments = call.Arguments
                            ?? JsonSerializer.SerializeToElement(new { input = call.Input ?? string.Empty });
            var request = new ToolCallRequest(
                call.Tool,
                arguments,
                CallId: $"task:{task.TaskId}:{stepIndex}",
                RawInput: call.Input);

            ToolCallResult result;
            try
            {
                using var toolBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                toolBudget.CancelAfter(StepTimeout(remaining));
                result = await _invoker.InvokeAsync(
                    request,
                    context.Profile,
                    grantedPermissions: null,
                    protocol,
                    toolBudget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _metrics.Value?.ToolTimedOut();
                return new TaskResult(
                    null,
                    TaskToolLoopCodes.ToolTimeout,
                    $"Tool '{call.Tool}' timed out inside the task step budget.");
            }

            state.ToolsUsed++;
            _metrics.Value?.ToolCalled();
            await TraceToolAsync(task.TaskId, call.Tool, result).ConfigureAwait(false);

            if (result.Ok)
            {
                observations.Add(new TaskToolObservation(
                    call.Tool,
                    call.Input ?? string.Empty,
                    Ok: true,
                    Output: SanitizeObservation(result.Output, observationLimit)));
                continue;
            }

            if (string.Equals(result.ErrorCode, ToolErrorCodes.Timeout, StringComparison.Ordinal))
            {
                _metrics.Value?.ToolTimedOut();
                return new TaskResult(
                    null,
                    TaskToolLoopCodes.ToolTimeout,
                    result.ErrorMessage ?? $"Tool '{call.Tool}' timed out.");
            }

            // Policy/permission rejections surface with the P5.1 observation code so the model can
            // correct itself; argument/unknown failures keep their concrete registry codes.
            var observationCode = result.ErrorCode switch
            {
                ToolErrorCodes.PolicyDenied or ToolErrorCodes.PermissionDenied => TaskToolLoopCodes.ToolNotAllowed,
                _ => result.ErrorCode,
            };

            observations.Add(new TaskToolObservation(
                call.Tool,
                call.Input ?? string.Empty,
                Ok: false,
                Output: SanitizeObservation(result.Output, observationLimit),
                ErrorCode: observationCode,
                ErrorMessage: result.ErrorMessage is null
                    ? null
                    : SanitizeObservation(result.ErrorMessage, observationLimit)));
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

    private int ComputeToolBudget(TaskExecutionContext context)
    {
        var hardCap = Math.Max(1, _loopOptions.MaxToolCallsHardCap);
        var requestedCalls = context.MaxToolCalls <= 0 ? hardCap : context.MaxToolCalls;
        return Math.Clamp(Math.Min(requestedCalls, hardCap), 1, hardCap);
    }

    private async ValueTask TraceFinalAsync(
        ITaskStore? store,
        string taskId,
        string status,
        string? code,
        long startedAt,
        LoopState state)
    {
        var payload = TaskTracePayload.Build(
            ("status", status),
            ("code", code),
            ("ms", (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
            ("tools", state.ToolsUsed));
        await TraceAsync(store, taskId, TaskTraceKinds.Final, payload).ConfigureAwait(false);
    }

    private async ValueTask TraceToolAsync(string taskId, string toolName, ToolCallResult result)
    {
        var payload = TaskTracePayload.Build(
            ("name", toolName),
            ("ms", result.Audit.DurationMs),
            ("code", result.Audit.ErrorCode ?? ToolErrorCodes.Succeeded),
            ("chars", result.Audit.OutputCharacters),
            ("truncated", result.Audit.Truncated));
        await TraceAsync(_stores.Value, taskId, TaskTraceKinds.Tool, payload).ConfigureAwait(false);
    }

    private async ValueTask TraceAsync(ITaskStore? store, string taskId, string kind, string payload)
    {
        if (store is null)
            return;

        try
        {
            await store.AppendEventAsync(taskId, kind, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Trace is best effort: a broken store must never fail the task, and the warning must
            // not contain prompt/argument/output content.
            _logger.Value?.LogWarning(
                "task trace write failed kind={Kind} error={ErrorType}",
                kind,
                ex.GetType().Name);
        }
    }

    private sealed class LoopState
    {
        public int ToolsUsed { get; set; }
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
