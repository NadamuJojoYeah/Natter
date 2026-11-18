using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class TaskRuntimeTests
{
    [Fact]
    public async Task Task_dispatches_runs_once_and_sends_result()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new RecordingDispatcher();
        var worker = new ImmediateWorker("done");
        var profiles = new RuntimeProfileStore(null);
        var runtime = new TaskDispatcher(
            store, worker, new ProfileResolver(profiles), profiles, dispatcher, new ChatCommandFactory(new TestClock()));
        var source = Source.Create("discord", "discord", "bot");
        var task = NewTask(source);
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);

        var completed = await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
        Assert.Equal("done", completed.ResultText);
        await Task.Delay(100);
        Assert.Single(dispatcher.Commands);
        Assert.Equal("done", Assert.IsType<MessageSendCommand>(dispatcher.Commands[0]).Payload.Content.Text);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_before_terminal_state_marks_cancelled()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new RecordingDispatcher();
        var worker = new BlockingWorker();
        var profiles = new RuntimeProfileStore(null);
        var runtime = new TaskDispatcher(
            store, worker, new ProfileResolver(profiles), profiles, dispatcher, new ChatCommandFactory(new TestClock()));
        var source = Source.Create("discord", "discord", "bot");
        var task = NewTask(source);
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);
        await WaitUntilAsync(async () => (await store.GetAsync(task.TaskId))?.Status == AgentTaskStatus.Running);

        Assert.True(await runtime.RequestCancelAsync(task.TaskId, task.ActorId!));
        var cancelled = await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Cancelled } item ? item : null);
        Assert.Equal("cancelled", cancelled.ErrorCode);
        Assert.Single(dispatcher.Commands);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Lease_and_optimistic_transition_are_enforced()
    {
        var store = new InMemoryTaskStore();
        var source = Source.Create("discord", "discord", "bot");
        var task = NewTask(source);
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "a", TimeSpan.FromMinutes(1)));
        Assert.False(await store.TryAcquireLeaseAsync(task.TaskId, "b", TimeSpan.FromMinutes(1)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, task.Version).AsTask());
        var finished = await store.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, 1, resultText: "ok");
        Assert.Equal(AgentTaskStatus.Completed, finished.Status);
        Assert.Equal(2, finished.Version);
    }

    [Fact]
    public async Task Recovery_requeues_pending_tasks_and_expires_stale_running()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new RecordingTaskDispatcher();
        var source = Source.Create("discord", "discord", "bot");
        var pending = NewTask(source);
        var running = NewTask(source) with { TaskId = "task_running", Prompt = "running" };
        await store.CreateAsync(pending);
        await store.CreateAsync(running);
        Assert.True(await store.TryAcquireLeaseAsync(running.TaskId, "old", TimeSpan.FromMilliseconds(1)));
        await store.TransitionAsync(running.TaskId, AgentTaskStatus.Running, running.Version);
        await Task.Delay(5);

        var lifecycle = new TaskRecoveryLifecycle(store, dispatcher);
        await lifecycle.StartAsync(new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider());
        Assert.Single(dispatcher.Enqueued);
        Assert.Equal(pending.TaskId, dispatcher.Enqueued[0].TaskId);
        Assert.Equal(AgentTaskStatus.Expired, (await store.GetAsync(running.TaskId))!.Status);
    }

    [Fact]
    public async Task Sqlite_task_store_persists_status_and_events()
    {
        var path = Path.Combine(Path.GetTempPath(), "natter-task-" + Guid.NewGuid().ToString("N"), "natter.db");
        var store = new Natter.Infrastructure.Chat.SqliteTaskStore(path);
        var source = Source.Create("discord", "discord", "bot");
        var task = NewTask(source);
        await store.CreateAsync(task);
        var reopened = new Natter.Infrastructure.Chat.SqliteTaskStore(path);
        Assert.NotNull(await reopened.GetAsync(task.TaskId));
        Assert.True(await reopened.TryAcquireLeaseAsync(task.TaskId, "a", TimeSpan.FromMinutes(1)));
        await reopened.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);
        await reopened.TransitionAsync(task.TaskId, AgentTaskStatus.Completed, 1, resultText: "ok");
        var completed = await reopened.GetAsync(task.TaskId);
        Assert.Equal(AgentTaskStatus.Completed, completed!.Status);
        Assert.Equal("ok", completed.ResultText);
        Assert.True((await reopened.GetEventsAsync(task.TaskId)).Count >= 3);
    }

    [Fact]
    public async Task Http_tool_enforces_allowlist_and_network_policy()
    {
        using var http = new HttpClient(new RecordingHttpMessageHandler((_, _) => DeepSeekTestData.Json("ok")));
        var tool = new HttpFetchTool(http, new HashSet<string>(["example.com"], StringComparer.OrdinalIgnoreCase));
        Assert.True((await tool.ExecuteAsync(new ToolRequest("https://example.com"))).Ok);
        var denied = await tool.ExecuteAsync(new ToolRequest("https://other.example"));
        Assert.Equal("host_not_allowed", denied.ErrorCode);

        var policy = new ReadOnlyToolPolicy();
        Assert.False(policy.IsAllowed(tool, new RuntimeProfile { Id = "p" }));
        Assert.True(policy.IsAllowed(tool, new RuntimeProfile
        {
            Id = "p",
            Policy = new ProfilePolicy { AllowNetworkTools = true },
        }));
    }

    [Fact]
    public async Task Clock_tool_is_read_only_and_allowed()
    {
        var tool = new ClockTool();
        Assert.True(tool.Capabilities.HasFlag(ToolCapabilities.ReadOnly));
        var result = await tool.ExecuteAsync(new ToolRequest("time"));
        Assert.True(result.Ok);
        Assert.Contains("T", result.Output);
    }

    private static AgentTask NewTask(Source source) => new()
    {
        TaskId = "task_" + Guid.NewGuid().ToString("N")[..12],
        SessionId = "session",
        Source = source,
        ConversationId = "c1",
        ActorId = "u1",
        Prompt = "do it",
        Status = AgentTaskStatus.Pending,
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    private sealed class ImmediateWorker(string text) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(text));
    }

    private sealed class RecordingTaskDispatcher : ITaskDispatcher
    {
        public List<AgentTask> Enqueued { get; } = [];

        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(task);
            return ValueTask.FromResult(task.TaskId);
        }

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
    }

    private sealed class BlockingWorker : ITaskWorker
    {
        public async ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TaskResult("never");
        }
    }

    private sealed class RecordingDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }
}
