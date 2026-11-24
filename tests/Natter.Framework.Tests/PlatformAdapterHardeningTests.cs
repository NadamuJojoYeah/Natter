using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Net;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.DingTalk;
using Natter.Chat.Discord;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 adapter hardening regressions (docs/CODE_REVIEW_2026-11-22.md §4.1).
/// DingTalk: A2 shared HttpClient, A3 ack-before-dispatch + dedup, A4 half-open detection,
/// A5 sessionWebhook allowlist, A6 reconnect announces itself.
/// Discord: A10 explicit user mentions, A11 bounded caches, A16 bounded 429 retry.
/// No real Discord connection is attempted; DingTalk Stream is exercised against a local
/// HttpListener gateway.
/// </summary>
public sealed class PlatformAdapterHardeningTests
{
    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A5: sessionWebhook allowlist
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://oapi.dingtalk.com/robot/sendBySession?session=x", true)]
    [InlineData("https://api.dingtalk.com/v1.0/robot/oToMessages/batchSend", true)]
    [InlineData("https://robot.dingtalk.com/hook?x=1", true)]
    [InlineData("http://oapi.dingtalk.com/robot/sendBySession", false)]
    [InlineData("https://oapi.dingtalk.com.evil.example/hook", false)]
    [InlineData("https://evil-dingtalk.com/hook", false)]
    [InlineData("https://127.0.0.1/hook", false)]
    [InlineData("https://169.254.169.254/latest/meta-data/", false)]
    [InlineData("https://user:pass@oapi.dingtalk.com/hook", false)]
    [InlineData("https://oapi.dingtalk.com:8443/hook", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("//oapi.dingtalk.com/hook", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Session_webhook_policy_only_accepts_https_dingtalk_hosts(string? webhook, bool expected)
    {
        var accepted = DingTalkSessionWebhookPolicy.TryValidate(webhook, out var uri, out var reason);

        Assert.Equal(expected, accepted);
        if (expected)
        {
            Assert.NotNull(uri);
            Assert.Equal("https", uri!.Scheme);
            Assert.Null(reason);
        }
        else
        {
            Assert.NotNull(reason);
        }
    }

    [Fact]
    public void Session_webhook_policy_reports_a_stable_reason_per_rejection()
    {
        Assert.Equal("missing", Reason(null));
        Assert.Equal("scheme_not_https", Reason("http://oapi.dingtalk.com/hook"));
        Assert.Equal("host_not_allowed", Reason("https://evil.example/hook"));
        Assert.Equal("credentials_in_url", Reason("https://a:b@oapi.dingtalk.com/hook"));
        Assert.Equal("non_default_port", Reason("https://oapi.dingtalk.com:9000/hook"));

        static string? Reason(string? webhook)
        {
            DingTalkSessionWebhookPolicy.TryValidate(webhook, out _, out var reason);
            return reason;
        }
    }

    [Fact]
    public async Task Stream_reply_rejects_an_untrusted_webhook_without_any_http_request()
    {
        var handler = new CountingHandler();
        using var transport = new DingTalkStreamTransport(
            new DingTalkOptions { ClientId = "client-id", BotId = "bot-dt" },
            "client-secret",
            new HttpClient(handler));

        var result = await transport.SendAsync(new DingTalkReply(
            "cid",
            "http://169.254.169.254/latest/meta-data/iam/security-credentials/",
            "hello"));

        Assert.False(result.Ok);
        Assert.Equal(DingTalkSessionWebhookPolicy.RejectedCode, result.ErrorCode);
        // Zero requests: the rejected webhook never reaches a socket.
        Assert.Empty(handler.Requests);

        var plain = await transport.SendAsync(new DingTalkReply(
            "cid",
            "https://evil.example/collect",
            "hello"));
        Assert.False(plain.Ok);
        Assert.Equal(DingTalkSessionWebhookPolicy.RejectedCode, plain.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Upstream_drops_an_untrusted_session_webhook_and_refuses_to_send()
    {
        var clock = new TestClock();
        var source = Source.Create("dingtalk", "dingtalk", "bot-dt");
        var options = new DingTalkOptions { BotId = "bot-dt", RequireMention = true };
        using var transport = new LocalDingTalkTransport();
        using var upstream = new DingTalkChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var created = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(created.Add);

        transport.Emit(CallbackMessage(
            "m-evil",
            "cid-evil",
            "hello",
            "https://evil.example/hook?steal=1"));

        var mapped = Assert.Single(created.OfType<MessageCreatedEvent>());
        Assert.Contains(DingTalkSessionWebhookPolicy.RejectedCode, upstream.LastError!, StringComparison.Ordinal);

        var command = new ChatCommandFactory(clock).Send(
            source,
            mapped.Payload.Message.Conversation,
            new Content("reply"));
        var ack = await upstream.ExecuteAsync(command);

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.NotFound, ack.Error!.Code);
        Assert.Empty(transport.Replies);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A2: replies must reuse the shared HttpClient
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_reply_reuses_the_injected_http_client()
    {
        var handler = new CountingHandler();
        using var transport = new DingTalkStreamTransport(
            new DingTalkOptions { ClientId = "client-id", BotId = "bot-dt" },
            "client-secret",
            new HttpClient(handler));

        const string webhook = "https://oapi.dingtalk.com/robot/sendBySession?session=abc";
        var first = await transport.SendAsync(new DingTalkReply("cid", webhook, "one"));
        var second = await transport.SendAsync(new DingTalkReply("cid", webhook, "two"));

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        // Two posts through the injected handler => the per-reply `new HttpClient` is gone.
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, uri => Assert.Equal("oapi.dingtalk.com", uri.Host));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A3: ack before dispatch + duplicate suppression
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_acks_before_dispatch_so_a_slow_subscriber_cannot_trigger_redelivery()
    {
        using var gateway = new FakeStreamGateway();
        gateway.OnStream = async (socket, cancellationToken) =>
        {
            await SendTextAsync(socket, CallbackFrame("frame-slow", "msg-slow", "cid", "hello"), cancellationToken);
            gateway.RecordAck(await ReadAckAsync(socket, cancellationToken));
        };
        gateway.Start();

        using var transport = new DingTalkStreamTransport(StreamOptions(gateway), "client-secret");
        // A subscriber that blocks exactly like a slow downstream turn would. The gate is released
        // by the test thread so the transport is never left with a stuck handler.
        using var dispatchGate = new ManualResetEventSlim(false);
        using var subscription = transport.Events.Subscribe(evt =>
        {
            if (evt.Kind == DingTalkTransportEventKind.Message)
                dispatchGate.Wait(TimeSpan.FromSeconds(10));
        });

        int ack;
        TimeSpan elapsed;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await transport.ConnectAsync();
            ack = await gateway.WaitForAckAsync(TimeSpan.FromSeconds(5));
            elapsed = stopwatch.Elapsed;
        }
        finally
        {
            dispatchGate.Set();
        }

        Assert.Equal(200, ack);
        // With the old order (OnNext then ack) the ack could only be written after the subscriber
        // returned, i.e. after the gate was released on timeout.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"ack took {elapsed.TotalMilliseconds:F0}ms; dispatch is still blocking the acknowledgement");
        await transport.DisconnectAsync();
    }

    [Fact]
    public async Task Stream_acknowledges_a_redelivered_callback_without_dispatching_it_twice()
    {
        using var gateway = new FakeStreamGateway();
        gateway.OnStream = async (socket, cancellationToken) =>
        {
            var frame = CallbackFrame("frame-dup", "msg-dup", "cid", "hello");
            await SendTextAsync(socket, frame, cancellationToken);
            gateway.RecordAck(await ReadAckAsync(socket, cancellationToken));

            // The platform re-delivers the same callback id when it did not see our first ack.
            await SendTextAsync(socket, frame, cancellationToken);
            gateway.RecordAck(await ReadAckAsync(socket, cancellationToken));
        };
        gateway.Start();

        using var transport = new DingTalkStreamTransport(StreamOptions(gateway), "client-secret");
        var events = new List<DingTalkTransportEvent>();
        using var subscription = transport.Events.Subscribe(events.Add);

        await transport.ConnectAsync();
        await WaitUntilAsync(() => gateway.Acks.Count >= 2, "both acks");
        await WaitUntilAsync(
            () => events.Any(item => item.ErrorCode == "stream_callback_duplicate"),
            "duplicate detection");

        Assert.Equal(2, gateway.Acks.Count);
        Assert.All(gateway.Acks, code => Assert.Equal(200, code));
        Assert.Single(events.Where(item => item.Kind == DingTalkTransportEventKind.Message));
        await transport.DisconnectAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A4 / S2-A6: half-open detection and reconnect announcement
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Stream_liveness_judges_idle_and_missing_pong()
    {
        var start = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var liveness = new DingTalkStreamLiveness(idleTimeoutMs: 1_000, pongTimeoutMs: 400);

        Assert.Equal(DingTalkStreamLivenessState.Unknown, liveness.Evaluate(start));

        liveness.Reset(start);
        Assert.Equal(DingTalkStreamLivenessState.Healthy, liveness.Evaluate(start.AddMilliseconds(50)));

        // Sent an ack, nothing came back inside the pong window.
        liveness.RecordSend(start.AddMilliseconds(100));
        Assert.Equal(
            DingTalkStreamLivenessState.PongMissing,
            liveness.Evaluate(start.AddMilliseconds(600)));

        // Inbound traffic again -> healthy.
        liveness.RecordInbound(start.AddMilliseconds(700));
        Assert.Equal(DingTalkStreamLivenessState.Healthy, liveness.Evaluate(start.AddMilliseconds(800)));

        // Silence past the idle window -> idle (half-open).
        Assert.Equal(
            DingTalkStreamLivenessState.Idle,
            liveness.Evaluate(start.AddMilliseconds(1_800)));
    }

    [Fact]
    public async Task Stream_reconnects_when_the_socket_goes_idle()
    {
        using var gateway = new FakeStreamGateway();
        // Accept the socket but never send a frame: the TCP connection looks open while the peer
        // is gone. This is the half-open failure S2-A4 calls out.
        gateway.OnStream = (socket, cancellationToken) => Task.CompletedTask;
        gateway.Start();

        var options = StreamOptions(gateway, idleMs: 120, pongMs: 80);
        using var transport = new DingTalkStreamTransport(options, "client-secret");
        var events = new List<DingTalkTransportEvent>();
        using var subscription = transport.Events.Subscribe(events.Add);

        await transport.ConnectAsync();
        await WaitUntilAsync(() => gateway.StreamConnections >= 2, "reconnect after idle timeout");

        Assert.Contains(
            events,
            item => item.Kind == DingTalkTransportEventKind.Error
                    && item.ErrorCode is "stream_idle_timeout" or "stream_pong_timeout");
        Assert.True(
            events.Count(item => item.Kind == DingTalkTransportEventKind.Connected) >= 2,
            "each successful (re)connect must announce itself");
        await transport.DisconnectAsync();
    }

    [Fact]
    public async Task Upstream_marks_connected_again_after_a_transport_reconnect()
    {
        var clock = new TestClock();
        var source = Source.Create("dingtalk", "dingtalk", "bot-dt");
        var options = new DingTalkOptions { BotId = "bot-dt" };
        using var transport = new LocalDingTalkTransport();
        using var upstream = new DingTalkChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var statuses = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(statuses.Add);

        transport.EmitEvent(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Disconnected });
        Assert.False(upstream.IsConnected);

        clock.Advance(TimeSpan.FromSeconds(30));
        transport.EmitEvent(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Connected });

        // S2-A6: health must stop reporting the outage once the transport recovered.
        Assert.True(upstream.IsConnected);
        Assert.True(upstream.Health.Connected);
        Assert.Equal(clock.UtcNow, upstream.Health.LastConnectedAt);
        Assert.Null(upstream.LastError);
        Assert.Contains(
            statuses.OfType<UpstreamStatusEvent>(),
            item => item.Payload.State == UpstreamState.Ready);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A10: explicit user mentions work, everyone/roles stay off
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Explicit_user_mentions_are_allowed_while_everyone_and_roles_stay_off()
    {
        var allowed = DiscordMentionPolicy.Build(["111", "222", "not-a-snowflake"]);
        var allowedTypes = allowed.AllowedTypes.GetValueOrDefault();

        Assert.Equal(AllowedMentionTypes.Users, allowedTypes);
        Assert.Equal([111UL, 222UL], allowed.UserIds);
        Assert.False(allowed.MentionRepliedUser);
        Assert.Equal(0, (int)(allowedTypes & DiscordMentionPolicy.NeverAllowed));

        var none = DiscordMentionPolicy.Build(null);
        Assert.Equal(AllowedMentionTypes.None, none.AllowedTypes.GetValueOrDefault());
        Assert.Empty(none.UserIds ?? []);

        var invalid = DiscordMentionPolicy.Build(["nope"]);
        Assert.Equal(AllowedMentionTypes.None, invalid.AllowedTypes.GetValueOrDefault());
        Assert.Empty(invalid.UserIds ?? []);

        // Explicitly assert the intent that used to be inverted: Users on, Everyone/Roles off.
        Assert.NotEqual(0, (int)(allowedTypes & AllowedMentionTypes.Users));
        Assert.Equal(0, (int)(allowedTypes & AllowedMentionTypes.Everyone));
        Assert.Equal(0, (int)(allowedTypes & AllowedMentionTypes.Roles));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A11: bounded caches
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bounded_cache_keeps_a_hard_capacity()
    {
        var cache = new DiscordBoundedCache<int>(capacity: 3, ttl: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 100; i++)
            cache.Set($"key-{i}", i);

        Assert.Equal(3, cache.Count);
        Assert.True(cache.EvictedCount >= 97);
        // The newest keys survive; the oldest are gone.
        Assert.False(cache.TryGet("key-0", out _));
        Assert.True(cache.TryGet("key-99", out var newest));
        Assert.Equal(99, newest);
    }

    [Fact]
    public async Task Bounded_cache_expires_entries()
    {
        var cache = new DiscordBoundedCache<int>(capacity: 10, ttl: TimeSpan.FromMilliseconds(60));
        cache.Set("k", 1);
        Assert.True(cache.TryGet("k", out _));

        await Task.Delay(140);

        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Count);
        Assert.True(cache.ExpiredCount >= 1);
    }

    [Fact]
    public void Bounded_cache_prune_drops_expired_entries_only()
    {
        var cache = new DiscordBoundedCache<int>(capacity: 10, ttl: TimeSpan.FromMinutes(5));
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Prune();
        Assert.Equal(2, cache.Count);
        Assert.True(cache.Remove("a"));
        Assert.False(cache.Remove("a"));
        Assert.Equal(1, cache.Count);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // S2-A16: bounded 429 retry that honours retryAfter
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rate_limit_policy_classifies_and_backs_off()
    {
        var options = new DiscordSendRetryOptions(MaxAttempts: 3, BaseDelayMs: 100, MaxDelayMs: 5_000);
        var limited = new HttpException(HttpStatusCode.TooManyRequests, null!, null, "rate limited", []);
        var badRequest = new HttpException(HttpStatusCode.BadRequest, null!, null, "bad request", []);

        Assert.True(DiscordSendRetryPolicy.IsRateLimited(limited));
        Assert.False(DiscordSendRetryPolicy.IsRateLimited(badRequest));
        Assert.False(DiscordSendRetryPolicy.IsRateLimited(new TimeoutException()));

        Assert.True(DiscordSendRetryPolicy.ShouldRetry(limited, attempt: 1, options));
        Assert.True(DiscordSendRetryPolicy.ShouldRetry(limited, attempt: 2, options));
        Assert.False(DiscordSendRetryPolicy.ShouldRetry(limited, attempt: 3, options));
        Assert.False(DiscordSendRetryPolicy.ShouldRetry(badRequest, attempt: 1, options));

        // Exponential backoff when the platform gave no delay.
        Assert.Equal(100, (int)DiscordSendRetryPolicy.DelayFor(limited, 1, options).TotalMilliseconds);
        Assert.Equal(200, (int)DiscordSendRetryPolicy.DelayFor(limited, 2, options).TotalMilliseconds);
    }

    [Fact]
    public void Rate_limit_policy_honours_the_platform_retry_after()
    {
        var options = new DiscordSendRetryOptions(MaxAttempts: 4, BaseDelayMs: 100, MaxDelayMs: 5_000);
        var request = new FakeRateLimitRequest { TimeoutAt = DateTimeOffset.UtcNow.AddMilliseconds(1_500) };
        var limited = new RateLimitedException(request);

        Assert.True(DiscordSendRetryPolicy.IsRateLimited(limited));

        var retryAfter = DiscordSendRetryPolicy.RetryAfterMs(limited);
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter!.Value, 1_000, 1_600);

        var delay = DiscordSendRetryPolicy.DelayFor(limited, attempt: 1, options);
        Assert.True(
            delay >= TimeSpan.FromMilliseconds(1_000),
            $"retryAfter was ignored: delay={delay.TotalMilliseconds}ms");

        // The ceiling still wins over an absurd platform delay.
        var huge = new RateLimitedException(new FakeRateLimitRequest
        {
            TimeoutAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        Assert.Equal(5_000, (int)DiscordSendRetryPolicy.DelayFor(huge, 1, options).TotalMilliseconds);
    }

    [Fact]
    public void Rate_limit_exhaustion_returns_an_explicit_failure()
    {
        var options = new DiscordSendRetryOptions(MaxAttempts: 3, BaseDelayMs: 50, MaxDelayMs: 1_000);
        var limited = new HttpException(HttpStatusCode.TooManyRequests, null!, null, "rate limited", []);

        var result = DiscordSendRetryPolicy.Exhausted(limited, attempts: 3, options);

        Assert.False(result.Ok);
        Assert.Equal("rate_limited", result.ErrorCode);
        Assert.Contains("3 attempt", result.ErrorMessage!, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static DingTalkOptions StreamOptions(
        FakeStreamGateway gateway,
        int idleMs = 60_000,
        int pongMs = 60_000)
        => new()
        {
            ClientId = "client-id",
            BotId = "bot-dt",
            StreamGatewayUrl = gateway.OpenUrl,
            StreamIdleTimeoutMs = idleMs,
            StreamPongTimeoutMs = pongMs,
            ReconnectBaseDelayMs = 10,
            ReconnectMaxDelayMs = 20,
        };

    private static DingTalkCallbackMessage CallbackMessage(
        string msgId,
        string conversationId,
        string text,
        string? sessionWebhook)
        => new(
            msgId,
            conversationId,
            "2",
            "group",
            "user-1",
            "user-1",
            "User",
            "bot-dt",
            "robot",
            text,
            ["bot-dt"],
            IsInAtList: true,
            "text",
            sessionWebhook,
            sessionWebhook is null ? null : DateTimeOffset.UtcNow.AddMinutes(30),
            DateTimeOffset.UtcNow);

    private static string CallbackFrame(string frameId, string msgId, string conversationId, string text)
    {
        var payload = CallbackMessage(msgId, conversationId, text, "https://oapi.dingtalk.com/robot/sendBySession?session=t");
        return JsonSerializer.Serialize(new
        {
            type = "CALLBACK",
            headers = new { messageId = frameId, topic = DingTalkStreamProtocol.BotTopic },
            data = JsonSerializer.Serialize(new
            {
                msgId = payload.MsgId,
                conversationId = payload.ConversationId,
                conversationType = payload.ConversationType,
                conversationTitle = payload.ConversationTitle,
                senderId = payload.SenderId,
                senderStaffId = payload.SenderStaffId,
                senderNick = payload.SenderNick,
                chatbotUserId = payload.ChatbotUserId,
                robotCode = payload.RobotCode,
                msgtype = "text",
                text = new { content = payload.Content },
                atUsers = payload.AtUserIds.Select(id => new { dingtalkId = id }).ToArray(),
                isInAtList = payload.IsInAtList,
                sessionWebhook = payload.SessionWebhook,
                sessionWebhookExpiredTime = payload.SessionWebhookExpiresAt?.ToUnixTimeMilliseconds(),
                createAt = payload.CreatedAt.ToUnixTimeMilliseconds(),
            }),
        });
    }

    private static async Task SendTextAsync(WebSocket socket, string json, CancellationToken cancellationToken)
        => await socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);

    private static async Task<int> ReadAckAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        var document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        return document.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsed)
            ? parsed
            : -1;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"Condition '{because}' was not met within {timeoutMs}ms.");
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Records request URIs so tests can assert "zero request" / "shared client".</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                _requests.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"errcode":0,"errmsg":"ok"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Local DingTalk Stream gateway: /open returns a ws endpoint, /stream accepts sockets.</summary>
    private sealed class FakeStreamGateway : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly List<int> _acks = [];
        private readonly Lock _ackGate = new();
        private readonly TaskCompletionSource<int> _firstAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<WebSocket> _sockets = [];
        private int _streamConnections;
        private Task? _loop;

        public FakeStreamGateway()
        {
            Port = FindFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        }

        public int Port { get; }

        public string OpenUrl => $"http://127.0.0.1:{Port}/open";

        public Func<WebSocket, CancellationToken, Task>? OnStream { get; set; }

        public int StreamConnections => Volatile.Read(ref _streamConnections);

        public IReadOnlyList<int> Acks
        {
            get
            {
                lock (_ackGate)
                    return _acks.ToArray();
            }
        }

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public void RecordAck(int code)
        {
            lock (_ackGate)
                _acks.Add(code);
            _firstAck.TrySetResult(code);
        }

        public Task<int> WaitForAckAsync(TimeSpan timeout)
            => _firstAck.Task.WaitAsync(timeout);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            try
            {
                _loop?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            lock (_ackGate)
            {
                foreach (var socket in _sockets)
                {
                    try
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                    }
                }

                _sockets.Clear();
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath;
                if (path == "/open")
                {
                    var body = Encoding.UTF8.GetBytes(
                        $$"""{"endpoint":"ws://127.0.0.1:{{Port}}/stream","ticket":"ticket"}""");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, _cts.Token);
                    context.Response.Close();
                    return;
                }

                if (path != "/stream")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                Interlocked.Increment(ref _streamConnections);
                var accepted = await context.AcceptWebSocketAsync(null).WaitAsync(_cts.Token);
                lock (_ackGate)
                    _sockets.Add(accepted.WebSocket);

                if (OnStream is { } handler)
                    await handler(accepted.WebSocket, _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // The client aborts the socket on idle/half-open paths; that is expected here.
            }
        }
    }

    private sealed class LocalDingTalkTransport : IDingTalkTransport, IDisposable
    {
        private readonly System.Reactive.Subjects.Subject<DingTalkTransportEvent> _events = new();

        public IObservable<DingTalkTransportEvent> Events => _events;

        public List<DingTalkReply> Replies { get; } = [];

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<DingTalkSendResult> SendAsync(DingTalkReply reply, CancellationToken cancellationToken = default)
        {
            Replies.Add(reply);
            return ValueTask.FromResult(DingTalkSendResult.Success());
        }

        public void Emit(DingTalkCallbackMessage message)
            => EmitEvent(new DingTalkTransportEvent
            {
                Kind = DingTalkTransportEventKind.Message,
                Message = message,
            });

        public void EmitEvent(DingTalkTransportEvent transportEvent) => _events.OnNext(transportEvent);

        public void Dispose() => _events.Dispose();
    }

    /// <summary>Minimal IRequest for constructing a typed rate-limit exception.</summary>
    private sealed class FakeRateLimitRequest : IRequest
    {
        public DateTimeOffset? TimeoutAt { get; set; }

        public RequestOptions Options { get; } = new();
    }
}
