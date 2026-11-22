using System.Diagnostics;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Discord;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Regression suite for docs/operations/2026-11-22-discord-gateway-stall.md: the gateway stayed
/// disconnected for 3h19m because nothing outside Discord.Net's internal reconnect loop watched
/// the connection. Everything here runs against a scripted fake transport; no Discord connection
/// is attempted and no real clock-driven production threshold is used.
/// </summary>
public sealed class GatewayReconnectTests
{
    private const string TokenSentinel = "MTIzNDU2Nzg5MDEyMzQ1Njc4.SENTINEL-BOT-TOKEN";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static Source Src => Source.Create("discord", "discord", "bot");

    // ─────────────────────────────────────────────────────────────────────────────
    // P0 defaults / no-regression locks
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Default_options_enable_the_watchdog_with_the_production_threshold()
    {
        var options = new DiscordOptions();

        // P0 acceptance: connected=false past 60s must trigger a rebuild with no configuration.
        Assert.True(options.ReconnectWatchdog);
        Assert.Equal(60_000, options.ReconnectWatchdogThresholdMs);
        Assert.Equal(10, options.ReconnectMaxAttempts);
        Assert.Equal(1_000, options.ReconnectBackoffInitialMs);
        Assert.Equal(60_000, options.ReconnectBackoffMaxMs);
    }

