using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai;
using Natter.Feature.Ai.DeepSeek;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.1 bounded tool loop: model → read-only tool → model, at most three tool calls, with step
/// timeouts, a total budget, cancellation, redacted observations and explicit TaskResult errors.
/// </summary>
public sealed class P5ToolLoopTests
{
    [Fact]
    public async Task Tool_loop_runs_model_then_tool_then_model_and_returns_final_text()
    {
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("2026-11-21T00:00:00Z")));
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("clock", "now"));
        gateway.Script.Enqueue(TaskModelReply.Answer("The time is now."));
        var worker = CreateWorker(new[] { clock }, gateway, out var provider);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("The time is now.", result.Text);
        Assert.Equal(1, clock.Calls);
        Assert.Equal("now", clock.LastInput);

        Assert.Equal(2, gateway.Steps.Count);
        Assert.Empty(gateway.Steps[0].Observations);
        Assert.Single(gateway.Steps[1].Observations);
        Assert.Equal("clock", gateway.Steps[1].Observations[0].Tool);
        Assert.True(gateway.Steps[1].Observations[0].Ok);
        Assert.Contains("2026-11-21T00:00:00Z", gateway.Steps[1].Observations[0].Output, StringComparison.Ordinal);
        Assert.Equal("clock", gateway.Steps[1].Tools.Single().Name);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Tool_loop_stops_at_the_hard_cap()
    {
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var gateway = new FakeGateway();
        for (var index = 0; index < 10; index++)
            gateway.Script.Enqueue(TaskModelReply.Call("clock", "now"));

        var worker = CreateWorker(new[] { clock }, gateway, out var provider);
        var context = Context(maxToolCalls: 9);

        var result = await worker.ExecuteAsync(NewTask(), context, CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolBudgetExceeded, result.ErrorCode);
        Assert.Null(result.Text);
        // The hard cap is 3 even when the task asks for more; the 4th model step is the one that
        // learns there is no budget left for another tool call.
        Assert.Equal(3, clock.Calls);
        Assert.Equal(4, gateway.Steps.Count);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Exactly_the_hard_cap_of_tool_calls_still_produces_a_final_answer()
    {
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("clock", "one"));
        gateway.Script.Enqueue(TaskModelReply.Call("clock", "two"));
        gateway.Script.Enqueue(TaskModelReply.Call("clock", "three"));
        gateway.Script.Enqueue(TaskModelReply.Answer("all done"));

        var worker = CreateWorker(new[] { clock }, gateway, out var provider);
        var result = await worker.ExecuteAsync(NewTask(), Context(maxToolCalls: 9), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("all done", result.Text);
        Assert.Equal(3, clock.Calls);
        Assert.Equal(4, gateway.Steps.Count);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Tool_loop_uses_the_lower_context_limit()
    {
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var gateway = new FakeGateway();
        for (var index = 0; index < 5; index++)
            gateway.Script.Enqueue(TaskModelReply.Call("clock", "now"));

        var worker = CreateWorker(new[] { clock }, gateway, out var provider);

        var result = await worker.ExecuteAsync(NewTask(), Context(maxToolCalls: 1), CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolBudgetExceeded, result.ErrorCode);
        Assert.Equal(1, clock.Calls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Tool_outside_the_policy_is_not_executed_and_is_fed_back_as_an_observation()
    {
        var shell = new FakeTool("shell", ToolCapabilities.ReadOnly | ToolCapabilities.Shell, static (_, _) =>
            ValueTask.FromResult(new ToolResult("should not run")));
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("shell", "rm -rf /"));
        gateway.Script.Enqueue(TaskModelReply.Answer("done"));

        var worker = CreateWorker(new[] { shell, clock }, gateway, out var provider);
        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("done", result.Text);
        Assert.Equal(0, shell.Calls);
        // Policy-violating tools are not even advertised to the model.
        Assert.DoesNotContain(gateway.Steps[0].Tools, tool => tool.Name == "shell");
        Assert.Equal(TaskToolLoopCodes.ToolNotAllowed, gateway.Steps[1].Observations[0].ErrorCode);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Tool_timeout_returns_an_explicit_error()
    {
        var slow = new FakeTool("slow", ToolCapabilities.ReadOnly, static async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ToolResult("never");
        });
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("slow", "wait"));

        var worker = CreateWorker(
            new[] { slow },
            gateway,
            out var provider,
            new ReadOnlyToolPolicy { Timeout = TimeSpan.FromMilliseconds(50) });

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolTimeout, result.ErrorCode);
        Assert.Null(result.Text);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Exhausted_total_budget_returns_an_explicit_error()
    {
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Answer("never reached"));
        var worker = CreateWorker(Array.Empty<ITool>(), gateway, out var provider);

        var result = await worker.ExecuteAsync(
            NewTask(),
            Context() with { Timeout = TimeSpan.Zero },
            CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolBudgetExceeded, result.ErrorCode);
        Assert.Empty(gateway.Steps);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_the_model_step()
    {
        var gateway = new FakeGateway
        {
            DelayFactory = static token => Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        gateway.Script.Enqueue(TaskModelReply.Answer("never"));
        var worker = CreateWorker(Array.Empty<ITool>(), gateway, out var provider);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await worker.ExecuteAsync(NewTask(), Context(), cancellation.Token));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Observations_are_redacted_and_truncated_before_reaching_the_model()
    {
        var secret = "token=super-secret-value-123";
        var noisy = new FakeTool("noisy", ToolCapabilities.ReadOnly, (_, _) =>
            ValueTask.FromResult(new ToolResult(secret + new string('x', 5_000))));
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("noisy", "dump"));
        gateway.Script.Enqueue(TaskModelReply.Answer("done"));

        var worker = CreateWorker(new[] { noisy }, gateway, out var provider);
        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        var observation = Assert.Single(gateway.Steps[1].Observations);
        Assert.DoesNotContain("super-secret-value-123", observation.Output, StringComparison.Ordinal);
        Assert.Contains("[redacted]", observation.Output, StringComparison.Ordinal);
        Assert.True(observation.Output.Length <= 2_001, $"observation length {observation.Output.Length}");
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Missing_gateway_keeps_the_legacy_responder_path()
    {
        var tools = Array.Empty<ITool>();
        var legacy = new LegacyResponder();
        var provider = BuildServices(registry => { }, legacy);

        var worker = new AiTaskWorker(provider, tools, new ReadOnlyToolPolicy(), new ToolLoopOptions());
        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("legacy answer", result.Text);
        Assert.Equal(1, legacy.Calls);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task TaskDispatcher_sends_the_final_tool_loop_result_exactly_once()
    {
        var store = new InMemoryTaskStore();
        var recorder = new RecordingCommandDispatcher();
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var gateway = new FakeGateway();
        gateway.Script.Enqueue(TaskModelReply.Call("clock", "now"));
        gateway.Script.Enqueue(TaskModelReply.Answer("loop final answer"));
        var worker = CreateWorker(new[] { clock }, gateway, out var provider);
        var profiles = new RuntimeProfileStore(null);
        var profile = new RuntimeProfile
        {
            Id = "p",
            Ai = new ProfileAi { Provider = "test#probe" },
        };
        await using var dispatcher = new TaskDispatcher(
            store,
            worker,
            new FixedProfileResolver(profile),
            profiles,
            recorder,
            new ChatCommandFactory(new TestClock()));

        var task = NewTask();
        task = task with { Status = AgentTaskStatus.Pending };
        await store.CreateAsync(task);
        await dispatcher.EnqueueAsync(task);

        var completed = await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
        Assert.Equal("loop final answer", completed.ResultText);
        await WaitUntilAsync(() => Task.FromResult(recorder.Commands.Count >= 1));
        var send = Assert.IsType<MessageSendCommand>(Assert.Single(recorder.Commands));
        Assert.Equal("loop final answer", send.Payload.Content.Text);
        Assert.Equal(1, clock.Calls);
        await provider.DisposeAsync();
    }

    // ------------------------------------------------------------------ gateway protocol

    [Fact]
    public async Task Gateway_parses_a_tool_call_and_sends_tool_schema_to_the_model()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "<tool_call>{\"tool\":\"clock\",\"input\":\"now\"}</tool_call>")),
        };
        var responder = BuildResponder(chat, out _);
        var step = new TaskModelStep(
            "task-1",
            "what time is it",
            null,
            [new TaskToolDescriptor("clock", "Returns the current time.")],
            [],
            0);

        var reply = await responder.CompleteStepAsync(step);

        Assert.True(reply.Ok);
        Assert.Equal("clock", reply.ToolCall!.Tool);
        Assert.Equal("now", reply.ToolCall.Input);
        Assert.Null(reply.Text);

        var sent = Assert.Single(chat.Calls);
        Assert.Contains("clock", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("tool_call", sent[0].Text, StringComparison.Ordinal);
        Assert.Equal("what time is it", sent[1].Text);
    }

    [Fact]
    public async Task Gateway_formats_observations_and_returns_the_final_text()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response>done</response>")),
        };
        var responder = BuildResponder(chat, out _);
        var step = new TaskModelStep(
            "task-1",
            "what time is it",
            null,
            [new TaskToolDescriptor("clock", "Returns the current time.")],
            [new TaskToolObservation("clock", "now", true, "2026-11-21T00:00:00Z")],
            1);

        var reply = await responder.CompleteStepAsync(step);

        Assert.True(reply.Ok);
        Assert.Null(reply.ToolCall);
        // Provider wrapper tags are stripped from the final answer (task-12 behaviour).
        Assert.Equal("done", reply.Text);

        var sent = Assert.Single(chat.Calls);
        Assert.Contains("[tool observation] clock (ok)", sent[2].Text, StringComparison.Ordinal);
        Assert.Contains("2026-11-21T00:00:00Z", sent[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_reports_a_malformed_tool_call_as_an_explicit_error()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                "<tool_call>not json</tool_call>")),
        };
        var responder = BuildResponder(chat, out _);

        var reply = await responder.CompleteStepAsync(new TaskModelStep(
            "task-1",
            "prompt",
            null,
            [],
            [],
            0));

        Assert.False(reply.Ok);
        Assert.Equal("tool_call_invalid", reply.ErrorCode);
    }

    [Fact]
    public async Task Gateway_provider_exception_becomes_an_explicit_step_error()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => throw new DeepSeekException(DeepSeekErrorKind.Timeout, "timed out"),
        };
        var responder = BuildResponder(chat, out _);

        var reply = await responder.CompleteStepAsync(new TaskModelStep("task-1", "prompt", null, [], [], 0));

        Assert.False(reply.Ok);
        Assert.Equal(TaskToolLoopCodes.ModelStepTimeout, reply.ErrorCode);
    }

    [Fact]
    public async Task Gateway_and_worker_never_write_conversation_history()
    {
        var calls = 0;
        var chat = new FakeChatClient
        {
            AsyncResponseFactory = _ => Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                Interlocked.Increment(ref calls) == 1
                    ? "<tool_call>{\"tool\":\"clock\",\"input\":\"now\"}</tool_call>"
                    : "final answer"))),
        };
        var responder = BuildResponder(chat, out var history);
        var registryServices = BuildServices(
            registry => registry.Register(new ChatResponderRegistration(
                "probe",
                0,
                "test",
                () => responder)),
            legacy: null);
        var tool = new FakeTool("clock", ToolCapabilities.ReadOnly, static (_, _) =>
            ValueTask.FromResult(new ToolResult("tick")));
        var worker = new AiTaskWorker(
            registryServices,
            new[] { tool },
            new ReadOnlyToolPolicy(),
            new ToolLoopOptions());

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("final answer", result.Text);
        Assert.Equal(2, chat.Calls.Count);
        Assert.Equal(0, history.Appends);
        await registryServices.DisposeAsync();
    }

    // ------------------------------------------------------------------ helpers

    private static AgentTask NewTask()
        => new()
        {
            TaskId = "task-" + Guid.NewGuid().ToString("N")[..8],
            SessionId = "session",
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = "u1",
            Prompt = "what time is it",
            Status = AgentTaskStatus.Running,
        };

    private static TaskExecutionContext Context(int maxToolCalls = 3)
        => new(
            new RuntimeProfile
            {
                Id = "p",
                Ai = new ProfileAi { Provider = "test#probe" },
            },
            new Conversation("c1", ConversationKind.Direct),
            [],
            maxToolCalls,
            8_000,
            TimeSpan.FromSeconds(5));

    private static AiTaskWorker CreateWorker(
        IReadOnlyList<ITool> tools,
        FakeGateway gateway,
        out ServiceProvider provider,
        IToolPolicy? policy = null)
    {
        provider = BuildServices(
            registry => registry.Register(new ChatResponderRegistration("probe", 0, "test", () => gateway)),
            legacy: null);
        return new AiTaskWorker(provider, tools, policy ?? new ReadOnlyToolPolicy(), new ToolLoopOptions());
    }

    private static ServiceProvider BuildServices(
        Action<ChatResponderRegistry> configureRegistry,
        IChatResponder? legacy)
    {
        var services = new ServiceCollection();
        var registry = new ChatResponderRegistry();
        configureRegistry(registry);
        services.AddSingleton<IChatResponderRegistry>(registry);
        if (legacy is not null)
            services.AddSingleton(legacy);
        return services.BuildServiceProvider();
    }

    private static AiChatResponder BuildResponder(FakeChatClient chat, out RecordingAiConversationStore history)
    {
        var options = new AiResponderOptions
        {
            SystemPrompt = "system",
            FallbackText = "AI is temporarily unavailable.",
            MaxConcurrentRequests = 2,
        };
        history = new RecordingAiConversationStore();
        return new AiChatResponder(
            chat,
            options,
            history,
            new AiConversationGate(options),
            new ChatCommandFactory(new TestClock()),
            new FakePluginLog());
    }

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

    private sealed class FixedProfileResolver(RuntimeProfile profile) : IProfileResolver
    {
        public RuntimeProfile? Resolve(Source source, Conversation conversation, User sender)
            => profile;
    }

    private sealed class RecordingCommandDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }

    private sealed class FakeGateway : IChatResponder, ITaskModelGateway
    {
        public Queue<TaskModelReply> Script { get; } = new();

        public List<TaskModelStep> Steps { get; } = [];

        public Func<CancellationToken, Task>? DelayFactory { get; set; }

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ChatCommand?>(null);

        public async ValueTask<TaskModelReply> CompleteStepAsync(
            TaskModelStep step,
            CancellationToken cancellationToken = default)
        {
            Steps.Add(step);
            if (DelayFactory is not null)
                await DelayFactory(cancellationToken);

            return Script.Count > 0 ? Script.Dequeue() : TaskModelReply.Answer("final answer");
        }
    }

    private sealed class LegacyResponder : IChatResponder
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Calls++;
            var source = chatEvent.Source;
            var conversation = (chatEvent as MessageCreatedEvent)?.Payload.Message.Conversation
                               ?? new Conversation("c1", ConversationKind.Direct);
            var command = new ChatCommandFactory(new TestClock()).Send(
                source,
                conversation,
                new Content("legacy answer"));
            return ValueTask.FromResult<ChatCommand?>(command);
        }
    }

    private sealed class FakeTool(
        string name,
        ToolCapabilities capabilities,
        Func<ToolRequest, CancellationToken, ValueTask<ToolResult>> execute) : ITool
    {
        private int _calls;

        public string Name { get; } = name;

        public string Description => Name + " test tool";

        public ToolCapabilities Capabilities { get; } = capabilities;

        public int Calls => Volatile.Read(ref _calls);

        public string? LastInput { get; private set; }

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastInput = request.Input;
            return await execute(request, cancellationToken);
        }
    }

    private sealed class RecordingAiConversationStore : IAiConversationStore
    {
        private readonly AiConversationStore _inner = new(new AiResponderOptions());
        private int _appends;

        public int Appends => Volatile.Read(ref _appends);

        public string GetKey(Source source, Conversation conversation, string senderId)
            => _inner.GetKey(source, conversation, senderId);

        public ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(
            string key,
            CancellationToken cancellationToken = default)
            => _inner.GetMessagesAsync(key, cancellationToken);

        public ValueTask AppendTurnAsync(
            string key,
            string userText,
            string assistantText,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _appends);
            return _inner.AppendTurnAsync(key, userText, assistantText, cancellationToken);
        }

        public ValueTask AppendTurnAsync(
            string key,
            string userText,
            string assistantText,
            string? sourceEventId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _appends);
            return _inner.AppendTurnAsync(key, userText, assistantText, sourceEventId, cancellationToken);
        }
    }
}
