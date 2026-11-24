using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.2b: native tool calling through the task loop, with capability negotiation against the text
/// fallback. Every tool call goes through <see cref="ToolInvoker"/>; observations never touch
/// conversation history and the dispatcher stays the only platform sender.
/// </summary>
public sealed class P5NativeToolCallingTests
{
    private const string InputSchema = """
        {
          "type": "object",
          "properties": { "input": { "type": "string", "minLength": 1 } },
          "required": ["input"],
          "additionalProperties": false
        }
        """;

    [Fact]
    public async Task Native_provider_passes_tool_declarations_and_runs_the_function_call()
    {
        var tool = new TestTool("clock", "Returns the current time", Result("tick"), NativeClockMetadata());
        var client = new ScriptedChatClient();
        client.Enqueue(() => FunctionCall("call-1", "clock", "now"));
        client.Enqueue(() => Text("final answer"));
        var worker = CreateWorker(new[] { tool }, client, ToolProtocolCapabilities.NativeAndText);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("final answer", result.Text);
        Assert.Equal(1, tool.Calls);

        var firstOptions = client.Options[0];
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(firstOptions!.Tools!));
        Assert.Equal("clock", declaration.Name);
        Assert.Contains("input", declaration.JsonSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(ChatToolMode.Auto, firstOptions.ToolMode);
        Assert.False(firstOptions.AllowMultipleToolCalls);

        // The second model step sees the tool result as a native FunctionResultContent pair.
        var functionResult = client.Messages[1]
            .SelectMany(message => message.Contents ?? [])
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal("task-observation-0", functionResult.CallId);
        Assert.Equal("tick", functionResult.Result);
    }