    [Fact]
    public async Task Default_threshold_never_rebuilds_a_connection_that_drops_within_one_second()
    {
        // Regression lock: stock options leave existing behaviour alone (a short disconnect is
        // still Discord.Net's business; the watchdog only takes over after the 60s threshold).
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, new DiscordOptions { SelfId = "bot" }, new TestClock());

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });

        await Task.Delay(150);

        Assert.Equal(1, transport.ConnectCount);
        Assert.False(upstream.IsConnected);
        // OBS-019 semantics untouched: disconnect cause and reconnect counters survive.
        Assert.Equal("gateway_4009", upstream.LastDisconnectCode);
        Assert.Equal(1, upstream.Health.ReconnectCount);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P0 watchdog
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Watchdog_rebuilds_the_transport_after_the_connected_false_threshold()
    {
        var transport = new ScriptedGatewayTransport();
        var logger = new RecordingLogger<DiscordChatUpstream>();
        using var upstream = new DiscordChatUpstream(Src, transport, WatchdogOptions(), new RealClock(), new FixedLoggerFactory(logger));

        await upstream.ConnectAsync();
        Assert.True(upstream.IsConnected);
        Assert.Equal(1, transport.ConnectCount);

        // The production failure mode: one disconnect event, then silence forever.
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect; closeCode=4009 (session timed out)",
        });
        Assert.False(upstream.IsConnected);

        await WaitUntilAsync(() => transport.ConnectCount >= 2, "watchdog rebuild");
        await WaitUntilAsync(() => upstream.IsConnected, "connected after watchdog rebuild");

        Assert.Equal(2, transport.ConnectCount);
        Assert.Equal(1, upstream.WatchdogTriggerCount);
        Assert.Equal(0, upstream.ReconnectAttemptCount);
        Assert.False(upstream.RequiresManualIntervention);
        // The rebuild stops the old transport before reconnecting; that code-less disconnect must
        // not erase the real close code that caused the rebuild.
        Assert.Equal("gateway_4009", upstream.LastDisconnectCode);

        var log = logger.AllText;
        Assert.Contains("watchdog rebuilding transport", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gateway_4009", log, StringComparison.Ordinal);
        Assert.Contains("watchdog reconnect succeeded", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watchdog_waits_for_the_threshold_before_touching_the_transport()
    {
        var options = WatchdogOptions(thresholdMs: 600, tickMs: 20);
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock());

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4000",
            ErrorMessage = "unknown gateway error",
        });

        await Task.Delay(200);
        Assert.Equal(1, transport.ConnectCount);

        await WaitUntilAsync(() => transport.ConnectCount >= 2, "rebuild after the configured threshold");
    }

    [Fact]
    public async Task Watchdog_backs_off_and_stops_after_the_attempt_cap_with_manual_intervention()
    {
        var options = WatchdogOptions(thresholdMs: 40, tickMs: 15, maxAttempts: 3);
        options.ReconnectBackoffInitialMs = 40;
        options.ReconnectBackoffMaxMs = 80;

        var transport = new ScriptedGatewayTransport();
        var logger = new RecordingLogger<DiscordChatUpstream>();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock(), new FixedLoggerFactory(logger));

        await upstream.ConnectAsync();
        transport.ConnectSucceeds = false;
        transport.Failure = new TimeoutException($"Unable to reconnect: Authorization: Bot {TokenSentinel}");

        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });

        await WaitUntilAsync(() => upstream.ReconnectExhausted, "reconnect exhaustion");
        await WaitUntilAsync(() => transport.ConnectCount >= 4, "three failed rebuild attempts");

        Assert.Equal(3, upstream.ReconnectAttemptCount);
        Assert.True(upstream.RequiresManualIntervention);
        Assert.Equal(3, transport.FailedAttempts);

        // The watchdog stopped: an exhausted watchdog must not hammer the platform forever.
        var frozen = transport.ConnectCount;
        await Task.Delay(300);
        Assert.Equal(frozen, transport.ConnectCount);
        Assert.True(upstream.WatchdogTask is null || upstream.WatchdogTask.IsCompleted);

        // Exponential backoff: 40ms after attempt 1, 80ms after attempt 2 (never less).
        var attempts = transport.ConnectAttempts;
        Assert.Equal(4, attempts.Count);
        Assert.True(attempts[2] - attempts[1] >= TimeSpan.FromMilliseconds(30),
            $"second attempt fired after {attempts[2] - attempts[1]} instead of honouring backoff");
        Assert.True(attempts[3] - attempts[2] >= TimeSpan.FromMilliseconds(60),
            $"third attempt fired after {attempts[3] - attempts[2]} instead of honouring backoff");

        var log = logger.AllText;
        Assert.Contains("manual intervention required", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnect attempt 2 failed", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retrying in", log, StringComparison.OrdinalIgnoreCase);
        // The failure text carried the bot token; logs must stay token-free.
        Assert.DoesNotContain(TokenSentinel, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchdog_does_not_intervene_when_the_library_recovers_within_the_threshold()
    {
        var options = WatchdogOptions(thresholdMs: 400, tickMs: 20);
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock());

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });

        await Task.Delay(80);
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Ready,
            SelfId = "bot",
            GuildCount = 1,
            ChannelCount = 2,
            LatencyMs = 12,
        });

        await Task.Delay(600);

        Assert.Equal(1, transport.ConnectCount);
        Assert.True(upstream.IsConnected);
        Assert.Equal(0, upstream.WatchdogTriggerCount);
    }

    [Fact]
    public async Task Disconnect_async_stops_the_watchdog_and_leaves_no_background_task()
    {
        var options = WatchdogOptions(thresholdMs: 40, tickMs: 10);
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock());

        await upstream.ConnectAsync();
        Assert.NotNull(upstream.WatchdogTask);

        await upstream.DisconnectAsync();

        Assert.Null(upstream.WatchdogTask);
        Assert.False(upstream.IsConnected);

        var frozen = transport.ConnectCount;
        await Task.Delay(250);
        Assert.Equal(frozen, transport.ConnectCount);
    }

    [Fact]
    public async Task Dispose_stops_the_watchdog_without_a_late_reconnect()
    {
        var options = WatchdogOptions(thresholdMs: 40, tickMs: 10);
        var transport = new ScriptedGatewayTransport();
        var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock());

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });

        upstream.Dispose();

        var frozen = transport.ConnectCount;
        await Task.Delay(250);
        Assert.Equal(frozen, transport.ConnectCount);
        Assert.Null(upstream.WatchdogTask);
    }

    [Fact]
    public async Task Watchdog_is_off_when_reconnect_watchdog_is_disabled()
    {
        var options = WatchdogOptions(thresholdMs: 20, tickMs: 10);
        options.ReconnectWatchdog = false;

        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock());

        await upstream.ConnectAsync();
        Assert.Null(upstream.WatchdogTask);

        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });

        await Task.Delay(250);
        Assert.Equal(1, transport.ConnectCount);
        Assert.False(upstream.IsConnected);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P1 close-code persistence
    // ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("The server sent close 4009 (Session timed out)", 4009)]
    [InlineData("WebSocket closed with close code 4003", 4003)]
    [InlineData("GatewayCloseCode: 4014", 4014)]
    [InlineData("close=4000", 4000)]
    [InlineData("Disconnect code 4002 decode error", 4002)]
    [InlineData("Server requested a reconnect", null)]
    [InlineData("gateway closed (1006)", null)]
    [InlineData("latency 4009 ms", null)]
    [InlineData("message 4009", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Close_code_is_extracted_from_discord_log_text_only_in_a_gateway_context(string? text, int? expected)
        => Assert.Equal(expected, DiscordGatewayCloseCode.TryExtract(text));

    [Fact]
    public void Close_code_is_recovered_from_a_typed_exception_property()
    {
        // Discord.Net socket exceptions expose the raw websocket close code as a property while
        // their message stays generic. The extractor must prefer the typed value.
        Assert.Equal(4009, DiscordGatewayCloseCode.TryExtract(new FakeCloseCodeException(4009)));
        Assert.Equal(4013, DiscordGatewayCloseCode.TryExtract(new FakeCloseCodeException(4013)));

        // A 4-digit number that is not a gateway code is ignored even when typed.
        Assert.Null(DiscordGatewayCloseCode.TryExtract(new FakeCloseCodeException(1006)));
    }

    [Fact]
    public void Close_code_is_recovered_from_the_inner_exception_of_a_generic_reconnect_exception()
    {
        var generic = new InvalidOperationException("Server requested a reconnect",
            new InvalidOperationException("The server sent close 4014 (Disallowed intent(s))"));

        Assert.Equal(4014, DiscordGatewayCloseCode.TryExtract(generic));
        Assert.Null(DiscordGatewayCloseCode.TryExtract(new InvalidOperationException("Server requested a reconnect")));
    }

    [Fact]
    public void Unknown_close_code_keeps_gateway_disconnected_and_is_marked_unknown()
    {
        Assert.Equal("gateway_4009", DiscordGatewayCloseCode.ToDisconnectCode(4009));
        Assert.Equal("gateway_disconnected", DiscordGatewayCloseCode.ToDisconnectCode(null));

        var unknown = DiscordGatewayCloseCode.ToDisconnectMessage(null, "Server requested a reconnect");
        Assert.Contains("Server requested a reconnect", unknown, StringComparison.Ordinal);
        Assert.Contains("closeCode=unknown", unknown, StringComparison.Ordinal);

        var known = DiscordGatewayCloseCode.ToDisconnectMessage(4009, "Server requested a reconnect");
        Assert.Contains("closeCode=4009", known, StringComparison.Ordinal);
        Assert.Contains("session timed out", known, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_close_code_reaches_last_disconnect_code_and_health()
    {
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, new DiscordOptions { SelfId = "bot" }, new TestClock());
        await upstream.ConnectAsync();

        var text = "The server sent close 4009 (Session timed out)";
        var code = DiscordGatewayCloseCode.TryExtract(text);
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = DiscordGatewayCloseCode.ToDisconnectCode(code),
            ErrorMessage = DiscordGatewayCloseCode.ToDisconnectMessage(code, "Server requested a reconnect"),
        });

        // No longer the flattened "gateway_disconnected" constant from the incident report.
        Assert.Equal("gateway_4009", upstream.LastDisconnectCode);
        Assert.Equal("gateway_4009", upstream.Health.LastDisconnectCode);
        Assert.Equal("gateway_4009", upstream.GetStatus().LastDisconnectCode);
        Assert.Contains("Session timed out", upstream.LastDisconnectError!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(DiscordGatewayCloseCode.DisconnectedCode, upstream.LastDisconnectCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P1 connection logging / redaction
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Redaction_scrubs_bot_authorization_and_url_credentials()
    {
        var redacted = DiscordLogRedaction.Redact(
            $"Authorization: Bot {TokenSentinel}; https://user:pa55@discord.example/gateway?token=abc123",
            TokenSentinel);

        Assert.DoesNotContain(TokenSentinel, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pa55", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);

        // Close codes and plain reasons pass through untouched.
        Assert.Equal("gateway_4009", DiscordLogRedaction.Redact("gateway_4009"));
        Assert.Equal("gateway closed (1006)", DiscordLogRedaction.Redact("gateway closed (1006)"));
    }

    [Fact]
    public async Task Connection_lifecycle_logs_include_source_code_and_no_token()
    {
        var options = WatchdogOptions(thresholdMs: 30, tickMs: 10);
        var transport = new ScriptedGatewayTransport();
        var logger = new RecordingLogger<DiscordChatUpstream>();
        using var upstream = new DiscordChatUpstream(Src, transport, options, new RealClock(), new FixedLoggerFactory(logger));

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = $"Server requested a reconnect; Authorization: Bot {TokenSentinel}",
        });
        await WaitUntilAsync(() => transport.ConnectCount >= 2, "watchdog rebuild");

        var log = logger.AllText;

        // Lifecycle is now visible in the journal (the incident's third defect: zero log lines).
        Assert.Contains("upstream connecting", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upstream connected", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watchdog started", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upstream disconnected", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gateway_4009", log, StringComparison.Ordinal);
        Assert.Contains("source=discord", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt=1", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lastCode=gateway_4009", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TokenSentinel, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disconnect_reason_from_a_poisoned_transport_message_is_redacted_in_health_too()
    {
        var transport = new ScriptedGatewayTransport();
        using var upstream = new DiscordChatUpstream(Src, transport, new DiscordOptions { SelfId = "bot" }, new TestClock());
        await upstream.ConnectAsync();

        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = $"WebSocket closed; Authorization: Bot {TokenSentinel}",
        });

        Assert.DoesNotContain(TokenSentinel, upstream.LastDisconnectError!, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenSentinel, upstream.Health.LastDisconnectError!, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenSentinel, upstream.GetStatus().LastDisconnectError!, StringComparison.Ordinal);
        Assert.Equal("gateway_4009", upstream.LastDisconnectCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // DI compatibility: the new ILogger parameters must stay optional
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Transport_and_upstream_resolve_without_a_logging_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DiscordOptions { SelfId = "bot" });
        services.AddSingleton(DiscordBotToken.From(TokenSentinel));
        services.AddSingleton<IClock>(new RealClock());
        services.AddSingleton(Src);
        services.AddSingleton<IDiscordTransport, DiscordSocketTransport>();
        services.AddSingleton<DiscordChatUpstream>();

        using var provider = services.BuildServiceProvider();

        // The plugin DI container may not register Microsoft.Extensions.Logging at all; the
        // optional logger parameters must fall back to a no-op logger instead of failing DI.
        using var transport = provider.GetRequiredService<IDiscordTransport>() as IDisposable;
        var upstream = provider.GetRequiredService<DiscordChatUpstream>();
        Assert.NotNull(transport);
        Assert.NotNull(upstream);
        Assert.False(upstream.IsConnected);
        Assert.Equal(0, upstream.Health.ReconnectCount);
    }

    [Fact]
    public async Task Upstream_logs_flow_through_the_ILoggerFactory_exposed_by_the_plugin_container()
    {
        var transport = new ScriptedGatewayTransport();
        var logger = new RecordingLogger<DiscordChatUpstream>();

        var services = new ServiceCollection();
        services.AddSingleton(new DiscordOptions
        {
            SelfId = "bot",
            ReconnectWatchdogThresholdMs = 40,
            ReconnectWatchdogTickMs = 10,
            ReconnectBackoffInitialMs = 20,
            ReconnectBackoffMaxMs = 40,
        });
        services.AddSingleton<IClock>(new RealClock());
        services.AddSingleton(Src);
        services.AddSingleton<IDiscordTransport>(transport);
        services.AddSingleton<ILoggerFactory>(new FixedLoggerFactory(logger));
        services.AddSingleton<DiscordChatUpstream>();

        using var provider = services.BuildServiceProvider();
        var upstream = provider.GetRequiredService<DiscordChatUpstream>();

        await upstream.ConnectAsync();
        transport.Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_4009",
            ErrorMessage = "Server requested a reconnect",
        });
        await WaitUntilAsync(() => transport.ConnectCount >= 2, "watchdog rebuild through DI");

        // This is the wiring production uses: NpsPluginRuntime registers ILoggerFactory only.
        Assert.Contains("watchdog rebuilding transport", logger.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gateway_4009", logger.AllText, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static DiscordOptions WatchdogOptions(int thresholdMs = 80, int tickMs = 20, int maxAttempts = 10)
        => new()
        {
            SelfId = "bot",
            ReconnectWatchdogThresholdMs = thresholdMs,
            ReconnectWatchdogTickMs = tickMs,
            ReconnectBackoffInitialMs = 20,
            ReconnectBackoffMaxMs = 80,
            ReconnectMaxAttempts = maxAttempts,
        };

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WaitTimeout)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"Condition '{because}' was not met within {WaitTimeout.TotalMilliseconds}ms.");
    }

    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>Controllable transport: every connect/disconnect is counted and scripted.</summary>
    private sealed class ScriptedGatewayTransport : IDiscordTransport, IDisposable
    {
        private readonly Subject<DiscordTransportEvent> _events = new();
        private readonly Lock _attemptsGate = new();
        private readonly List<DateTimeOffset> _connectAttempts = [];
        private int _connectCount;
        private int _disconnectCount;
        private int _failedAttempts;

        public string SelfId { get; init; } = "bot";

        /// <summary>False makes <see cref="ConnectAsync"/> throw, like a gateway that refuses to return.</summary>
        public bool ConnectSucceeds { get; set; } = true;

        public Exception Failure { get; set; } = new TimeoutException("simulated gateway connect failure");

        public IObservable<DiscordTransportEvent> Events => _events;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public int DisconnectCount => Volatile.Read(ref _disconnectCount);

        public int FailedAttempts => Volatile.Read(ref _failedAttempts);

        public IReadOnlyList<DateTimeOffset> ConnectAttempts
        {
            get
            {
                lock (_attemptsGate)
                    return _connectAttempts.ToArray();
            }
        }

        public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _connectCount);
            lock (_attemptsGate)
                _connectAttempts.Add(DateTimeOffset.UtcNow);

            await Task.Yield();

            if (!ConnectSucceeds)
            {
                Interlocked.Increment(ref _failedAttempts);
                throw Failure;
            }

            Emit(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Ready,
                SelfId = SelfId,
                SelfName = "FakeBot",
                GuildCount = 1,
                ChannelCount = 2,
                LatencyMs = 12,
            });
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _disconnectCount);
            Emit(new DiscordTransportEvent { Kind = DiscordTransportEventKind.Disconnected });
            return ValueTask.CompletedTask;
        }

        public ValueTask<DiscordSendResult> SendMessageAsync(DiscordSendMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success($"sent-{request.ChannelId}"));

        public ValueTask<DiscordSendResult> UpdateMessageAsync(DiscordUpdateMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success(request.MessageId));

        public ValueTask<DiscordSendResult> DeleteMessageAsync(DiscordDeleteMessage request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success(request.MessageId));

        public ValueTask TriggerTypingAsync(DiscordConversationRef conversation, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<DiscordSendResult> RespondInteractionAsync(DiscordInteractionResponse response, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(DiscordSendResult.Success($"interaction-{response.InteractionId}"));

        public void Emit(DiscordTransportEvent transportEvent) => _events.OnNext(transportEvent);

        public void Dispose() => _events.Dispose();
    }

    /// <summary>Host-shaped factory: every category resolves to the same recording sink.</summary>
    private sealed class FixedLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _entries = [];
        private readonly List<LogLevel> _levels = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToArray();
            }
        }

        public IReadOnlyList<LogLevel> Levels
        {
            get
            {
                lock (_gate)
                    return _levels.ToArray();
            }
        }

        public string AllText
        {
            get
            {
                lock (_gate)
                    return string.Join("\n", _entries);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (exception is not null)
                line = $"{line} | {exception.GetType().Name}: {exception.Message}";

            lock (_gate)
            {
                _entries.Add(line);
                _levels.Add(logLevel);
            }
        }
    }

    /// <summary>Stand-in for Discord.Net socket exceptions that expose the raw close code.</summary>
    private sealed class FakeCloseCodeException(int closeCode) : Exception("WebSocket connection was closed")
    {
        public int CloseCode { get; } = closeCode;
    }
}
