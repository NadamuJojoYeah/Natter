using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Ports;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Host-owned task dispatcher. Workers never write to platform transports directly.</summary>
public sealed class TaskDispatcher(
    ITaskStore store,
    ITaskWorker worker,
    IProfileResolver profiles,
    IRuntimeProfileStore profileStore,
    IChatCommandDispatcher commands,
    ChatCommandFactory commandFactory,
    TaskMetrics? metrics = null,
    ILogger<TaskDispatcher>? logger = null,
    int resultSendAttempts = 3,
    TimeSpan? resultRetryInterval = null) : ITaskDispatcher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _resultsSent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingResults = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _retryCancellation = new();
    private readonly TimeSpan _retryInterval = resultRetryInterval ?? TimeSpan.FromSeconds(15);
    private readonly int _sendAttempts = Math.Max(1, resultSendAttempts);
    private readonly string _owner = $"host:{Environment.ProcessId}";
    private int _retryLoopStarted;
    // Lease must cover the worker timeout; a 2m lease would let the watchdog expire a
    // legitimate 5m task before it can finish.
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LeaseTtl = TaskTimeout + TimeSpan.FromMinutes(1);

    public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TaskTimeout);
        _cancellations[task.TaskId] = cts;
        metrics?.Created();
        _ = Task.Run(() => ExecuteAsync(task, cts.Token), CancellationToken.None);
        return ValueTask.FromResult(task.TaskId);
    }

    public async ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
    {
        var task = await store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null || task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired)
            return false;
        if (!string.IsNullOrWhiteSpace(task.ActorId)
            && !string.Equals(task.ActorId, actorId, StringComparison.Ordinal))
            return false;
        if (_cancellations.TryGetValue(taskId, out var cts))
            cts.Cancel();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _cancellations.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _cancellations.Clear();
        try
        {
            _retryCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _retryCancellation.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task ExecuteAsync(AgentTask created, CancellationToken cancellationToken)
    {
        var task = created;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!await store.TryAcquireLeaseAsync(task.TaskId, _owner, LeaseTtl, cancellationToken).ConfigureAwait(false))
                return;
            task = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var profile = profiles.Resolve(task.Source,
                new Conversation(task.ConversationId, task.GuildId is null ? ConversationKind.Direct : ConversationKind.Guild, task.GuildId),
                new User(task.ActorId ?? "unknown", "Task User"))
                ?? profileStore.Current.Profiles.First();
            var memory = Array.Empty<MemoryItem>();
            var context = new TaskExecutionContext(profile,
                new Conversation(task.ConversationId, task.GuildId is null ? ConversationKind.Direct : ConversationKind.Guild, task.GuildId),
                memory, 3, 8000, TaskTimeout);
            var result = await worker.ExecuteAsync(task, context, cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                task = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, task.Version,
                    resultText: result.Text, cancellationToken: cancellationToken).ConfigureAwait(false);
                metrics?.Duration(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                metrics?.Completed();
            }
            else
            {
                task = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Failed, task.Version,
                    errorCode: result.ErrorCode, errorMessage: result.ErrorMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
                metrics?.Duration(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                metrics?.Failed();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.Status is not (AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired))
                    await store.TransitionAsync(task.TaskId, AgentTaskStatus.Cancelled, current.Version,
                        errorCode: "cancelled", errorMessage: "Task cancelled.", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    metrics?.Duration(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                    metrics?.Cancelled();
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Task {TaskId} failed", task.TaskId);
            try
            {
                var current = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null && current.Status is not (AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled or AgentTaskStatus.Expired))
                    await store.TransitionAsync(task.TaskId, AgentTaskStatus.Failed, current.Version,
                        errorCode: "task_failed", errorMessage: "Task failed.", cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    metrics?.Duration(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                metrics?.Failed();
            }
            catch
            {
            }
        }
        finally
        {
            try
            {
                await SendResultOnceAsync(task).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Task {TaskId} result delivery crashed; result kept for retry.", task.TaskId);
                _pendingResults[task.TaskId] = 0;
                EnsureResultRetryLoop();
            }

            if (_cancellations.TryRemove(task.TaskId, out var cts))
                cts.Dispose();
        }
    }

    private async ValueTask SendResultOnceAsync(AgentTask task)
    {
        if (_resultsSent.ContainsKey(task.TaskId))
            return;

        // Delivery is retried in-process first; a failure never marks the result as sent, it is
        // queued for the background compensation loop instead (OBS-P5F-2).
        if (await TryDeliverResultAsync(task.TaskId, _sendAttempts, CancellationToken.None).ConfigureAwait(false))
        {
            _resultsSent[task.TaskId] = 0;
            _pendingResults.TryRemove(task.TaskId, out _);
            return;
        }

        _pendingResults[task.TaskId] = 0;
        EnsureResultRetryLoop();
    }

    private async ValueTask<bool> TryDeliverResultAsync(string taskId, int attempts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var completed = await store.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
                if (completed is null)
                    return true;

                var text = completed.Status switch
                {
                    AgentTaskStatus.Completed => completed.ResultText ?? "Task completed.",
                    AgentTaskStatus.Cancelled => "Task cancelled.",
                    AgentTaskStatus.Expired => "Task expired.",
                    AgentTaskStatus.Pending or AgentTaskStatus.Running or AgentTaskStatus.WaitingForInput
                        => "Task failed.",
                    _ => "Task failed.",
                };
                var conversation = new Conversation(
                    completed.ConversationId,
                    completed.GuildId is null ? ConversationKind.Direct : ConversationKind.Guild,
                    completed.GuildId);
                var command = commandFactory.Send(completed.Source, conversation, new Content(text),
                    idempotencyKey: $"task-result:{completed.TaskId}");

                // Idempotency keys make a re-send safe when the first attempt failed after the
                // platform accepted the message.
                await commands.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "Task {TaskId} result delivery failed (attempt {Attempt}/{Attempts}).",
                    taskId,
                    attempt,
                    attempts);

                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return false;
    }

    private void EnsureResultRetryLoop()
    {
        if (Interlocked.Exchange(ref _retryLoopStarted, 1) == 1)
            return;

        var token = _retryCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_retryInterval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    foreach (var taskId in _pendingResults.Keys)
                    {
                        if (await TryDeliverResultAsync(taskId, 1, token).ConfigureAwait(false))
                        {
                            _pendingResults.TryRemove(taskId, out _);
                            _resultsSent[taskId] = 0;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // The dispatcher was disposed while the loop was waiting.
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "The task result retry loop stopped unexpectedly.");
            }
        }, CancellationToken.None);
    }
}
