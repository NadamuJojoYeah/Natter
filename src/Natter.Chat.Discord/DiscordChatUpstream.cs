using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Natter.Abstractions.Ports;

namespace Natter.Chat.Discord;

/// <summary>
/// Discord transport upstream: source identity, allowlists, NLMP event mapping, command
/// execution and capability declaration. Networking lives in <see cref="IDiscordTransport"/>.
/// </summary>
public sealed class DiscordChatUpstream : IChatUpstream, IChatUpstreamHealth, IDisposable
{
    private readonly IDiscordTransport _transport;
    private readonly DiscordOptions _options;
    private readonly IClock _clock;
    private readonly DiscordEventMapper _mapper;
    private readonly ILogger<DiscordChatUpstream> _logger;
    private readonly Subject<ChatEvent> _events = new();
    private readonly IObservable<ChatEvent> _synchronizedEvents;
    private readonly ChatIdempotencyCache _idempotency;
    private readonly Capabilities _capabilities;
    private readonly Lock _gate = new();
    private readonly Lock _watchdogGate = new();
    private IDisposable? _transportSubscription;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private TaskCompletionSource _connectedSignal = NewConnectedSignal();
    private long _sequence;
    private bool _connected;
    private bool _disposed;
    private int _sendFailures;
    private string? _lastSendError;
    private int _reconnectCount;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastConnectedAt;
    private DateTimeOffset? _lastDisconnectAt;
    private DateTimeOffset? _disconnectedSince;
    private DateTimeOffset? _nextReconnectAt;
    private string? _lastReconnectError;
    private int _reconnectAttempts;
    private int _watchdogTriggers;
    private int _reconnectExhausted;
    private int _reconnectInProgress;

    public DiscordChatUpstream(
        Source source,
        IDiscordTransport transport,
        DiscordOptions options,
        IClock clock,
        ILoggerFactory? loggerFactory = null)
    {
        _idempotency = new ChatIdempotencyCache(clock: () => _clock.UtcNow);
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        // Plugins resolve their services from a container that exposes ILoggerFactory but not an
        // open ILogger<> registration, so the factory (not ILogger<T>) is the injectable seam.
        _logger = loggerFactory?.CreateLogger<DiscordChatUpstream>() ?? NullLogger<DiscordChatUpstream>.Instance;
        _mapper = new DiscordEventMapper(source, options);
        _synchronizedEvents = Subject.Synchronize(_events);
        _capabilities = DiscordCapabilities.Create(options);
    }

