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
    TimeSpan? resultRetryInterval = null,
    TimeSpan? shutdownTimeout = null,
    int maxSentResults = 4096,
    int maxPendingResults = 1024) : ITaskDispatcher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _tracked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _resultsSent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingResults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _resultsSentOrder = new();
    private readonly ConcurrentQueue<string> _pendingOrder = new();
    private readonly CancellationTokenSource _retryCancellation = new();
    private readonly TimeSpan _retryInterval = resultRetryInterval ?? TimeSpan.FromSeconds(15);
    private readonly int _sendAttempts = Math.Max(1, resultSendAttempts);
    private readonly string _owner = $"host:{Environment.ProcessId}";
    private int _retryLoopStarted;
    private long _skippedResults;
    private long _abandonedOnDispose;
    private readonly int _maxSentResults = Math.Max(1, maxSentResults);
    private readonly int _maxPendingResults = Math.Max(1, maxPendingResults);
    private readonly TimeSpan _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
    // Lease must cover the worker timeout; a 2m lease would let the watchdog expire a
    // legitimate 5m task before it can finish.
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LeaseTtl = TaskTimeout + TimeSpan.FromMinutes(1);

    /// <summary>Results intentionally not sent because this dispatcher never reached the terminal state.</summary>
    public long SkippedResults => Interlocked.Read(ref _skippedResults);

    /// <summary>Results waiting for the background compensation loop.</summary>
    public int PendingResultCount => _pendingResults.Count;

    /// <summary>Results delivered successfully at least once.</summary>
    public int SentResultCount => _resultsSent.Count;

    /// <summary>Tasks currently tracked as active by this dispatcher (bounded by running tasks).</summary>
    public int TrackedCount => _tracked.Count;

    /// <summary>Worker executions currently in flight.</summary>
    public int RunningWorkerCount => _running.Count;

    /// <summary>Workers still running after the shutdown drain timeout.</summary>
    public long AbandonedOnDispose => Interlocked.Read(ref _abandonedOnDispose);

    /// <summary>
    /// Idempotent by task id: a task that is already queued/running (or already terminal in the
    /// store) returns its id without a second run or a second <see cref="TaskMetrics.Created"/>.
    /// </summary>
    public async ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (_cancellations.ContainsKey(task.TaskId))
            return task.TaskId;

        var current = await store.GetAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
        if (current is null || IsTerminal(current.Status))
            return task.TaskId;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TaskTimeout);
        if (!_cancellations.TryAdd(task.TaskId, cts))
        {
            cts.Dispose();
            return task.TaskId;
        }

        // Created is bound to the first successful enqueue only; the terminal accounting happens
        // exactly once when this dispatcher observes/finishes the task.
        _tracked[task.TaskId] = 0;
        metrics?.Created();
        var run = Task.Run(() => ExecuteAsync(current, cts.Token), CancellationToken.None);
        _running[task.TaskId] = run;
        return task.TaskId;
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
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The worker finished between the lookup and the cancel; nothing left to cancel.
            }
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Take ownership per entry so a finishing worker cannot dispose the CTS concurrently with
        // Cancel (ObjectDisposedException in host shutdown, OBS-P53-FLAKY-HOST-WIRING).
        foreach (var taskId in _cancellations.Keys)
        {
            if (!_cancellations.TryRemove(taskId, out var cts))
                continue;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }
        _cancellations.Clear();

        // S2-B9: shutdown must not drop in-flight results. Wait for running workers with a bound;
        // anything still running afterwards is counted and logged, never silently ignored.
        var running = _running.Values.ToArray();
        if (running.Length > 0)
        {
            try
            {
                await Task.WhenAll(running).WaitAsync(_shutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var abandoned = _running.Count;
                Interlocked.Add(ref _abandonedOnDispose, abandoned);
                logger?.LogWarning(
                    "Task dispatcher disposed with {Abandoned} worker(s) still running after {Timeout}.",
                    abandoned,
                    _shutdownTimeout);
            }
        }

        try
        {
            _retryCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _retryCancellation.Dispose();
    }

    private async Task ExecuteAsync(AgentTask created, CancellationToken cancellationToken)
    {
        var task = created;
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var terminalReached = false;
        try
        {
            var current = await store.GetAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
            if (current is null || IsTerminal(current.Status))
            {
                ReleaseTracked(task.TaskId);
                return;
            }

            if (!await store.TryAcquireLeaseAsync(task.TaskId, _owner, LeaseTtl, cancellationToken).ConfigureAwait(false))
            {
                // Another owner is running (or just finished) this task: not a failure.
                ReleaseTracked(task.TaskId);
                return;
            }

            try
            {
                task = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, current.Version, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // Version conflict: someone else advanced the task between the read and the
                // transition. Re-read and only fail when it is still ours and non-terminal.
                var latest = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (latest is null || IsTerminal(latest.Status) || !IsOwnedByUs(latest))
                {
                    logger?.LogDebug(ex, "Task {TaskId} was advanced by another actor; skipping this run.", task.TaskId);
                    ReleaseTracked(task.TaskId);
                    return;
                }

                throw;
            }

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
                if (await TryTransitionAsync(task.TaskId, task.Version, AgentTaskStatus.Completed, resultText: result.Text).ConfigureAwait(false))
                    terminalReached = MarkTerminal(task.TaskId, AgentTaskStatus.Completed, startedAt);
            }
            else
            {
                if (await TryTransitionAsync(task.TaskId, task.Version, AgentTaskStatus.Failed,
                        errorCode: result.ErrorCode, errorMessage: result.ErrorMessage).ConfigureAwait(false))
                    terminalReached = MarkTerminal(task.TaskId, AgentTaskStatus.Failed, startedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (current is not null
                    && !IsTerminal(current.Status)
                    && await TryTransitionAsync(current.TaskId, current.Version, AgentTaskStatus.Cancelled,
                        errorCode: "cancelled", errorMessage: "Task cancelled.").ConfigureAwait(false))
                {
                    terminalReached = MarkTerminal(task.TaskId, AgentTaskStatus.Cancelled, startedAt);
                }
                else
                {
                    ReleaseTracked(task.TaskId);
                }
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            // Distinguish "advanced elsewhere" from a real failure before marking Failed.
            try
            {
                var current = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
                if (current is null || IsTerminal(current.Status) || !IsOwnedByUs(current))
                {
                    logger?.LogDebug(ex, "Task {TaskId} was advanced by another actor; not marking failed.", task.TaskId);
                    if (current is null || current.Status != AgentTaskStatus.Expired)
                        ReleaseTracked(task.TaskId);
                    return;
                }

                logger?.LogError(ex, "Task {TaskId} failed", task.TaskId);
                if (await TryTransitionAsync(current.TaskId, current.Version, AgentTaskStatus.Failed,
                        errorCode: "task_failed", errorMessage: "Task failed.").ConfigureAwait(false))
                    terminalReached = MarkTerminal(task.TaskId, AgentTaskStatus.Failed, startedAt);
                else
                    ReleaseTracked(task.TaskId);
            }
            catch
            {
            }
        }
        finally
        {
            // S1-07: only a terminal state reached by this dispatcher may deliver a result. A lease
            // miss, a version conflict or a task advanced elsewhere is "skipped": sending here would
            // map Pending/Running to "Task failed." and poison _resultsSent for the real result.
            if (terminalReached)
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
            }
            else
            {
                Interlocked.Increment(ref _skippedResults);
                logger?.LogDebug("Task {TaskId} did not reach a terminal state here; result send skipped.", task.TaskId);
            }

            _running.TryRemove(task.TaskId, out _);
            if (_cancellations.TryRemove(task.TaskId, out var cts))
                cts.Dispose();
        }
    }

    private static bool IsTerminal(AgentTaskStatus status)
        => status is AgentTaskStatus.Completed
            or AgentTaskStatus.Failed
            or AgentTaskStatus.Cancelled
            or AgentTaskStatus.Expired;

    private bool IsOwnedByUs(AgentTask task)
        => string.Equals(task.LeaseOwner, _owner, StringComparison.Ordinal)
           || (task.LeaseOwner is null && !IsTerminal(task.Status));

    private async ValueTask<bool> TryTransitionAsync(
        string taskId,
        int expectedVersion,
        AgentTaskStatus next,
        string? resultText = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        try
        {
            await store.TransitionAsync(taskId, next, expectedVersion, resultText, errorCode, errorMessage, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Version conflict or already terminal: the task was advanced elsewhere.
            return false;
        }
    }

    private bool MarkTerminal(string taskId, AgentTaskStatus status, long startedAt)
    {
        if (!_tracked.TryRemove(taskId, out _))
            return false;

        metrics?.Duration(System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
        switch (status)
        {
            case AgentTaskStatus.Completed:
                metrics?.Completed();
                break;
            case AgentTaskStatus.Failed:
                metrics?.Failed();
                break;
            case AgentTaskStatus.Cancelled:
                metrics?.Cancelled();
                break;
            default:
                metrics?.Released();
                break;
        }

        return true;
    }

    private void ReleaseTracked(string taskId)
    {
        if (_tracked.TryRemove(taskId, out _))
            metrics?.Released();
    }

    private async ValueTask SendResultOnceAsync(AgentTask task)
    {
        if (_resultsSent.ContainsKey(task.TaskId))
            return;

        // Delivery is retried in-process first; a failure never marks the result as sent, it is
        // queued for the background compensation loop instead (OBS-P5F-2).
        if (await TryDeliverResultAsync(task.TaskId, _sendAttempts, CancellationToken.None).ConfigureAwait(false))
        {
            MarkResultSent(task.TaskId);
            _pendingResults.TryRemove(task.TaskId, out _);
            return;
        }

        MarkResultPending(task.TaskId);
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

    private void MarkResultSent(string taskId)
    {
        _resultsSent[taskId] = 0;
        _resultsSentOrder.Enqueue(taskId);
        // S2-B8: keep the dedupe set bounded; the platform idempotency key still protects a
        // replay after eviction.
        while (_resultsSentOrder.Count > _maxSentResults && _resultsSentOrder.TryDequeue(out var oldest))
            _resultsSent.TryRemove(oldest, out _);
    }

    private void MarkResultPending(string taskId)
    {
        _pendingResults[taskId] = 0;
        _pendingOrder.Enqueue(taskId);
        while (_pendingOrder.Count > _maxPendingResults && _pendingOrder.TryDequeue(out var oldest))
        {
            _pendingResults.TryRemove(oldest, out _);
            logger?.LogWarning(
                "Task dispatcher pending-result capacity ({Capacity}) reached; dropping result for {TaskId}.",
                _maxPendingResults,
                oldest);
        }
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
                            MarkResultSent(taskId);
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
