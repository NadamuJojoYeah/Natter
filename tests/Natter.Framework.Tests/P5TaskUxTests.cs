using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.3 task UX: trace payloads (no prompt/arguments/output/secrets), /status details, /tasks
/// filters and limits, /cancel state messages and the new metrics fields.
/// </summary>
public sealed class P5TaskUxTests
{
    private const string PromptSentinel = "PROMPT_SENTINEL do the thing";
    private const string ToolInputSentinel = "TOOL_INPUT_SENTINEL";
    private const string ToolOutputSentinel = "TOOL_OUTPUT_SENTINEL sk-abcdefgh12345678";

    [Fact]
    public async Task Trace_events_are_ordered_and_never_leak_task_content()
    {
        var (store, metrics) = (new InMemoryTaskStore(), new TaskMetrics());
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("clock", ToolInputSentinel));
        gateway.Script.Enqueue(TaskModelReply.Answer("done"));
        var tool = new TestTool(ToolOutputSentinel);
        var worker = Worker(store, metrics, gateway, tool);

        var result = await worker.ExecuteAsync(NewTask(PromptSentinel), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        var events = await store.GetEventsAsync("task-1");
        var trace = events
            .Where(taskEvent => taskEvent.Kind is TaskTraceKinds.Started or TaskTraceKinds.Step or TaskTraceKinds.Tool or TaskTraceKinds.Final)
            .ToArray();
        Assert.Equal(
            [TaskTraceKinds.Started, TaskTraceKinds.Step, TaskTraceKinds.Tool, TaskTraceKinds.Step, TaskTraceKinds.Final],
            trace.Select(taskEvent => taskEvent.Kind));

        var payloads = string.Join("\n", trace.Select(taskEvent => taskEvent.PayloadJson));
        Assert.DoesNotContain(PromptSentinel, payloads, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolInputSentinel, payloads, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolOutputSentinel, payloads, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh", payloads, StringComparison.Ordinal);
        Assert.DoesNotContain("do the thing", payloads, StringComparison.Ordinal);

        foreach (var taskEvent in trace)
        {
            using var document = JsonDocument.Parse(taskEvent.PayloadJson!);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, new[] { "prompt", "input", "output", "arguments", "result", "secret", "token" });
                Assert.True(
                    property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String or JsonValueKind.True or JsonValueKind.False,
                    $"{taskEvent.Kind}/{property.Name} must be a scalar");
            }
        }

