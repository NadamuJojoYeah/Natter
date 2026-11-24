using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// BUG-P53-REPLAY-ENQUEUE: enqueueing the same AgentTask twice must run it once, count metrics
/// once, never mark a running task Failed and never send the result twice. Version conflicts from
/// an already-advanced task are not failures.
/// </summary>
public sealed class P5ReplayAndIdempotencyTests
{
    [Fact]
    public async Task Double_enqueue_runs_once_and_keeps_metrics_and_status_consistent()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var worker = new CountingWorker(delay: TimeSpan.FromMilliseconds(80));
        var recorder = new RecordingDispatcher();
        await using var dispatcher = Dispatcher(store, worker, metrics, recorder);
        var task = NewTask();

        await store.CreateAsync(task);
        await dispatcher.EnqueueAsync(task);
        await dispatcher.EnqueueAsync(task);
        // A third call while the worker is still running must also be a no-op.
        await dispatcher.EnqueueAsync(task);

        await WaitUntilAsync(() => Task.FromResult(worker.Calls >= 1));
        var completed = await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);

        Assert.Equal(1, worker.Calls);
        Assert.Equal("done", completed.ResultText);
        Assert.Equal(1, metrics.Snapshot().Created);
        Assert.Equal(1, metrics.Snapshot().Completed);
        Assert.Equal(0, metrics.Snapshot().Failed);
        Assert.Equal(0, metrics.Snapshot().Active);

        await WaitUntilAsync(() => Task.FromResult(recorder.Commands.Count >= 1));
        Assert.Single(recorder.Commands);

        // A replay after the terminal state is also a no-op (no second run, no metrics drift).
        await dispatcher.EnqueueAsync(task);
        await Task.Delay(120);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, metrics.Snapshot().Created);
        Assert.Equal(0, metrics.Snapshot().Active);
        Assert.Single(recorder.Commands);
    }

    [Fact]
    public async Task Concurrent_double_enqueue_only_starts_one_run()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var worker = new CountingWorker(delay: TimeSpan.FromMilliseconds(100));
        var recorder = new RecordingDispatcher();
        await using var dispatcher = Dispatcher(store, worker, metrics, recorder);
        var task = NewTask();
        await store.CreateAsync(task);

        await Task.WhenAll(
            dispatcher.EnqueueAsync(task).AsTask(),
            dispatcher.EnqueueAsync(task).AsTask(),
            dispatcher.EnqueueAsync(task).AsTask());

        await WaitUntilAsync(() => Task.FromResult(worker.Calls >= 1));
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);

        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, metrics.Snapshot().Created);
        Assert.Equal(0, metrics.Snapshot().Active);
        Assert.Equal(0, metrics.Snapshot().Failed);
    }

    [Fact]
    public async Task Version_conflict_from_an_advanced_task_is_not_marked_failed()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var worker = new CountingWorker(delay: TimeSpan.FromMilliseconds(40));
        var recorder = new RecordingDispatcher();
        await using var dispatcher = Dispatcher(store, worker, metrics, recorder);
        var task = NewTask();
        await store.CreateAsync(task);

        // Another actor advances the task to Running before this dispatcher gets to transition.
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "other-host", TimeSpan.FromMinutes(1)));
        var running = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);

        await dispatcher.EnqueueAsync(task);
        await Task.Delay(200);

        var current = await store.GetAsync(task.TaskId);
        Assert.Equal(AgentTaskStatus.Running, current!.Status);
        Assert.Equal(running.Version, current.Version);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, metrics.Snapshot().Failed);
        Assert.Equal(1, metrics.Snapshot().Created);
        Assert.Equal(0, metrics.Snapshot().Active);

        // The original owner can still cancel it; the dispatcher did not steal the lease.
        Assert.True(await dispatcher.RequestCancelAsync(task.TaskId, task.ActorId!));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Cancelled, current.Version, errorCode: "cancelled");
        Assert.Equal(AgentTaskStatus.Cancelled, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Enqueue_after_terminal_state_does_not_run_or_count()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var worker = new CountingWorker(TimeSpan.Zero);
        var recorder = new RecordingDispatcher();
        await using var dispatcher = Dispatcher(store, worker, metrics, recorder);
        var task = NewTask();
        await store.CreateAsync(task);
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, task.Version + 1, resultText: "already done");

        await dispatcher.EnqueueAsync(task);
        await Task.Delay(120);

        Assert.Equal(0, worker.Calls);
        Assert.Equal(0, metrics.Snapshot().Created);
        Assert.Equal(0, metrics.Snapshot().Active);
        Assert.Empty(recorder.Commands);
    }

    // ------------------------------------------------------------------ helpers

    private static TaskDispatcher Dispatcher(
        ITaskStore store,
        ITaskWorker worker,
        TaskMetrics metrics,
        IChatCommandDispatcher recorder)
    {
        var profiles = new RuntimeProfileStore(null);
        return new TaskDispatcher(
            store,
            worker,
            new ProfileResolver(profiles),
            profiles,
            recorder,
            new ChatCommandFactory(new TestClock()),
            metrics);
    }

    private static AgentTask NewTask()
        => new()
        {
            TaskId = "task-" + Guid.NewGuid().ToString("N")[..10],
            SessionId = "session",
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = "u1",
            Prompt = "do it",
            Status = AgentTaskStatus.Pending,
        };

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException();
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(20);
        }

        throw new TimeoutException();
    }

    private sealed class CountingWorker(TimeSpan delay) : ITaskWorker
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<TaskResult> ExecuteAsync(
            AgentTask task,
            TaskExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            return new TaskResult("done");
        }
    }

    private sealed class RecordingDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            lock (Commands)
                Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }
}
