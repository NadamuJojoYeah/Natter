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
    ILogger<TaskDispatcher>? logger = null) : ITaskDispatcher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _resultsSent = new(StringComparer.Ordinal);
    private readonly string _owner = $"host:{Environment.ProcessId}";
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
        await ValueTask.CompletedTask;
    }

    private async Task ExecuteAsync(AgentTask created, CancellationToken cancellationToken)
    {
        var task = created;
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
                metrics?.Completed();
            }
            else
            {
                task = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Failed, task.Version,
                    errorCode: result.ErrorCode, errorMessage: result.ErrorMessage, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                    metrics?.Failed();
            }
            catch
            {
            }
        }
        finally
        {
            await SendResultOnceAsync(task).ConfigureAwait(false);
            if (_cancellations.TryRemove(task.TaskId, out var cts))
                cts.Dispose();
        }
    }

    private async ValueTask SendResultOnceAsync(AgentTask task)
    {
        if (!_resultsSent.TryAdd(task.TaskId, 0))
            return;
        var completed = await store.GetAsync(task.TaskId, CancellationToken.None).ConfigureAwait(false);
        if (completed is null)
            return;
        var text = completed.Status switch
        {
            AgentTaskStatus.Completed => completed.ResultText ?? "Task completed.",
            AgentTaskStatus.Cancelled => "Task cancelled.",
            AgentTaskStatus.Expired => "Task expired.",
            _ => "Task failed.",
        };
        var conversation = new Conversation(
            completed.ConversationId,
            completed.GuildId is null ? ConversationKind.Direct : ConversationKind.Guild,
            completed.GuildId);
        var command = commandFactory.Send(completed.Source, conversation, new Content(text),
            idempotencyKey: $"task-result:{completed.TaskId}");
        await commands.ExecuteAsync(command, CancellationToken.None).ConfigureAwait(false);
    }
}
