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
        await WaitUntilAsync(() => Task.FromResult(dispatcher.Commands.Count >= 1));
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
        await WaitUntilAsync(() => Task.FromResult(dispatcher.Commands.Count >= 1));
        Assert.Single(dispatcher.Commands);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Expired_lease_claim_clears_lease_fields_in_memory()
    {
        var store = new InMemoryTaskStore();
        var task = NewTask(Source.Create("discord", "discord", "bot"));
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner", TimeSpan.FromMinutes(1)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);

        var expired = await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        var claimed = Assert.Single(expired);
        Assert.Equal(AgentTaskStatus.Expired, claimed.Status);
        Assert.Null(claimed.LeaseOwner);
        Assert.Null(claimed.LeaseExpiresAt);
        var stored = await store.GetAsync(task.TaskId);
        Assert.Null(stored!.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
    }

    [Fact]
    public async Task Expired_lease_claim_clears_lease_fields_in_sqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), "natter-task-" + Guid.NewGuid().ToString("N"), "natter.db");
        var store = new Natter.Infrastructure.Chat.SqliteTaskStore(path);
        var task = NewTask(Source.Create("discord", "discord", "bot"));
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner", TimeSpan.FromMinutes(1)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);

        var expired = await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        var claimed = Assert.Single(expired);
        Assert.Equal(AgentTaskStatus.Expired, claimed.Status);
        Assert.Null(claimed.LeaseOwner);
        Assert.Null(claimed.LeaseExpiresAt);
        var stored = await store.GetAsync(task.TaskId);
        Assert.Null(stored!.LeaseOwner);
        Assert.Null(stored.LeaseExpiresAt);
    }

    [Fact]
    public async Task Result_delivery_is_compensated_after_the_first_attempt_fails()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new FlakyDispatcher(failuresBeforeSuccess: 2);
        var worker = new ImmediateWorker("done");
        var profiles = new RuntimeProfileStore(null);
        var runtime = new TaskDispatcher(
            store,
            worker,
            new ProfileResolver(profiles),
            profiles,
            dispatcher,
            new ChatCommandFactory(new TestClock()),
            resultSendAttempts: 1,
            resultRetryInterval: TimeSpan.FromMilliseconds(40));

        var task = NewTask(Source.Create("discord", "discord", "bot"));
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);

        await WaitUntilAsync(() => Task.FromResult(dispatcher.DeliveredCount >= 1));
        Assert.Equal(3, dispatcher.Attempts);
        Assert.Single(dispatcher.Commands);
        Assert.Equal(
            "done",
            Assert.IsType<MessageSendCommand>(dispatcher.Commands[0]).Payload.Content.Text);
        Assert.Equal(AgentTaskStatus.Completed, (await store.GetAsync(task.TaskId))!.Status);

        // A delivered result is not sent again by the compensation loop.
        await Task.Delay(150);
        Assert.Single(dispatcher.Commands);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Result_delivery_retries_within_the_first_attempt_window()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new FlakyDispatcher(failuresBeforeSuccess: 2);
        var worker = new ImmediateWorker("done");
        var profiles = new RuntimeProfileStore(null);
        var runtime = new TaskDispatcher(
            store,
            worker,
            new ProfileResolver(profiles),
            profiles,
            dispatcher,
            new ChatCommandFactory(new TestClock()),
            resultSendAttempts: 3);

        var task = NewTask(Source.Create("discord", "discord", "bot"));
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);

        await WaitUntilAsync(() => Task.FromResult(dispatcher.DeliveredCount >= 1));
        Assert.Equal(3, dispatcher.Attempts);
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
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider();
        await lifecycle.StartAsync(services);

        // S2-B7: recovery waits for the transport in the background, so the first enqueue is
        // asynchronous; wait for it instead of asserting immediately.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && dispatcher.Enqueued.Count == 0)
            await Task.Delay(20);

        Assert.Single(dispatcher.Enqueued);
        Assert.Equal(pending.TaskId, dispatcher.Enqueued[0].TaskId);
        Assert.Equal(AgentTaskStatus.Expired, (await store.GetAsync(running.TaskId))!.Status);
        await lifecycle.StopAsync(services);
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
    public async Task Ai_worker_uses_the_host_responder_and_never_sends_directly()
    {
        var responder = new EchoResponder();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<IChatResponder>(responder)
            .BuildServiceProvider();
        var worker = new AiTaskWorker(services, [new ClockTool()], new ReadOnlyToolPolicy());
        var source = Source.Create("discord", "discord", "bot");
        var task = NewTask(source);
        var result = await worker.ExecuteAsync(task, new TaskExecutionContext(
            new RuntimeProfile { Id = "p", Ai = new ProfileAi { Provider = "p#r" } },
            new Conversation("c1", ConversationKind.Direct),
            [], 3, 8000, TimeSpan.FromSeconds(5)));
        Assert.True(result.Ok);
        Assert.Equal("ai answer", result.Text);
        Assert.Contains("#task:", responder.ConversationId);
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

    private sealed class FlakyDispatcher(int failuresBeforeSuccess) : IChatCommandDispatcher
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public int Attempts { get; private set; }

        public List<ChatCommand> Commands { get; } = [];

        public int DeliveredCount
        {
            get
            {
                lock (Commands)
                    return Commands.Count;
            }
        }

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
                throw new InvalidOperationException("transport down");

            lock (Commands)
                Commands.Add(command);

            return ValueTask.FromResult(ChatAck.Success(command));
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
    private sealed class EchoResponder : IChatResponder
    {
        public string? ConversationId { get; private set; }

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            ConversationId = chatEvent is MessageCreatedEvent message
                ? message.Payload.Message.Conversation.Id
                : null;
            return ValueTask.FromResult<ChatCommand?>(new MessageSendCommand
            {
                Id = "cmd:echo",
                Source = chatEvent.Source,
                IssuedAt = DateTimeOffset.UtcNow,
                Payload = new MessageSendPayload(new Conversation("c1", ConversationKind.Direct), new Content("ai answer")),
            });
        }
    }

}
