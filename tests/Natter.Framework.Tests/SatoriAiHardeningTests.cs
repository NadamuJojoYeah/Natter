using System.Diagnostics;
using System.Reactive.Subjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Satori;
using Natter.Feature.Ai;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 hardening for the Satori adapter (sn commit after mapping, Direct capability, transport
/// disposal) and the shared AI responder (visible provider failures, bounded/cancelable clip
/// retries, structured finish_reason, bounded conversation gate).
/// </summary>
public sealed class SatoriAiHardeningTests
{
    // ---------------------------------------------------------------- Satori A7/A8/A9

    [Fact]
    public async Task Mapping_failure_does_not_advance_sn_and_the_event_can_be_replayed()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        var logger = new ListLogger<SatoriChatUpstream>();
        using var upstream = new SatoriChatUpstream(source, transport, clock, logger: logger);
        await upstream.ConnectAsync();

        var captured = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(new CaptureObserver<ChatEvent>(captured));

        // No login/message: the mapper returns null for this frame.
        transport.Emit(new SatoriEvent { Type = "message-created", Sn = 42 });

        Assert.Null(upstream.SnTracker.LastSn);
        Assert.Empty(captured);
        Assert.Contains(logger.Messages, message => message.Contains("sn not advanced", StringComparison.Ordinal));

        // The same sequence is replayed with a valid frame; it must still be accepted.
        transport.Emit(CreateMessageEvent(sn: 42));

        Assert.Equal(42, upstream.SnTracker.LastSn);
        Assert.Single(captured);
    }

    [Fact]
    public async Task Direct_conversations_are_advertised_and_pass_the_dispatcher()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        // Direct is advertised even before a login.features frame arrives.
        var initial = await upstream.GetCapabilitiesAsync();
        Assert.Contains(ConversationKind.Direct, initial.ConversationKinds);

        transport.Emit(LoginEvent());
        var capabilities = await upstream.GetCapabilitiesAsync();
        Assert.Contains(ConversationKind.Direct, capabilities.ConversationKinds);
        Assert.True(capabilities.SupportsConversation(ConversationKind.Direct));

        var registry = new ChatUpstreamRegistry();
        registry.Register(upstream);
        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);
        var send = new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation("dm", ConversationKind.Direct),
            new Content("hello"),
            idempotencyKey: "direct-1");

        var ack = await dispatcher.ExecuteAsync(send);

        Assert.True(ack.Ok, ack.Error?.Message);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task Dispose_disconnects_and_disposes_the_transport_idempotently()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        upstream.Dispose();
        upstream.Dispose();

        Assert.Equal(1, transport.DisconnectCount);
        Assert.True(transport.Disposed);
    }

    // ---------------------------------------------------------------- AI A17

    [Fact]
    public async Task Authentication_failure_is_surfaced_with_a_code_and_counted()
    {
        var responder = BuildResponder(new FakeChatClient
        {
            ResponseFactory = _ => throw new ProviderException(AiErrorKind.Authentication),
        });
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "auth-1"))));

        Assert.Contains("ai.authentication", command.Payload.Content.Text, StringComparison.Ordinal);
        Assert.Equal(1, responder.FailureCount);
        Assert.Equal("ai.authentication", responder.LastFailureCode);
    }

    [Fact]
    public async Task Balance_and_invalid_request_failures_are_not_silent()
    {
        var responder = BuildResponder(new FakeChatClient
        {
            ResponseFactory = _ => throw new ProviderException(AiErrorKind.InsufficientBalance),
        });
        var source = ChatTestData.Source();

        var first = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "balance-1"))));
        Assert.Contains("ai.insufficient_balance", first.Payload.Content.Text, StringComparison.Ordinal);
        Assert.Equal(1, responder.FailureCount);

        var invalid = BuildResponder(new FakeChatClient
        {
            ResponseFactory = _ => throw new ProviderException(AiErrorKind.InvalidRequest),
        });
        var second = Assert.IsType<MessageSendCommand>(await invalid.RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "invalid-1"))));
        Assert.Contains("ai.invalid_request", second.Payload.Content.Text, StringComparison.Ordinal);
        Assert.Equal(1, invalid.FailureCount);
    }

    // ---------------------------------------------------------------- AI A18

    [Fact]
    public async Task Clip_retry_has_a_bounded_backoff_and_counts_attempts()
    {
        var calls = 0;
        var responder = BuildResponder(
            new FakeChatClient
            {
                ResponseFactory = _ =>
                {
                    calls++;
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response clipped>"));
                },
            },
            options => options.ClipRetryDelayMilliseconds = 60);

        var stopwatch = Stopwatch.StartNew();
        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(ChatTestData.Source(), ChatTestData.Message(ChatTestData.Source(), id: "clip-1"))));
        stopwatch.Stop();

        Assert.Equal(2, calls);
        Assert.Equal("AI is temporarily unavailable.", command.Payload.Content.Text);
        Assert.True(stopwatch.ElapsedMilliseconds >= 40, $"elapsed={stopwatch.ElapsedMilliseconds}");
        Assert.Equal("ai.clipped", responder.LastFailureCode);
    }

    [Fact]
    public async Task Clip_retry_limit_is_bounded()
    {
        var calls = 0;
        var responder = BuildResponder(
            new FakeChatClient
            {
                ResponseFactory = _ =>
                {
                    calls++;
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response clipped>"));
                },
            },
            options =>
            {
                options.MaxClipRetries = 2;
                options.ClipRetryDelayMilliseconds = 1;
            });

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(ChatTestData.Source(), ChatTestData.Message(ChatTestData.Source(), id: "clip-limit"))));

        Assert.Equal(3, calls);
        Assert.Equal("AI is temporarily unavailable.", command.Payload.Content.Text);
    }

    [Fact]
    public async Task Clip_retry_honours_cancellation_during_backoff()
    {
        var calls = 0;
        var responder = BuildResponder(
            new FakeChatClient
            {
                ResponseFactory = _ =>
                {
                    calls++;
                    return new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response clipped>"));
                },
            },
            options => options.ClipRetryDelayMilliseconds = 2000);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await responder.RespondAsync(
                ChatTestData.CreatedEvent(ChatTestData.Source(), ChatTestData.Message(ChatTestData.Source(), id: "clip-cancel")),
                cancellation.Token));

        stopwatch.Stop();
        Assert.Equal(1, calls);
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"elapsed={stopwatch.ElapsedMilliseconds}");
    }

    // ---------------------------------------------------------------- AI A19

    [Fact]
    public async Task Structured_length_finish_reason_is_treated_as_clipped()
    {
        var calls = 0;
        var responder = BuildResponder(new FakeChatClient
        {
            ResponseFactory = _ => ++calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial"))
                {
                    FinishReason = ChatFinishReason.Length,
                }
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "complete answer")),
        });
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "finish-length"))));

        Assert.Equal("complete answer", command.Payload.Content.Text);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Structured_length_finish_reason_falls_back_after_retry_limit()
    {
        var responder = BuildResponder(
            new FakeChatClient
            {
                ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial"))
                {
                    FinishReason = ChatFinishReason.Length,
                },
            },
            options => options.MaxClipRetries = 0);

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(ChatTestData.Source(), ChatTestData.Message(ChatTestData.Source(), id: "finish-length-2"))));

        Assert.Equal("AI is temporarily unavailable.", command.Payload.Content.Text);
    }

    // ---------------------------------------------------------------- AI A20

    [Fact]
    public async Task Conversation_gate_prunes_idle_entries()
    {
        var options = new AiResponderOptions
        {
            MaxConcurrentRequests = 4,
            ConversationIdleSeconds = 0,
            MaxConversationEntries = 32,
        };
        using var gate = new AiConversationGate(options);

        for (var index = 0; index < 200; index++)
        {
            using var lease = await gate.AcquireAsync($"conversation-{index}");
        }

        Assert.True(gate.ConversationCount <= options.MaxConversationEntries, $"count={gate.ConversationCount}");
        Assert.Equal(0, gate.ActiveOperationCount);
    }

    [Fact]
    public async Task Conversation_gate_caps_entries_even_before_the_idle_timeout()
    {
        var options = new AiResponderOptions
        {
            MaxConcurrentRequests = 4,
            ConversationIdleSeconds = 600,
            MaxConversationEntries = 64,
        };
        using var gate = new AiConversationGate(options);

        for (var index = 0; index < 500; index++)
        {
            using var lease = await gate.AcquireAsync($"conv-{index}");
        }

        Assert.True(gate.ConversationCount <= options.MaxConversationEntries, $"count={gate.ConversationCount}");
    }

    [Fact]
    public async Task Dispose_during_an_inflight_lease_does_not_throw()
    {
        var gate = new AiConversationGate(new AiResponderOptions
        {
            GateDisposeTimeoutMilliseconds = 50,
            ConversationIdleSeconds = 0,
        });
        var lease = await gate.AcquireAsync("conv");

        var dispose = Task.Run(gate.Dispose);
        await Task.Delay(10);
        lease.Dispose();

        await dispose;
        Assert.Equal(0, gate.ActiveOperationCount);
        gate.Dispose();
    }

    [Fact]
    public async Task Dispose_with_zero_wait_and_inflight_lease_still_lets_lease_release()
    {
        var gate = new AiConversationGate(new AiResponderOptions
        {
            GateDisposeTimeoutMilliseconds = 0,
            ConversationIdleSeconds = 0,
        });
        var lease = await gate.AcquireAsync("conv");

        gate.Dispose();
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, gate.ActiveOperationCount);
    }

    // ---------------------------------------------------------------- helpers

    private static AiChatResponder BuildResponder(FakeChatClient chat, Action<AiResponderOptions>? configure = null)
    {
        var options = new AiResponderOptions
        {
            SystemPrompt = "system",
            HistoryMaxMessages = 20,
            HistoryMaxCharacters = 100000,
            FallbackText = "AI is temporarily unavailable.",
        };
        configure?.Invoke(options);
        var clock = new TestClock();
        return new AiChatResponder(
            chat,
            options,
            new AiConversationStore(options),
            new AiConversationGate(options),
            new ChatCommandFactory(clock),
            new FakePluginLog());
    }

    private static SatoriEvent CreateMessageEvent(long sn = 42)
        => new()
        {
            Sn = sn,
            Type = "message-created",
            Timestamp = 1763128800,
            Login = new SatoriLogin
            {
                Platform = "discord",
                User = new SatoriUser { Id = "bot-1", Name = "Natter", IsBot = true },
                Features = ["message.update", "message.delete", "message.attachments", "guild.plain"],
            },
            Message = new SatoriMessage
            {
                Id = "987",
                Content = "hello",
                Channel = new SatoriChannel { Id = "555", Type = 0, Name = "general" },
                Guild = new SatoriGuild { Id = "444" },
                User = new SatoriUser { Id = "333", Name = "yg", IsBot = false },
                CreatedAt = 1763128800,
            },
        };

    private static SatoriEvent LoginEvent()
        => new()
        {
            Sn = 1,
            Type = "login-added",
            Timestamp = 1763128800,
            Login = new SatoriLogin
            {
                Platform = "discord",
                User = new SatoriUser { Id = "bot-1", Name = "Natter", IsBot = true },
                Features = ["message.update", "message.delete", "message.attachments", "guild.plain"],
            },
        };

    private sealed class ProviderException(AiErrorKind kind) : Exception(kind.ToString()), IAiProviderException
    {
        public AiErrorKind Kind { get; } = kind;

        public int? StatusCode => null;

        public string? ErrorCode => Kind.ToString();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class FakeSatoriTransport : ISatoriTransport, IDisposable
    {
        private readonly Subject<SatoriEvent> _events = new();

        public IObservable<SatoriEvent> Events => _events;

        public List<SatoriApiRequest> Requests { get; } = [];

        public int DisconnectCount { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask ConnectAsync(long? lastSn, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SatoriSendResult> SendAsync(
            SatoriApiRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new SatoriSendResult(true, ["satori-message-1"]));
        }

        public void Emit(SatoriEvent satoriEvent) => _events.OnNext(satoriEvent);

        public void Dispose()
        {
            Disposed = true;
            _events.Dispose();
        }
    }
}
