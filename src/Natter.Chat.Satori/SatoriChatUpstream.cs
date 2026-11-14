using System.Reactive.Subjects;
using Natter.Abstractions.Ports;
using Natter.Chat;

namespace Natter.Chat.Satori;

/// <summary>Result of one Satori API call, reduced to what the NLMP adapter needs.</summary>
public sealed record SatoriSendResult(
    bool Ok,
    IReadOnlyList<string>? MessageIds = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? RetryAfterMs = null);

/// <summary>
/// Transport boundary for the Satori adapter: WebSocket/HTTP, authentication and
/// reconnection live behind this interface. The transport owns credentials; the adapter
/// passes only the last processed <c>sn</c>.
/// </summary>
public interface ISatoriTransport
{
    IObservable<SatoriEvent> Events { get; }

    ValueTask ConnectAsync(long? lastSn, CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<SatoriSendResult> SendAsync(SatoriApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Satori adapter as an NLMP upstream: maps ingress events to NLMP, maps commands to Satori
/// API requests, tracks <c>sn</c> for session recovery and deduplicates replayed events.
/// </summary>
public sealed class SatoriChatUpstream : IChatUpstream, IDisposable
{
    private readonly ISatoriTransport _transport;
    private readonly IClock _clock;
    private readonly SatoriEventMapper _mapper;
    private readonly SatoriCommandMapper _commandMapper = new();
    private readonly SatoriSnTracker _snTracker = new();
    private readonly SatoriCapabilityOptions? _capabilityOptions;
    private readonly ChatIdempotencyCache _idempotency;
    private readonly Subject<ChatEvent> _subject = new();
    private readonly ISubject<ChatEvent> _events;
    private readonly Lock _gate = new();
    private IDisposable? _subscription;
    private Capabilities _capabilities;
    private bool _connected;
    private bool _disposed;

    public SatoriChatUpstream(
        Source source,
        ISatoriTransport transport,
        IClock clock,
        SatoriMappingOptions? mappingOptions = null,
        SatoriCapabilityOptions? capabilityOptions = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _capabilityOptions = capabilityOptions;
        _mapper = new SatoriEventMapper(clock, mappingOptions);
        _capabilities = SatoriCapabilityMapper.Map(null, capabilityOptions);
        _events = Subject.Synchronize(_subject);
        _idempotency = new ChatIdempotencyCache(clock: () => _clock.UtcNow);
    }

    public Source Source { get; }

    public bool IsConnected => _connected;

    public IObservable<ChatEvent> Events => _events;

    public SatoriSnTracker SnTracker => _snTracker;

    public SatoriMessageCache SnapshotCache => _mapper.SnapshotCache;

    /// <summary>Last transport/mapping failure, exposed for diagnostics (no message bodies).</summary>
    public string? LastError { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connected)
            return;

        // Subscribe before connecting so READY/login features emitted during the handshake
        // are not lost (capabilities are derived from login.features).
        lock (_gate)
        {
            _subscription ??= _transport.Events.Subscribe(new TransportObserver(this));
        }

        try
        {
            await _transport.ConnectAsync(_snTracker.LastSn, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        catch
        {
            lock (_gate)
            {
                _subscription?.Dispose();
                _subscription = null;
            }

            throw;
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            return;

        _connected = false;

        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_capabilities);
    }

    public async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Unavailable,
                $"Satori upstream '{Source.Key}' is not connected."));
        }

        if (command.Source != Source)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                $"Command source '{command.Source.Key}' does not match upstream '{Source.Key}'."));
        }

        var key = command.IdempotencyKey;
        if (!string.IsNullOrWhiteSpace(key) && _idempotency.TryGet(key, out var cached) && cached is not null)
            return cached;

        if (!_commandMapper.TryMap(command, out var request) || request is null)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Unsupported,
                $"The Satori adapter cannot map command type '{command.Type}'."));
        }

        SatoriSendResult result;
        try
        {
            result = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Internal,
                $"Satori transport failed: {exception.Message}"));
        }

        var ack = result.Ok
            ? ChatAck.Success(command, result.MessageIds, _clock.UtcNow)
            : ChatAck.Failure(command, ChatError.Create(
                result.ErrorCode ?? ChatErrorCodes.Internal,
                result.ErrorMessage ?? "Satori API call failed.",
                result.RetryAfterMs));

        if (ack.Ok && !string.IsNullOrWhiteSpace(key))
            _idempotency.Store(key, ack);

        return ack;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        _connected = false;
        _subject.Dispose();
    }

    private void OnTransportEvent(SatoriEvent satoriEvent)
    {
        try
        {
            if (satoriEvent.Login?.Features is { Count: > 0 } features)
                _capabilities = SatoriCapabilityMapper.Map(features, _capabilityOptions);

            // Advance sn for every frame, including ones NLMP ignores: a reconnect must not
            // process an ignored frame again.
            if (satoriEvent.Sn is { } sn && !_snTracker.TryAdvance(sn))
                return;

            var chatEvent = _mapper.Map(satoriEvent);
            if (chatEvent is not null)
                _events.OnNext(chatEvent);
        }
        catch (Exception exception)
        {
            // Mapping must never terminate the transport's event stream.
            LastError = exception.Message;
        }
    }

    private sealed class TransportObserver(SatoriChatUpstream owner) : IObserver<SatoriEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
            => owner.LastError = error.Message;

        public void OnNext(SatoriEvent value)
            => owner.OnTransportEvent(value);
    }
}