        var toolEvent = JsonDocument.Parse(trace[2].PayloadJson!);
        Assert.Equal("clock", toolEvent.RootElement.GetProperty("name").GetString());
        Assert.True(toolEvent.RootElement.TryGetProperty("ms", out _));
        Assert.True(toolEvent.RootElement.TryGetProperty("chars", out _));
        var finalEvent = JsonDocument.Parse(trace[^1].PayloadJson!);
        Assert.Equal("completed", finalEvent.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, finalEvent.RootElement.GetProperty("tools").GetInt32());

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.ToolCalls);
        Assert.Equal(0, snapshot.ToolTimeouts);
    }

    [Fact]
    public async Task Trace_store_rejects_unsafe_payloads()
    {
        var store = new InMemoryTaskStore();
        await store.CreateAsync(NewTask("prompt"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendEventAsync("task-1", TaskTraceKinds.Tool, """{"prompt":"leak"}""").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendEventAsync("task-1", TaskTraceKinds.Tool, """{"name":"has space and output"}""").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendEventAsync("task-1", TaskTraceKinds.Tool, """{"nested":{"x":1}}""").AsTask());
    }

    [Fact]
    public async Task Append_event_failure_does_not_fail_the_task()
    {
        var inner = new InMemoryTaskStore();
        var store = new FailingTraceStore(inner);
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Answer("done"));
        var worker = Worker(store, new TaskMetrics(), gateway, new TestTool("tick"));

        var result = await worker.ExecuteAsync(NewTask("prompt"), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("done", result.Text);
        Assert.True(store.AppendAttempts > 0);
    }






    [Fact]
    public void Default_profile_is_unchanged()
    {
        var profiles = new RuntimeProfileStore(null);
        Assert.Equal("tier1-default", profiles.Current.DefaultProfile);
        Assert.Equal("host#static", profiles.Current.Profiles.Single(profile => profile.Id == "tier1-default").Prompt.Provider);
    }

    // ------------------------------------------------------------------ helpers

    private static AiTaskWorker Worker(ITaskStore store, TaskMetrics metrics, FakeGateway gateway, ITool tool)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(metrics);
        var responders = new ChatResponderRegistry();
        responders.Register(new ChatResponderRegistration("probe", 0, "test", () => gateway));
        services.AddSingleton<IChatResponderRegistry>(responders);
        var provider = services.BuildServiceProvider();
        var policy = new ReadOnlyToolPolicy();
        var registry = new ToolRegistry(new[] { tool });
        return new AiTaskWorker(provider, new[] { tool }, policy, new ToolLoopOptions(), registry, new ToolInvoker(registry, policy));
    }

    private static ChatCommandRouter Router(ITaskStore store, ITaskDispatcher dispatcher)
        => new(
            new InMemoryMemoryStore(),
            new InMemorySummaryStore(),
            new InMemoryConversationStore(),
            new ChatCommandFactory(new TestClock()),
            tasks: store,
            taskDispatcher: dispatcher,
            toolLoopOptions: new ToolLoopOptions());

    private async Task<string> ReplyText(
        ChatCommandRouter router,
        string command,
        (string Key, string Value)? option,
        params string[] values)
    {
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        var options = option is { } pair
            ? new Dictionary<string, string> { [pair.Key] = pair.Value }
            : new Dictionary<string, string>();
        var interaction = new Interaction(
            "i1",
            InteractionKind.Slash,
            user,
            conversation,
            DateTimeOffset.UtcNow,
            command,
            options.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value)),
            values.Length == 0 ? null : values.ToList(),
            "reply-token");
        var chatEvent = new InteractionCreatedEvent
        {
            Id = "evt:" + Guid.NewGuid().ToString("N"),
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new InteractionPayload(interaction),
        };

        var reply = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(chatEvent));
        return reply.Payload.Content.Text ?? string.Empty;
    }

    private static async Task SeedTraceAsync(ITaskStore store, string taskId, int steps, int tools)
    {
        await store.AppendEventAsync(taskId, TaskTraceKinds.Started,
            TaskTracePayload.Build(("status", "running"), ("budget", 3)));

        for (var step = 1; step <= steps; step++)
        {
            await store.AppendEventAsync(taskId, TaskTraceKinds.Step,
                TaskTracePayload.Build(("step", step), ("protocol", "text"), ("toolCall", step < steps)));
        }

        for (var tool = 0; tool < tools; tool++)
        {
            await store.AppendEventAsync(taskId, TaskTraceKinds.Tool,
                TaskTracePayload.Build(("name", "clock"), ("ms", 5), ("code", "tool_ok"), ("chars", 4), ("truncated", false)));
        }
    }

    private static AgentTask NewTask(string prompt)
        => new()
        {
            TaskId = "task-1",
            SessionId = "session",
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = "u1",
            Prompt = prompt,
            Status = AgentTaskStatus.Running,
        };

    private static AgentTask OwnedTask(
        string id,
        string actor,
        AgentTaskStatus status,
        string session = "session",
        string? errorCode = null,
        string? errorMessage = null)
        => new()
        {
            TaskId = id,
            SessionId = session,
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = actor,
            Prompt = PromptSentinel,
            Status = status,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static TaskExecutionContext Context()
        => new(
            new RuntimeProfile
            {
                Id = "p",
                Ai = new ProfileAi { Provider = "test#probe" },
            },
            new Conversation("c1", ConversationKind.Direct),
            [],
            3,
            8_000,
            TimeSpan.FromSeconds(5));

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

    private sealed class FakeGateway : IChatResponder, ITaskModelGateway
    {
        public Queue<TaskModelReply> Script { get; } = new();

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ChatCommand?>(null);

        public ValueTask<TaskModelReply> CompleteStepAsync(TaskModelStep step, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Script.Count > 0 ? Script.Dequeue() : TaskModelReply.Answer("final"));
    }

    private sealed class TestTool(string output) : ITool
    {
        public string Name => "clock";

        public string Description => "Returns a fixed value";

        public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

        public int Calls { get; private set; }

        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new ToolResult(output));
        }
    }

    private sealed class FailingTraceStore(ITaskStore inner) : ITaskStore
    {
        public int AppendAttempts { get; private set; }

        public ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default)
            => inner.CreateAsync(task, cancellationToken);

        public ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
            => inner.GetAsync(taskId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.ListActiveAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListRecoverableAsync(CancellationToken cancellationToken = default)
            => inner.ListRecoverableAsync(cancellationToken);

        public ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default)
            => inner.TryAcquireLeaseAsync(taskId, owner, ttl, cancellationToken);

        public ValueTask<AgentTask> TransitionAsync(string taskId, AgentTaskStatus next, int expectedVersion, string? resultText = null, string? errorCode = null, string? errorMessage = null, CancellationToken cancellationToken = default)
            => inner.TransitionAsync(taskId, next, expectedVersion, resultText, errorCode, errorMessage, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
            => inner.ClaimExpiredLeasesAsync(now, cancellationToken);

        public ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default)
            => inner.SaveResultAsync(taskId, resultText, cancellationToken);

        public ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default)
            => inner.GetEventsAsync(taskId, cancellationToken);

        public ValueTask AppendEventAsync(string taskId, string kind, string? payloadJson, CancellationToken cancellationToken = default)
        {
            AppendAttempts++;
            throw new InvalidOperationException("trace store down");
        }
    }

    private sealed class FakeTaskDispatcher : ITaskDispatcher
    {
        public bool CancelResult { get; set; }

        public int CancelCalls { get; private set; }

        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(task.TaskId);

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return ValueTask.FromResult(CancelResult);
        }
    }

    private sealed class ImmediateWorker(string text) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(text));
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