    [Fact]
    public async Task Text_only_provider_uses_the_tool_call_fallback()
    {
        var tool = new TestTool("clock", "Returns the current time", Result("tick"), NativeClockMetadata());
        var client = new ScriptedChatClient();
        client.Enqueue(() => Text("""<tool_call>{"tool":"clock","input":"now"}</tool_call>"""));
        client.Enqueue(() => Text("final answer"));
        var worker = CreateWorker(new[] { tool }, client, ToolProtocolCapabilities.TextOnly);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal("final answer", result.Text);
        Assert.Equal(1, tool.Calls);
        Assert.True(client.Options[0]?.Tools is null or { Count: 0 });
        var observation = client.Messages[1].Last().Text;
        Assert.Contains("[tool observation] clock (ok)", observation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Native_only_provider_without_tools_answers_directly()
    {
        var client = new ScriptedChatClient();
        client.Enqueue(() => Text("hello"));
        var worker = CreateWorker(Array.Empty<ITool>(), client, ToolProtocolCapabilities.NativeOnly);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("hello", result.Text);
        Assert.True(client.Options[0]?.Tools is null or { Count: 0 });
    }

    [Fact]
    public async Task Native_only_provider_with_a_text_only_tool_reports_protocol_unsupported()
    {
        var legacy = new TestTool("legacy", "Legacy tool", Result("never"), ToolMetadata.Legacy);
        var client = new ScriptedChatClient();
        var worker = CreateWorker(new[] { legacy }, client, ToolProtocolCapabilities.NativeOnly);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ProtocolUnsupported, result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Equal(0, legacy.Calls);
        Assert.Empty(client.Messages);
    }

    [Fact]
    public async Task Unknown_tool_is_reported_back_to_the_model_as_an_observation()
    {
        var tool = new TestTool("clock", "Returns the current time", Result("tick"), NativeClockMetadata());
        var client = new ScriptedChatClient();
        client.Enqueue(() => FunctionCall("call-1", "missing", "x"));
        client.Enqueue(() => Text("done"));
        var worker = CreateWorker(new[] { tool }, client, ToolProtocolCapabilities.NativeAndText);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Equal(0, tool.Calls);
        var functionResult = FunctionResult(client, step: 1);
        Assert.Contains("tool_unknown", functionResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_arguments_are_reported_back_to_the_model_as_an_observation()
    {
        var tool = new TestTool("clock", "Returns the current time", Result("tick"), NativeClockMetadata());
        var client = new ScriptedChatClient();
        client.Enqueue(() => FunctionCall("call-1", "clock", string.Empty));
        client.Enqueue(() => Text("done"));
        var worker = CreateWorker(new[] { tool }, client, ToolProtocolCapabilities.NativeAndText);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        Assert.Equal(0, tool.Calls);
        var functionResult = FunctionResult(client, step: 1);
        Assert.Contains("tool_arguments_invalid", functionResult, StringComparison.Ordinal);
        Assert.Contains("/input", functionResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_timeout_returns_an_explicit_error()
    {
        var slow = new TestTool(
            "slow",
            "Waits forever",
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ToolResult("never");
            },
            NativeClockMetadata(timeout: TimeSpan.FromMilliseconds(40)));
        var client = new ScriptedChatClient();
        client.Enqueue(() => FunctionCall("call-1", "slow", "wait"));
        var worker = CreateWorker(new[] { slow }, client, ToolProtocolCapabilities.NativeAndText);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolTimeout, result.ErrorCode);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_to_the_model_step()
    {
        var client = new ScriptedChatClient
        {
            Handler = static async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "never"));
            },
        };
        var worker = CreateWorker(Array.Empty<ITool>(), client, ToolProtocolCapabilities.NativeAndText);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await worker.ExecuteAsync(NewTask(), Context(), cancellation.Token));
    }

    [Fact]
    public async Task Tool_output_is_truncated_by_the_invoker_before_reaching_the_model()
    {
        var noisy = new TestTool(
            "noisy",
            "Returns a lot of text",
            (_, _) => ValueTask.FromResult(new ToolResult(new string('x', 50))),
            NativeClockMetadata(maxOutputCharacters: 10));
        var client = new ScriptedChatClient();
        client.Enqueue(() => FunctionCall("call-1", "noisy", "dump"));
        client.Enqueue(() => Text("done"));
        var worker = CreateWorker(new[] { noisy }, client, ToolProtocolCapabilities.NativeAndText);

        var result = await worker.ExecuteAsync(NewTask(), Context(), CancellationToken.None);

        Assert.Equal("done", result.Text);
        var functionResult = FunctionResult(client, step: 1);
        Assert.Equal(10, functionResult.Length);
        Assert.DoesNotContain(new string('x', 50), functionResult, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_profile_defaults_are_unchanged()
    {
        var profiles = new RuntimeProfileStore(null);
        var profile = Assert.Single(profiles.Current.Profiles, item => item.Id == "tier1-default");

        Assert.Equal("tier1-default", profiles.Current.DefaultProfile);
        Assert.Equal("host#static", profile.Prompt.Provider);
        Assert.True(string.IsNullOrEmpty(profile.Prompt.SystemPrompt));
        Assert.Equal("host#recent-window", profile.Context.Assembler);
        Assert.Equal("host#inline", profile.Memory.Store);
    }

    // ------------------------------------------------------------------ helpers

    private static Func<ToolRequest, CancellationToken, ValueTask<ToolResult>> Result(string output)
        => (_, _) => ValueTask.FromResult(new ToolResult(output));

    private static ToolMetadata NativeClockMetadata(
        TimeSpan? timeout = null,
        int maxOutputCharacters = 100)
        => new(
            Version: "1.3.0",
            InputSchema: InputSchema,
            Permissions: [],
            MaxOutputCharacters: maxOutputCharacters,
            Timeout: timeout,
            NativeCallSupported: true);

    private static ChatResponse FunctionCall(string callId, string name, string input)
        => new(new ChatMessage(
            ChatRole.Assistant,
            new List<AIContent>
            {
                new FunctionCallContent(callId, name, new Dictionary<string, object?> { ["input"] = input }),
            }));

    private static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    private static string FunctionResult(ScriptedChatClient client, int step)
    {
        var result = client.Messages[step]
            .SelectMany(message => message.Contents ?? [])
            .OfType<FunctionResultContent>()
            .LastOrDefault();
        Assert.NotNull(result);
        return result!.Result?.ToString() ?? string.Empty;
    }

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

    private static AiTaskWorker CreateWorker(
        IReadOnlyList<ITool> tools,
        ScriptedChatClient client,
        ToolProtocolCapabilities providerProtocols)
    {
        var responder = BuildResponder(client, providerProtocols);
        var services = new ServiceCollection();
        var responders = new ChatResponderRegistry();
        responders.Register(new ChatResponderRegistration("probe", 0, "test", () => responder));
        services.AddSingleton<IChatResponderRegistry>(responders);
        var provider = services.BuildServiceProvider();

        var policy = new ReadOnlyToolPolicy();
        var registry = new ToolRegistry(tools);
        return new AiTaskWorker(provider, tools, policy, new ToolLoopOptions(), registry, new ToolInvoker(registry, policy));
    }

    private static AiChatResponder BuildResponder(IChatClient client, ToolProtocolCapabilities protocols)
    {
        var options = new AiResponderOptions
        {
            SystemPrompt = "system",
            FallbackText = "AI is temporarily unavailable.",
            MaxConcurrentRequests = 2,
        };
        return new AiChatResponder(
            client,
            options,
            new AiConversationStore(options),
            new AiConversationGate(options),
            new ChatCommandFactory(new TestClock()),
            new FakePluginLog(),
            toolProtocols: protocols);
    }

    private sealed class TestTool(
        string name,
        string description,
        Func<ToolRequest, CancellationToken, ValueTask<ToolResult>> execute,
        ToolMetadata metadata) : ITool
    {
        private int _calls;

        public string Name { get; } = name;

        public string Description { get; } = description;

        public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

        public ToolMetadata Metadata { get; } = metadata;

        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return await execute(request, cancellationToken);
        }
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _script = new();
        private readonly ChatClientMetadata _metadata = new("fake", new Uri("https://example.invalid"), "fake-model");

        public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? Handler { get; set; }

        public void Enqueue(Func<ChatResponse> next) => _script.Enqueue(next);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToArray();
            Messages.Add(list);
            Options.Add(options);

            if (Handler is not null)
                return Handler(list, options, cancellationToken);

            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()()
                : throw new InvalidOperationException("No scripted response left."));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}
