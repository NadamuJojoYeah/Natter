using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S1-05 (conversation lease covers history read/write), S1-06 (bounded, ordered, drainable
/// orchestrator) and S1-07 (terminal-gated task result delivery).
/// </summary>
public sealed class ConcurrencyLifecycleTests
{
    // ================================================================== S1-05

    [Fact]
    public async Task Concurrent_same_conversation_requests_share_one_history_window()
    {
        var history = new TestHistory();
        var chat = new FakeChatClient
        {
            AsyncResponseFactory = async messages =>
            {
                await Task.Delay(40);
                var lastUser = messages.Last(message => message.Role == ChatRole.User).Text;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply:" + lastUser));
            },
        };
        var options = new AiResponderOptions { SystemPrompt = "system", MaxConcurrentRequests = 4 };
        var responder = new AiChatResponder(
            chat,
            options,
            history,
            new AiConversationGate(options),
            new ChatCommandFactory(new TestClock()),
            new FakePluginLog());
        var source = Source.Create("discord", "discord", "bot");

        var first = responder.RespondAsync(MessageEvent("m1", "c1", "alpha", source)).AsTask();
        var second = responder.RespondAsync(MessageEvent("m2", "c1", "beta", source)).AsTask();
        var commands = await Task.WhenAll(first, second);

        Assert.Equal(2, history.Turns.Count);
        Assert.Equal(["alpha", "beta"], history.Turns.Select(turn => turn.User).OrderBy(text => text).ToArray().Order());
        foreach (var turn in history.Turns)
            Assert.Equal("reply:" + turn.User, turn.Assistant);

        Assert.Equal(2, chat.Calls.Count);
        var priorAssistantCounts = chat.Calls
            .Select(call => call.Count(message => message.Role == ChatRole.Assistant && message.Text != call.Last(m => m.Role == ChatRole.User).Text))
            .ToArray();
        // One request saw no previous turn, the other exactly one: no stale shared window.
        Assert.Equal([0, 1], priorAssistantCounts.OrderBy(value => value).ToArray());
        Assert.All(commands, command => Assert.NotNull(command));
    }

    // ================================================================== S1-06

    [Fact]
    public async Task Same_conversation_events_keep_arrival_order()
    {
        var events = new FakeEventSource();
        var responder = new RecordingResponder(async (message, token) =>
        {
            if (message.Id == "m1")
                await Task.Delay(120, token);
            return null;
        });
        using var orchestrator = NewOrchestrator(events, responder, new ChatOrchestrationOptions
        {
            WorkerCount = 2,
            QueueCapacity = 16,
            DrainTimeout = TimeSpan.FromSeconds(5),
        });
        var source = Source.Create("discord", "discord", "bot");
        await orchestrator.StartAsync(EmptyProvider);

        events.Publish(MessageEvent("m1", "c1", "one", source));
        events.Publish(MessageEvent("m2", "c1", "two", source));

        await WaitUntilAsync(() => Task.FromResult(orchestrator.ProcessedCount == 2));
        Assert.Equal(["m1", "m2"], responder.Handled);
        await orchestrator.StopAsync(EmptyProvider);
    }

    [Fact]
    public async Task Different_conversations_are_handled_in_parallel()
    {
        var events = new FakeEventSource();
        var responder = new RecordingResponder(async (_, token) =>
        {
            await Task.Delay(150, token);
            return null;
        });
        using var orchestrator = NewOrchestrator(events, responder, new ChatOrchestrationOptions
        {
            WorkerCount = 2,
            QueueCapacity = 16,
            DrainTimeout = TimeSpan.FromSeconds(5),
        });
        var source = Source.Create("discord", "discord", "bot");
        await orchestrator.StartAsync(EmptyProvider);

        events.Publish(MessageEvent("a1", "conversation-a", "a", source));
        events.Publish(MessageEvent("b1", "conversation-b", "b", source));

        await WaitUntilAsync(() => Task.FromResult(orchestrator.ProcessedCount == 2));
        Assert.True(responder.MaxActive >= 2, $"expected parallel handling, max active was {responder.MaxActive}");
        await orchestrator.StopAsync(EmptyProvider);
    }

    [Fact]
    public async Task Queue_overflow_is_counted_and_logged_instead_of_blocking()
    {
        var events = new FakeEventSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = new RecordingResponder(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        });
        using var orchestrator = NewOrchestrator(events, responder, new ChatOrchestrationOptions
        {
            WorkerCount = 1,
            QueueCapacity = 1,
            DrainTimeout = TimeSpan.FromSeconds(5),
        });
        var source = Source.Create("discord", "discord", "bot");
        await orchestrator.StartAsync(EmptyProvider);

        events.Publish(MessageEvent("blocking", "c1", "hold", source));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 5; index++)
            events.Publish(MessageEvent("overflow-" + index, "c1", "x", source));

        Assert.True(orchestrator.DroppedCount > 0, "queue overflow must be counted, not silently ignored");
        release.TrySetResult();
        await orchestrator.StopAsync(EmptyProvider);
        Assert.True(orchestrator.ProcessedCount >= 1);
    }

    [Fact]
    public async Task StopAsync_drains_in_flight_events_before_returning()
    {
        var events = new FakeEventSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responder = new RecordingResponder(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(100, token);
            return null;
        });
        using var orchestrator = NewOrchestrator(events, responder, new ChatOrchestrationOptions
        {
            WorkerCount = 1,
            QueueCapacity = 8,
            DrainTimeout = TimeSpan.FromSeconds(5),
        });
        var source = Source.Create("discord", "discord", "bot");
        await orchestrator.StartAsync(EmptyProvider);

        events.Publish(MessageEvent("m1", "c1", "one", source));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await orchestrator.StopAsync(EmptyProvider);

        Assert.Equal(1, orchestrator.ProcessedCount);
        Assert.Equal(0, orchestrator.DrainTimeoutCount);
        Assert.Equal(["m1"], responder.Handled);
    }

    [Fact]
    public async Task Handler_exceptions_are_counted_and_do_not_stop_the_queue()
    {
        var events = new FakeEventSource();
        var responder = new RecordingResponder((message, _) =>
        {
            if (message.Id == "boom")
                throw new InvalidOperationException("handler exploded");
            return Task.FromResult<ChatCommand?>(null);
        });
        using var orchestrator = NewOrchestrator(events, responder, new ChatOrchestrationOptions
        {
            WorkerCount = 1,
            QueueCapacity = 8,
            DrainTimeout = TimeSpan.FromSeconds(5),
        });
        var source = Source.Create("discord", "discord", "bot");
        await orchestrator.StartAsync(EmptyProvider);

        events.Publish(MessageEvent("boom", "c1", "x", source));
        events.Publish(MessageEvent("ok", "c2", "y", source));

        await WaitUntilAsync(() => Task.FromResult(orchestrator.FailedCount == 1 && orchestrator.ProcessedCount == 1));
        await orchestrator.StopAsync(EmptyProvider);
    }

    // ================================================================== S1-07

    [Fact]
    public async Task Lease_not_acquired_does_not_send_any_result()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorded = new RecordingCommandDispatcher();
        var worker = new CountingWorker(TimeSpan.Zero);
        await using var dispatcher = NewDispatcher(store, worker, metrics, recorded);
        var task = PendingTask();
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "other-host", TimeSpan.FromMinutes(1)));

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SkippedResults >= 1));

        Assert.Empty(recorded.Commands);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(AgentTaskStatus.Pending, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Version_conflict_does_not_send_any_result()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorded = new RecordingCommandDispatcher();
        var worker = new CountingWorker(TimeSpan.Zero);
        await using var dispatcher = NewDispatcher(store, worker, metrics, recorded);
        var task = PendingTask();
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "other-host", TimeSpan.FromMinutes(1)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, task.Version);

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SkippedResults >= 1));

        Assert.Empty(recorded.Commands);
        Assert.Equal(0, worker.Calls);
        Assert.Equal(AgentTaskStatus.Running, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Completed_task_is_not_resent_on_replay()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorded = new RecordingCommandDispatcher();
        var worker = new CountingWorker(TimeSpan.Zero);
        await using var dispatcher = NewDispatcher(store, worker, metrics, recorded);
        var task = PendingTask();
        await store.CreateAsync(task);

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(recorded.Commands.Count == 1));
        await dispatcher.EnqueueAsync(task);
        await Task.Delay(150);

        Assert.Single(recorded.Commands);
        Assert.Equal(AgentTaskStatus.Completed, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Real_failure_sends_exactly_one_result()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorded = new RecordingCommandDispatcher();
        var worker = new ThrowingWorker();
        await using var dispatcher = NewDispatcher(store, worker, metrics, recorded);
        var task = PendingTask();
        await store.CreateAsync(task);

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(recorded.Commands.Count == 1));
        await Task.Delay(120);

        var command = Assert.IsType<MessageSendCommand>(Assert.Single(recorded.Commands));
        Assert.Equal("Task failed.", command.Payload.Content.Text);
        Assert.Equal(AgentTaskStatus.Failed, (await store.GetAsync(task.TaskId))!.Status);
    }

    [Fact]
    public async Task Failed_result_send_is_compensated_by_the_retry_loop()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorded = new RecordingCommandDispatcher { FailFirstAttempts = 1 };
        var worker = new CountingWorker(TimeSpan.Zero);
        var dispatcher = NewDispatcher(
            store, worker, metrics, recorded, resultSendAttempts: 1, retryInterval: TimeSpan.FromMilliseconds(50));
        await using var _ = dispatcher;
        var task = PendingTask();
        await store.CreateAsync(task);

        await dispatcher.EnqueueAsync(task);
        await WaitUntilAsync(() => Task.FromResult(dispatcher.SentResultCount >= 1 || recorded.Commands.Count >= 1));
        await WaitUntilAsync(() => Task.FromResult(dispatcher.PendingResultCount == 0));

        Assert.Single(recorded.Commands);
        Assert.True(dispatcher.SentResultCount >= 1);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly IServiceProvider EmptyProvider = new ServiceCollection().BuildServiceProvider();

    private static ChatOrchestrator NewOrchestrator(
        FakeEventSource events,
        IChatResponder responder,
        ChatOrchestrationOptions options)
        => new(events, new NoopDispatcher(), responder, options);

    private static TaskDispatcher NewDispatcher(
        ITaskStore store,
        ITaskWorker worker,
        TaskMetrics metrics,
        IChatCommandDispatcher recorded,
        int resultSendAttempts = 3,
        TimeSpan? retryInterval = null)
    {
        var profiles = new RuntimeProfileStore(null);
        return new TaskDispatcher(
            store,
            worker,
            new ProfileResolver(profiles),
            profiles,
            recorded,
            new ChatCommandFactory(new TestClock()),
            metrics,
            resultSendAttempts: resultSendAttempts,
            resultRetryInterval: retryInterval);
    }

    private static AgentTask PendingTask()
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

    private static MessageCreatedEvent MessageEvent(string id, string conversationId, string text, Source source)
        => new()
        {
            Id = id,
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(new Message(
                id,
                new Conversation(conversationId, ConversationKind.Direct),
                new User("u1", "User"),
                new Content(text),
                DateTimeOffset.UtcNow)),
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

    private sealed class TestHistory : IAiConversationStore
    {
        private readonly List<(string User, string Assistant)> _turns = [];

        public IReadOnlyList<(string User, string Assistant)> Turns
        {
            get
            {
                lock (_turns)
                    return _turns.ToArray();
            }
        }

        public string GetKey(Source source, Conversation conversation, string senderId)
            => $"{source.Key}|{conversation.Id}|{senderId}";

        public ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (_turns)
            {
                var messages = new List<ChatMessage>();
                foreach (var (user, assistant) in _turns)
                {
                    messages.Add(new ChatMessage(ChatRole.User, user));
                    messages.Add(new ChatMessage(ChatRole.Assistant, assistant));
                }

                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
            }
        }

        public ValueTask AppendTurnAsync(string key, string userText, string assistantText, CancellationToken cancellationToken = default)
        {
            lock (_turns)
                _turns.Add((userText, assistantText));
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendTurnAsync(string key, string userText, string assistantText, string? sourceEventId, CancellationToken cancellationToken = default)
            => AppendTurnAsync(key, userText, assistantText, cancellationToken);
    }

    private sealed class FakeEventSource : IChatEventSource
    {
        private readonly List<IObserver<ChatEvent>> _observers = [];

        public IObservable<ChatEvent> Events => new Observable(_observers);

        public void Publish(ChatEvent chatEvent)
        {
            IObserver<ChatEvent>[] observers;
            lock (_observers)
                observers = _observers.ToArray();
            foreach (var observer in observers)
                observer.OnNext(chatEvent);
        }

        private sealed class Observable(List<IObserver<ChatEvent>> observers) : IObservable<ChatEvent>
        {
            public IDisposable Subscribe(IObserver<ChatEvent> observer)
            {
                lock (observers)
                    observers.Add(observer);
                return new Subscription(observer, observers);
            }

            private sealed class Subscription(IObserver<ChatEvent> observer, List<IObserver<ChatEvent>> observers) : IDisposable
            {
                public void Dispose()
                {
                    lock (observers)
                        observers.Remove(observer);
                }
            }
        }
    }

    private sealed class RecordingResponder(Func<MessageCreatedEvent, CancellationToken, Task<ChatCommand?>> handler) : IChatResponder
    {
        private readonly List<string> _handled = [];
        private int _active;

        public IReadOnlyList<string> Handled
        {
            get
            {
                lock (_handled)
                    return _handled.ToArray();
            }
        }

        public int MaxActive { get; private set; }

        public async ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            if (chatEvent is not MessageCreatedEvent message)
                return null;

            var active = Interlocked.Increment(ref _active);
            lock (_handled)
            {
                if (active > MaxActive)
                    MaxActive = active;
            }

            try
            {
                var command = await handler(message, cancellationToken).ConfigureAwait(false);
                lock (_handled)
                    _handled.Add(message.Id);
                return command;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class NoopDispatcher : IChatCommandDispatcher
    {
        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatAck.Success(command));
    }

    private sealed class RecordingCommandDispatcher : IChatCommandDispatcher
    {
        private int _attempts;

        public List<ChatCommand> Commands { get; } = [];

        public int FailFirstAttempts { get; init; }

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= FailFirstAttempts)
                throw new InvalidOperationException("dispatch down");

            lock (Commands)
                Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
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

    private sealed class ThrowingWorker : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(
            AgentTask task,
            TaskExecutionContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("worker exploded");
    }
}