    public Source Source { get; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _connected;
        }
    }

    public IObservable<ChatEvent> Events => _synchronizedEvents;

    public string? LastError { get; private set; }

    /// <summary>OBS-019: reason of the most recent disconnect, kept across a successful reconnect.</summary>
    public string? LastDisconnectError { get; private set; }

    /// <summary>OBS-019: stable code of the most recent disconnect (transport code or fallback).</summary>
    public string? LastDisconnectCode { get; private set; }

    /// <summary>Consecutive failed watchdog rebuilds; reset to zero by a successful connect.</summary>
    public int ReconnectAttemptCount => Volatile.Read(ref _reconnectAttempts);

    /// <summary>How many times the watchdog rebuilt the transport since the last successful connect.</summary>
    public int WatchdogTriggerCount => Volatile.Read(ref _watchdogTriggers);

    /// <summary>When the next rebuild attempt is allowed (after backoff); null when not waiting.</summary>
    public DateTimeOffset? NextReconnectAt
    {
        get
        {
            lock (_gate)
                return _nextReconnectAt;
        }
    }

    /// <summary>Reason reported by the most recent failed rebuild attempt.</summary>
    public string? LastReconnectError
    {
        get
        {
            lock (_gate)
                return _lastReconnectError;
        }
    }

    /// <summary>
    /// True when the watchdog gave up after <see cref="DiscordOptions.ReconnectMaxAttempts"/>
    /// consecutive failures. The service stays up but cannot self-heal until an operator steps in
    /// (or the host calls ConnectAsync again).
    /// </summary>
    public bool ReconnectExhausted => Volatile.Read(ref _reconnectExhausted) != 0;

    /// <summary>Explicit operator-facing flag raised when the watchdog stopped retrying.</summary>
    public bool RequiresManualIntervention => ReconnectExhausted;

    /// <summary>Diagnostics: the background watchdog task, null once it exited or was never started.</summary>
    public Task? WatchdogTask
    {
        get
        {
            lock (_watchdogGate)
                return _watchdogTask;
        }
    }

    public int? GuildCount { get; private set; }

    public int? ChannelCount { get; private set; }

    public int? LatencyMs { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(Source.SelfId))
            throw new InvalidOperationException("Discord Source.SelfId must be configured before connecting.");

        lock (_gate)
        {
            if (_connected)
                return;

            _transportSubscription ??= _transport.Events.Subscribe(new TransportObserver(this));
        }

        _logger?.LogInformation(
            "Discord upstream connecting (source={Source}, watchdog={Watchdog}, thresholdMs={ThresholdMs}, "
            + "maxAttempts={MaxAttempts}).",
            Source.Key,
            _options.ReconnectWatchdog,
            _options.ReconnectWatchdogThresholdMs,
            _options.ReconnectMaxAttempts);

        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (!IsConnected)
            {
                await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Discord transport failed to become ready: {LastError ?? "no Ready event"}");
            }

            // A caller-driven connect is the clean slate: drop watchdog counters from earlier
            // failures and (re)arm the watchdog for the connection that just came up.
            ResetReconnectState("connect succeeded");
            StartWatchdog();
            PublishStatus(UpstreamState.Ready);

            _logger?.LogInformation(
                "Discord upstream connected (source={Source}, guilds={GuildCount}, channels={ChannelCount}, "
                + "latencyMs={LatencyMs}, watchdog={Watchdog}).",
                Source.Key,
                GuildCount,
                ChannelCount,
                LatencyMs,
                WatchdogTask is { IsCompleted: false });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError("connect_failed", DiscordLogRedaction.Redact(ex.Message));
            PublishStatus(UpstreamState.Error, LastError);
            // Exception objects are logged as a redacted string on purpose: exception text can
            // echo request details, and the journal must never contain the bot token.
            _logger?.LogError(
                "Discord upstream connect failed (source={Source}, errorType={ErrorType}, error={Error}).",
                Source.Key,
                ex.GetType().Name,
                LastError);
            throw;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Stop the watchdog first: a shutdown must not race a rebuild and must not leave a
        // background task holding the transport.
        await StopWatchdogAsync().ConfigureAwait(false);

        lock (_gate)
        {
            _transportSubscription?.Dispose();
            _transportSubscription = null;
            _connected = false;
            _disconnectedSince = null;
        }

        _logger?.LogInformation("Discord upstream disconnecting (source={Source}).", Source.Key);

        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = DiscordLogRedaction.Redact(ex.Message);
            _logger?.LogWarning(
                "Discord upstream transport disconnect failed (source={Source}, errorType={ErrorType}, error={Error}).",
                Source.Key,
                ex.GetType().Name,
                LastError);
        }

        PublishStatus(UpstreamState.Disconnected);
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_capabilities);

    public async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();

        if (!string.Equals(command.Source.Key, Source.Key, StringComparison.Ordinal))
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                $"Command source '{command.Source.Key}' does not match upstream '{Source.Key}'."));

        var idempotencyKey = command.IdempotencyKey;
        var reserved = false;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            // S2-A1: reserve before publishing so concurrent retries cannot double-send.
            switch (_idempotency.TryReserve(idempotencyKey, out var cached))
            {
                case ChatIdempotencyCache.Reservation.Cached:
                    return cached!;
                case ChatIdempotencyCache.Reservation.InFlight:
                    return ChatAck.Failure(command, ChatError.Create(
                        ChatErrorCodes.Conflict,
                        $"A command with idempotency key '{idempotencyKey}' is already in flight."));
                default:
                    reserved = true;
                    break;
            }
        }

        ChatAck ack;
        try
        {
            ack = await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (reserved)
                _idempotency.Rollback(idempotencyKey);
            throw;
        }

        if (reserved)
        {
            if (ack.Ok)
                _idempotency.Commit(idempotencyKey, ack);
            else
                _idempotency.Rollback(idempotencyKey);
        }

        return ack;
    }

    public UpstreamHealth Health
        => new(
            Source.Key,
            IsConnected,
            _lastEventAt,
            _lastConnectedAt,
            _lastDisconnectAt,
            _reconnectCount,
            LastError,
            _sendFailures,
            _lastSendError,
            LastDisconnectError,
            LastDisconnectCode);

    public DiscordStatusSnapshot GetStatus()
        => new(
            Source,
            IsConnected,
            LastError,
            GuildCount,
            ChannelCount,
            LatencyMs,
            _capabilities,
            _options.Guilds.ToArray(),
            _options.Channels.ToArray(),
            _options.Users.ToArray(),
            _options.RequireMention,
            _options.SlashCommands,
            _sendFailures,
            _lastSendError,
            _lastEventAt,
            _lastConnectedAt,
            _lastDisconnectAt,
            _reconnectCount,
            LastDisconnectError,
            LastDisconnectCode,
            _options.ReconnectWatchdog,
            ReconnectAttemptCount,
            WatchdogTriggerCount,
            NextReconnectAt,
            LastReconnectError,
            RequiresManualIntervention);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopWatchdogSynchronously();
        _transportSubscription?.Dispose();
        _transportSubscription = null;
        _events.OnCompleted();
        _events.Dispose();
        if (_transport is IDisposable disposable)
            disposable.Dispose();
    }

    private async ValueTask<ChatAck> ExecuteCoreAsync(ChatCommand command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command)
            {
                case MessageSendCommand send:
                    return await SendAsync(send, cancellationToken).ConfigureAwait(false);
                case MessageUpdateCommand update:
                    return await UpdateAsync(update, cancellationToken).ConfigureAwait(false);
                case MessageDeleteCommand delete:
                    return await DeleteAsync(delete, cancellationToken).ConfigureAwait(false);
                case TypingStartCommand typing:
                    return await TypingAsync(typing, cancellationToken).ConfigureAwait(false);
                case InteractionRespondCommand respond:
                    return await RespondAsync(respond, cancellationToken).ConfigureAwait(false);
                default:
                    return ChatAck.Failure(command, ChatError.Create(
                        ChatErrorCodes.Unsupported,
                        $"Discord upstream does not support command '{command.Type}'."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.Internal, ex.Message));
        }
    }

    private async ValueTask<ChatAck> SendAsync(MessageSendCommand command, CancellationToken cancellationToken)
    {
        var payload = ChatCapabilityDegrader.DegradeSend(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var chunks = ChatTextChunker.Chunk(payload.Content, _capabilities.MaxMessageLength);
        var messageIds = new List<string>(chunks.Count);
        var mentionIds = OutboundMentionIds(payload.Content);

        for (var index = 0; index < chunks.Count; index++)
        {
            var request = new DiscordSendMessage(
                payload.Conversation.Id,
                chunks[index].Text,
                index == 0 ? payload.ReplyTo : null,
                mentionIds);

            var result = await _transport.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                Interlocked.Increment(ref _sendFailures);
                _lastSendError = $"{result.ErrorCode ?? "unknown"}: channel {request.ChannelId}";
                return ChatAck.Failure(command, ToError(result));
            }

            if (!string.IsNullOrWhiteSpace(result.MessageId))
                messageIds.Add(result.MessageId);
        }

        return ChatAck.Success(command, messageIds, _clock.UtcNow);
    }

    private async ValueTask<ChatAck> UpdateAsync(MessageUpdateCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Edit)
            return Unsupported(command, "message.update");

        var payload = ChatCapabilityDegrader.DegradeUpdate(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var mentionIds = OutboundMentionIds(payload.Content);

        var result = await _transport.UpdateMessageAsync(
            new DiscordUpdateMessage(payload.Conversation.Id, payload.MessageId, payload.Content.Text, mentionIds),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, result.MessageId is null ? null : [result.MessageId], _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private async ValueTask<ChatAck> DeleteAsync(MessageDeleteCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Delete)
            return Unsupported(command, "message.delete");

        var result = await _transport.DeleteMessageAsync(
            new DiscordDeleteMessage(command.Payload.Conversation.Id, command.Payload.MessageId),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, deliveredAt: _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private async ValueTask<ChatAck> TypingAsync(TypingStartCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.Typing)
            return Unsupported(command, "typing.start");

        await _transport.TriggerTypingAsync(
            new DiscordConversationRef(command.Payload.Conversation.Id, command.Payload.Conversation.GuildId),
            cancellationToken).ConfigureAwait(false);

        return ChatAck.Success(command, deliveredAt: _clock.UtcNow);
    }

    private async ValueTask<ChatAck> RespondAsync(InteractionRespondCommand command, CancellationToken cancellationToken)
    {
        if (!_capabilities.SlashCommands && !_capabilities.Buttons && !_capabilities.Modals)
            return Unsupported(command, "interaction.respond");

        var payload = ChatCapabilityDegrader.DegradeRespond(command, _capabilities).Payload;
        payload.Content.EnsureValid();

        var result = await _transport.RespondInteractionAsync(
            new DiscordInteractionResponse(
                payload.InteractionId,
                payload.Content.Text,
                payload.Ephemeral,
                payload.UpdateOriginal),
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ChatAck.Success(command, result.MessageId is null ? null : [result.MessageId], _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private void OnTransportEvent(DiscordTransportEvent transportEvent)
    {
        _lastEventAt = _clock.UtcNow;
        if (transportEvent.Kind == DiscordTransportEventKind.Ready)
            _lastConnectedAt = _clock.UtcNow;
        if (transportEvent.Kind == DiscordTransportEventKind.Disconnected)
        {
            _lastDisconnectAt = _clock.UtcNow;
            Interlocked.Increment(ref _reconnectCount);
        }
        switch (transportEvent.Kind)
        {
            case DiscordTransportEventKind.Ready:
                OnReady(transportEvent);
                break;
            case DiscordTransportEventKind.MessageCreated:
                if (_options.MessageCommands)
                {
                    var message = PublishMappedMessage(transportEvent, isEdit: false);
                    if (message is not null && _options.Typing)
                        _ = TriggerTypingBestEffortAsync(message.Conversation);
                }
                break;
            case DiscordTransportEventKind.MessageUpdated:
                if (_options.MessageCommands)
                    PublishMappedMessage(transportEvent, isEdit: true);
                break;
            case DiscordTransportEventKind.MessageDeleted:
                PublishDeleted(transportEvent);
                break;
            case DiscordTransportEventKind.InteractionCreated:
                PublishInteraction(transportEvent);
                break;
            case DiscordTransportEventKind.Error:
                LastError = DiscordLogRedaction.Redact(
                    $"{transportEvent.ErrorCode ?? "transport_error"}: {transportEvent.ErrorMessage ?? "Discord transport error."}");
                if (transportEvent.Fatal)
                {
                    lock (_gate)
                    {
                        _connected = false;
                        _disconnectedSince ??= _clock.UtcNow;
                    }
                }
                _logger?.LogWarning(
                    "Discord upstream transport error (source={Source}, code={Code}, fatal={Fatal}, reason={Reason}, "
                    + "downSince={DownSince}).",
                    Source.Key,
                    transportEvent.ErrorCode ?? "transport_error",
                    transportEvent.Fatal,
                    LastError,
                    _disconnectedSince);
                PublishStatus(UpstreamState.Error, LastError);
                break;
            case DiscordTransportEventKind.Disconnected:
                lock (_gate)
                {
                    _connected = false;
                    // The watchdog measures downtime from the first disconnect event; repeated
                    // disconnect noise while already down must not extend or reset the window.
                    _disconnectedSince ??= _clock.UtcNow;
                }
                // OBS-019: disconnect cause is persisted (and never cleared by Ready), so /health
                // can explain a reconnect instead of reporting "disconnected without reason".
                var incomingCode = string.IsNullOrWhiteSpace(transportEvent.ErrorCode)
                    ? null
                    : transportEvent.ErrorCode;
                var incomingReason = DiscordLogRedaction.Redact(
                    string.IsNullOrWhiteSpace(transportEvent.ErrorMessage)
                        ? "Discord transport disconnected."
                        : transportEvent.ErrorMessage);

                if (incomingCode is not null || LastDisconnectCode is null)
                {
                    LastDisconnectCode = incomingCode ?? "transport_disconnected";
                    LastDisconnectError = incomingReason;
                }
                else
                {
                    // A code-less disconnect (the watchdog stopping the old transport before a
                    // rebuild, or a transport that reports no detail) must not erase the real
                    // close code that caused the outage.
                    _logger?.LogDebug(
                        "Discord upstream reported a disconnect without a code (source={Source}); "
                        + "keeping the known cause {Code}.",
                        Source.Key,
                        LastDisconnectCode);
                }

                // LastError keeps its "current/latest error" semantics; the disconnect reason lives
                // in LastDisconnectError/LastDisconnectCode and survives a reconnect (OBS-019).
                if (incomingCode is not null || !string.IsNullOrWhiteSpace(transportEvent.ErrorMessage))
                    LastError = incomingReason;
                _logger?.LogWarning(
                    "Discord upstream disconnected (source={Source}, code={Code}, reason={Reason}, "
                    + "reconnectCount={ReconnectCount}, downSince={DownSince}).",
                    Source.Key,
                    LastDisconnectCode,
                    LastDisconnectError,
                    _reconnectCount,
                    _disconnectedSince);
                PublishStatus(UpstreamState.Disconnected, LastDisconnectError);
                break;
        }
    }

    private void OnReady(DiscordTransportEvent transportEvent)
    {
        GuildCount = transportEvent.GuildCount;
        ChannelCount = transportEvent.ChannelCount;
        LatencyMs = transportEvent.LatencyMs;

        if (!string.IsNullOrWhiteSpace(transportEvent.SelfId)
            && !string.Equals(transportEvent.SelfId, Source.SelfId, StringComparison.Ordinal))
        {
            SetError("self_id_mismatch", $"Discord login user '{transportEvent.SelfId}' does not match configured selfId '{Source.SelfId}'.");
            return;
        }

        LastError = null;
        lock (_gate)
        {
            _connected = true;
            _disconnectedSince = null;
        }

        // A Ready event is the definition of a healthy gateway: reset the watchdog counters here
        // (not only in the rebuild path) so health never reports a stale attempt count after the
        // library itself recovered, and so connected=true implies attempts==0.
        if (ReconnectAttemptCount != 0 || ReconnectExhausted)
            ResetReconnectState("gateway became ready again");

        // Releases the watchdog rebuild currently waiting for this Ready event.
        _connectedSignal.TrySetResult();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // P0 reconnect watchdog (2026-11-22 incident): Discord.Net's internal _reconnect loop can
    // terminate for good and leave the host with connected=false forever. This watchdog is the
    // external supervisor that rebuilds the transport (StopAsync → LoginAsync → StartAsync,
    // expressed as DisconnectAsync + ConnectAsync) with backoff, and stops after a hard cap.
    // ─────────────────────────────────────────────────────────────────────────────

    private static readonly TimeSpan WatchdogShutdownTimeout = TimeSpan.FromSeconds(5);

    private TimeSpan WatchdogThreshold
        => TimeSpan.FromMilliseconds(Math.Max(1, _options.ReconnectWatchdogThresholdMs));

    private TimeSpan ConnectedSignalGrace
        => TimeSpan.FromSeconds(Math.Max(5, _options.ReadyTimeoutSeconds + 5));

    private void StartWatchdog()
    {
        if (!_options.ReconnectWatchdog)
            return;

        lock (_watchdogGate)
        {
            if (_watchdogTask is { IsCompleted: false })
                return;

            _watchdogCts?.Dispose();
            _watchdogCts = new CancellationTokenSource();
            var token = _watchdogCts.Token;
            _watchdogTask = Task.Run(() => WatchdogLoopAsync(token), CancellationToken.None);
        }

        _logger?.LogInformation(
            "Discord gateway watchdog started (source={Source}, thresholdMs={ThresholdMs}, tickMs={TickMs}, "
            + "backoffMs={BackoffMs}..{BackoffMaxMs}, maxAttempts={MaxAttempts}).",
            Source.Key,
            _options.ReconnectWatchdogThresholdMs,
            (long)ResolveWatchdogTick().TotalMilliseconds,
            _options.ReconnectBackoffInitialMs,
            _options.ReconnectBackoffMaxMs,
            _options.ReconnectMaxAttempts);
    }

    private async ValueTask StopWatchdogAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_watchdogGate)
        {
            cts = _watchdogCts;
            task = _watchdogTask;
            _watchdogCts = null;
            _watchdogTask = null;
        }

        if (cts is null && task is null)
            return;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(WatchdogShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning(
                    "Discord gateway watchdog did not stop within {TimeoutMs}ms (source={Source}).",
                    (long)WatchdogShutdownTimeout.TotalMilliseconds,
                    Source.Key);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private void StopWatchdogSynchronously()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_watchdogGate)
        {
            cts = _watchdogCts;
            task = _watchdogTask;
            _watchdogCts = null;
            _watchdogTask = null;
        }

        if (cts is null && task is null)
            return;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (task is not null)
        {
            try
            {
                if (!task.Wait(WatchdogShutdownTimeout))
                {
                    _logger?.LogWarning(
                        "Discord gateway watchdog did not stop within {TimeoutMs}ms while disposing (source={Source}).",
                        (long)WatchdogShutdownTimeout.TotalMilliseconds,
                        Source.Key);
                }
            }
            catch (AggregateException)
            {
                // The loop reports its own failure; disposal must never throw.
            }
        }

        cts?.Dispose();
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        var tick = ResolveWatchdogTick();
        try
        {
            using var timer = new PeriodicTimer(tick);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await WatchdogTickAsync(cancellationToken).ConfigureAwait(false))
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            // Clean host shutdown (DisconnectAsync/Dispose): no leak, no extra attempt.
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                "Discord gateway watchdog loop failed (source={Source}, errorType={ErrorType}, error={Error}).",
                Source.Key,
                ex.GetType().Name,
                DiscordLogRedaction.Redact(ex.Message));
        }
    }

    private async ValueTask<bool> WatchdogTickAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            // A successful reconnect (ours or the library's) resets the failure counters.
            if (ReconnectAttemptCount != 0 || ReconnectExhausted)
                ResetReconnectState("gateway became ready again");
            return true;
        }

        DateTimeOffset? disconnectedSince;
        lock (_gate)
            disconnectedSince = _disconnectedSince;

        // Never connected yet: ConnectAsync owns that failure path, not the watchdog.
        if (disconnectedSince is null)
            return true;

        var downFor = _clock.UtcNow - disconnectedSince.Value;
        if (downFor < WatchdogThreshold || !CanAttemptReconnectNow())
            return true;

        return await AttemptReconnectAsync(downFor, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> AttemptReconnectAsync(TimeSpan downFor, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
            return true;

        try
        {
            var attempt = Interlocked.Increment(ref _reconnectAttempts);
            Interlocked.Increment(ref _watchdogTriggers);
            var lastCode = LastDisconnectCode ?? "unknown";

            _logger?.LogWarning(
                "Discord gateway watchdog rebuilding transport (source={Source}, attempt={Attempt}, "
                + "maxAttempts={MaxAttempts}, downForMs={DownForMs}, lastCode={LastCode}, lastError={LastError}).",
                Source.Key,
                attempt,
                _options.ReconnectMaxAttempts,
                (long)downFor.TotalMilliseconds,
                lastCode,
                LastDisconnectError);

            // Arm the Ready signal before touching the transport so a fast reconnect cannot be
            // missed: only a Ready event proves the gateway is usable again.
            var ready = ResetConnectedSignal();

            try
            {
                try
                {
                    await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug(
                        "Discord gateway watchdog could not stop the old transport before rebuilding "
                        + "(source={Source}, errorType={ErrorType}, error={Error}).",
                        Source.Key,
                        ex.GetType().Name,
                        DiscordLogRedaction.Redact(ex.Message));
                }

                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await ready.Task.WaitAsync(ConnectedSignalGrace, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return RecordReconnectFailure(ex, attempt, lastCode);
            }

            ResetReconnectState("watchdog rebuild succeeded");
            _logger?.LogInformation(
                "Discord gateway watchdog reconnect succeeded (source={Source}, attempt={Attempt}, "
                + "guilds={GuildCount}, latencyMs={LatencyMs}).",
                Source.Key,
                attempt,
                GuildCount,
                LatencyMs);

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInProgress, 0);
        }
    }

    private bool RecordReconnectFailure(Exception exception, int attempt, string lastCode)
    {
        var reason = DiscordLogRedaction.Redact(exception.Message);
        lock (_gate)
            _lastReconnectError = reason;

        var maxAttempts = Math.Max(1, _options.ReconnectMaxAttempts);
        if (attempt >= maxAttempts)
        {
            Volatile.Write(ref _reconnectExhausted, 1);
            _logger?.LogError(
                "Discord gateway reconnect exhausted after {Attempts} attempt(s); manual intervention required "
                + "(source={Source}, lastCode={LastCode}, errorType={ErrorType}, lastError={LastError}). "
                + "The watchdog stopped retrying.",
                attempt,
                Source.Key,
                lastCode,
                exception.GetType().Name,
                reason);
            PublishStatus(UpstreamState.Error, "reconnect_exhausted: manual intervention required");
            return false;
        }

        var delay = ComputeBackoff(attempt);
        lock (_gate)
            _nextReconnectAt = _clock.UtcNow + delay;

        _logger?.LogWarning(
            "Discord gateway reconnect attempt {Attempt} failed; retrying in {DelayMs}ms "
            + "(source={Source}, lastCode={LastCode}, errorType={ErrorType}, lastError={LastError}).",
            attempt,
            (long)delay.TotalMilliseconds,
            Source.Key,
            lastCode,
            exception.GetType().Name,
            reason);

        return true;
    }

    private void ResetReconnectState(string reason)
    {
        var previousAttempts = Interlocked.Exchange(ref _reconnectAttempts, 0);
        Volatile.Write(ref _reconnectExhausted, 0);

        DateTimeOffset? previousNext;
        lock (_gate)
        {
            previousNext = _nextReconnectAt;
            _nextReconnectAt = null;
            _lastReconnectError = null;
        }

        if (previousAttempts > 0 || previousNext is not null)
        {
            _logger?.LogInformation(
                "Discord gateway reconnect state reset after {Reason} (source={Source}, clearedAttempts={Attempts}).",
                reason,
                Source.Key,
                previousAttempts);
        }
    }

    private bool CanAttemptReconnectNow()
    {
        DateTimeOffset? next;
        lock (_gate)
            next = _nextReconnectAt;
        return next is null || _clock.UtcNow >= next.Value;
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var initial = Math.Max(1, _options.ReconnectBackoffInitialMs);
        var max = Math.Max(initial, _options.ReconnectBackoffMaxMs);
        var shift = Math.Clamp(attempt - 1, 0, 20);
        var scaled = (long)initial << shift;
        return TimeSpan.FromMilliseconds(Math.Clamp(scaled, (long)initial, (long)max));
    }

    private TimeSpan ResolveWatchdogTick()
    {
        if (_options.ReconnectWatchdogTickMs > 0)
            return TimeSpan.FromMilliseconds(Math.Clamp(_options.ReconnectWatchdogTickMs, 10, 60_000));

        var thresholdMs = Math.Max(1, _options.ReconnectWatchdogThresholdMs);
        return TimeSpan.FromMilliseconds(Math.Clamp(thresholdMs / 4.0, 25, 5_000));
    }

    private TaskCompletionSource ResetConnectedSignal()
    {
        var signal = NewConnectedSignal();
        lock (_gate)
            _connectedSignal = signal;
        return signal;
    }

    private static TaskCompletionSource NewConnectedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Message? PublishMappedMessage(DiscordTransportEvent transportEvent, bool isEdit)
    {
        if (transportEvent.Message is null)
            return null;

        Message? message;
        try
        {
            message = _mapper.MapMessage(transportEvent.Message, isEdit);
        }
        catch (NlmpValidationException ex)
        {
            SetError("mapping_failed", ex.Message, fatal: false);
            return null;
        }

        if (message is null)
            return null;

        var occurredAt = isEdit
            ? message.EditedAt ?? message.CreatedAt
            : message.CreatedAt;

        ChatEvent chatEvent = isEdit
            ? new MessageUpdatedEvent
            {
                // S2-A13: edits are keyed by revision timestamp, so a second edit of the same
                // message is not swallowed by dedupe while a replay of one edit stays stable.
                Id = ChatEventId.DeriveEdit(Source, message.Id, occurredAt),
                Source = Source,
                Sequence = Interlocked.Increment(ref _sequence),
                OccurredAt = occurredAt,
                ReceivedAt = _clock.UtcNow,
                Payload = new MessagePayload(message),
            }
            : new MessageCreatedEvent
            {
                Id = $"discord:message:{message.Id}:created",
                Source = Source,
                Sequence = Interlocked.Increment(ref _sequence),
                OccurredAt = occurredAt,
                ReceivedAt = _clock.UtcNow,
                Payload = new MessagePayload(message),
            };

        _events.OnNext(chatEvent);
        return message;
    }

    private async Task TriggerTypingBestEffortAsync(Conversation conversation)
    {
        try
        {
            await _transport.TriggerTypingAsync(
                new DiscordConversationRef(conversation.Id, conversation.GuildId)).ConfigureAwait(false);
        }
        catch
        {
            // Typing indicators are cosmetic.
        }
    }

    private void PublishDeleted(DiscordTransportEvent transportEvent)
    {
        if (string.IsNullOrWhiteSpace(transportEvent.MessageId) || transportEvent.Channel is null)
            return;

        var conversation = _mapper.MapConversation(transportEvent.Channel);
        _events.OnNext(new MessageDeletedEvent
        {
            Id = $"discord:message:{transportEvent.MessageId}:deleted",
            Source = Source,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = _clock.UtcNow,
            ReceivedAt = _clock.UtcNow,
            Payload = new MessageDeletedPayload(conversation, transportEvent.MessageId, _clock.UtcNow),
        });
    }

    private void PublishInteraction(DiscordTransportEvent transportEvent)
    {
        if (transportEvent.Interaction is null)
            return;

        Interaction? interaction;
        try
        {
            interaction = _mapper.MapInteraction(transportEvent.Interaction);
        }
        catch (NlmpValidationException ex)
        {
            SetError("mapping_failed", ex.Message, fatal: false);
            return;
        }

        if (interaction is null)
            return;

        _events.OnNext(new InteractionCreatedEvent
        {
            Id = $"discord:interaction:{interaction.Id}",
            Source = Source,
            Sequence = Interlocked.Increment(ref _sequence),
            OccurredAt = interaction.CreatedAt,
            ReceivedAt = _clock.UtcNow,
            Payload = new InteractionPayload(interaction),
        });
    }

    private void PublishStatus(UpstreamState state, string? detail = null)
    {
        _events.OnNext(new UpstreamStatusEvent
        {
            Id = $"discord:status:{Interlocked.Increment(ref _sequence)}",
            Source = Source,
            Sequence = _sequence,
            OccurredAt = _clock.UtcNow,
            ReceivedAt = _clock.UtcNow,
            Payload = new UpstreamStatusPayload(state, detail),
        });
    }

    private void SetError(string code, string message, bool fatal = true)
    {
        LastError = $"{code}: {message}";
        if (fatal)
        {
            lock (_gate)
                _connected = false;
        }
    }

    private string[] OutboundMentionIds(Content content)
        => content.Mentions?
            .Where(mention => mention.Kind == MentionKind.User
                              && mention.Id is not null
                              && !string.Equals(mention.Id, Source.SelfId, StringComparison.Ordinal))
            .Select(mention => mention.Id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray()
           ?? [];

    private static ChatError ToError(DiscordSendResult result)
    {
        var code = result.ErrorCode switch
        {
            "rate_limited" => ChatErrorCodes.RateLimited,
            "forbidden" => ChatErrorCodes.Forbidden,
            "not_found" => ChatErrorCodes.NotFound,
            "timeout" => ChatErrorCodes.Timeout,
            "unavailable" => ChatErrorCodes.Unavailable,
            "invalid_request" => ChatErrorCodes.InvalidRequest,
            _ => ChatErrorCodes.Internal,
        };

        return ChatError.Create(code, result.ErrorMessage ?? "Discord request failed.", result.RetryAfterMs);
    }

    private static ChatAck Unsupported(ChatCommand command, string type)
        => ChatAck.Failure(command, ChatError.Create(
            ChatErrorCodes.Unsupported,
            $"Discord does not support '{type}' in the current configuration."));

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TransportObserver(DiscordChatUpstream owner) : IObserver<DiscordTransportEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            owner.SetError("transport_stream", error.Message);
            owner.PublishStatus(UpstreamState.Error, owner.LastError);
        }

        public void OnNext(DiscordTransportEvent value) => owner.OnTransportEvent(value);
    }
}

/// <summary>Serializable status payload for <c>discord status</c>.</summary>
public sealed record DiscordStatusSnapshot(
    Source Source,
    bool Connected,
    string? LastError,
    int? GuildCount,
    int? ChannelCount,
    int? LatencyMs,
    Capabilities Capabilities,
    IReadOnlyList<string> Guilds,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Users,
    bool RequireMention,
    bool SlashCommands,
    int SendFailures,
    string? LastSendError,
    DateTimeOffset? LastEventAt = null,
    DateTimeOffset? LastConnectedAt = null,
    DateTimeOffset? LastDisconnectAt = null,
    int ReconnectCount = 0,
    string? LastDisconnectError = null,
    string? LastDisconnectCode = null,
    bool ReconnectWatchdogEnabled = false,
    int ReconnectAttempts = 0,
    int WatchdogTriggers = 0,
    DateTimeOffset? NextReconnectAt = null,
    string? LastReconnectError = null,
    bool RequiresManualIntervention = false);
