using System.Reactive.Subjects;
using Natter.Chat;
using Natter.Chat.Satori;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class SatoriUpstreamTests
{
    [Fact]
    public async Task Upstream_maps_events_and_updates_capabilities_from_login_features()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);

        var captured = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(new CaptureObserver<ChatEvent>(captured));

        await upstream.ConnectAsync();

        transport.Emit(CreateMessageEvent());

        var chatEvent = Assert.IsType<MessageCreatedEvent>(Assert.Single(captured));
        Assert.Equal("987", chatEvent.Payload.Message.Id);
        Assert.Equal("bot-1", chatEvent.Source.SelfId);

        var capabilities = await upstream.GetCapabilitiesAsync();
        Assert.True(capabilities.Edit);
        Assert.True(capabilities.Delete);
        Assert.True(capabilities.Attachments);
    }

    [Fact]
    public async Task Upstream_maps_commands_and_deduplicates_by_idempotency_key()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var send = new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi"),
            idempotencyKey: "run-1");

        var first = await upstream.ExecuteAsync(send);
        var second = await upstream.ExecuteAsync(send);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Result!.MessageIds, second.Result!.MessageIds);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("/v1/message.create", request.Route);
        Assert.Equal("discord", request.Headers["Satori-Platform"]);
    }

    [Fact]
    public async Task Upstream_returns_unsupported_for_unmappable_commands()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        using var upstream = new SatoriChatUpstream(source, new FakeSatoriTransport(), clock);
        await upstream.ConnectAsync();

        var typing = new TypingStartCommand
        {
            Id = "cmd:typing",
            Source = source,
            IssuedAt = clock.UtcNow,
            Payload = new TypingStartPayload(ChatTestData.Conversation()),
        };

        var ack = await upstream.ExecuteAsync(typing);

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.Unsupported, ack.Error!.Code);
    }

    [Fact]
    public async Task Upstream_deduplicates_replayed_sn_and_resumes_from_it()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var captured = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(new CaptureObserver<ChatEvent>(captured));

        var chatEvent = CreateMessageEvent();
        transport.Emit(chatEvent);
        transport.Emit(chatEvent);

        Assert.Single(captured);
        Assert.Equal(42, upstream.SnTracker.LastSn);

        await upstream.DisconnectAsync();
        await upstream.ConnectAsync();

        Assert.Equal(42, transport.LastSn);
    }

    [Fact]
    public async Task Upstream_reports_rate_limit_errors_from_the_transport()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport
        {
            Result = new SatoriSendResult(
                Ok: false,
                ErrorCode: ChatErrorCodes.RateLimited,
                ErrorMessage: "Discord rate limit exceeded.",
                RetryAfterMs: 1200),
        };

        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var ack = await upstream.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi")));

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.RateLimited, ack.Error!.Code);
        Assert.True(ack.Error.Retryable);
        Assert.Equal(1200, ack.Error.RetryAfterMs);
    }

    private static SatoriEvent CreateMessageEvent()
        => new()
        {
            Sn = 42,
            Type = "message-created",
            Timestamp = 1763128800,
            Login = new SatoriLogin
            {
                Platform = "discord",
                User = new SatoriUser { Id = "bot-1", Name = "Natter", IsBot = true },
                Features = ["message.update", "message.delete", "message.attachments"],
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

    private sealed class FakeSatoriTransport : ISatoriTransport
    {
        private readonly Subject<SatoriEvent> _events = new();

        public IObservable<SatoriEvent> Events => _events;

        public long? LastSn { get; private set; }

        public List<SatoriApiRequest> Requests { get; } = [];

        public SatoriSendResult Result { get; set; } = new(true, ["satori-message-1"]);

        public ValueTask ConnectAsync(long? lastSn, CancellationToken cancellationToken = default)
        {
            LastSn = lastSn;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SatoriSendResult> SendAsync(
            SatoriApiRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Result);
        }

        public void Emit(SatoriEvent satoriEvent) => _events.OnNext(satoriEvent);
    }
}
