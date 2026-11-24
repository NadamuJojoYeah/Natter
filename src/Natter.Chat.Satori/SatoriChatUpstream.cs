using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Safety;

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
    private readonly ILogger<SatoriChatUpstream>? _logger;
    private readonly SatoriEventMapper _mapper;
    private readonly SatoriCommandMapper _commandMapper = new();
    private readonly SatoriSnTracker _snTracker = new();
    private readonly SatoriCapabilityOptions? _capabilityOptions;
    private readonly ChatOutboundPolicy _outboundPolicy;
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
        SatoriCapabilityOptions? capabilityOptions = null,
        ChatOutboundPolicy? outboundPolicy = null,
        ILogger<SatoriChatUpstream>? logger = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _capabilityOptions = capabilityOptions;
        _outboundPolicy = outboundPolicy ?? ChatOutboundPolicy.Default;
        _logger = logger;
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

        if (command.Source is null)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                "Command source must not be null."));
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

        // Sends go through the same chunking contract as Discord and DingTalk: a reply longer than
        // the declared MaxMessageLength is split instead of being rejected as a whole. Everything
        // else (update/delete) replaces or removes exactly one message and is not split, matching
        // the Discord adapter's convention.
        var ack = command is MessageSendCommand send
            ? await SendChunkedAsync(send, cancellationToken).ConfigureAwait(false)
            : await SendMappedAsync(command, cancellationToken).ConfigureAwait(false);

        if (ack.Ok && !string.IsNullOrWhiteSpace(key))
            _idempotency.Store(key, ack);

        return ack;
    }

    /// <summary>
    /// Maps and sends a command in one request. Adapter-side defence in depth: the mapper validates
    /// the mention policy against the mapped elements (not only <c>Content.Mentions</c>), so a caller
    /// that bypasses the dispatcher cannot emit a high-risk mention. Text that merely looks like an
    /// element is escaped into a text node and does not become a mention.
    /// </summary>
    private async ValueTask<ChatAck> SendMappedAsync(ChatCommand command, CancellationToken cancellationToken)
    {
        if (!_commandMapper.TryMap(command, _outboundPolicy, out var request, out var mapError)
            || request is null)
        {
            if (mapError is not null)
                return ChatAck.Failure(command, mapError);

            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Unsupported,
                $"The Satori adapter cannot map command type '{command.Type}'."));
        }

        return await SendRequestAsync(command, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a send into platform-sized chunks (task-74). Satori advertised
    /// <see cref="Capabilities.MaxMessageLength"/> but used to post the mapped request un-chunked, so
    /// an over-long reply was rejected as a whole. The split is the shared
    /// <see cref="ChatTextChunker"/> used by Discord and DingTalk: rune-safe, and a fenced code block
    /// that spans a boundary is closed and reopened on the next chunk when the content is markdown.
    /// Each chunk is mapped on its own, so the mention policy is validated for every piece of the
    /// reply; only the first chunk keeps the reply reference, and attachments/mentions stay on it.
    /// Failure semantics match the other adapters: the first failing chunk ends the send with that
    /// chunk's error, already-delivered chunks are not rolled back, and the ack is not cached.
    /// </summary>
    private async ValueTask<ChatAck> SendChunkedAsync(
        MessageSendCommand send,
        CancellationToken cancellationToken)
    {
        var content = send.Payload.Content;
        try
        {
            content.EnsureValid();
        }
        catch (NlmpValidationException exception)
        {
            return ChatAck.Failure(send, ChatError.Create(ChatErrorCodes.InvalidRequest, exception.Message));
        }

        var chunks = ChatTextChunker.Chunk(content, _capabilities.MaxMessageLength);
        var messageIds = new List<string>();

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkCommand = send with
            {
                Payload = send.Payload with
                {
                    Content = chunks[index],
                    ReplyTo = index == 0 ? send.Payload.ReplyTo : null,
                },
            };

            if (!_commandMapper.TryMap(chunkCommand, _outboundPolicy, out var request, out var mapError)
                || request is null)
            {
                return ChatAck.Failure(send, mapError ?? ChatError.Create(
                    ChatErrorCodes.Unsupported,
                    $"The Satori adapter cannot map command type '{chunkCommand.Type}'."));
            }

            var chunkAck = await SendRequestAsync(chunkCommand, request, cancellationToken).ConfigureAwait(false);
            if (!chunkAck.Ok)
                return ChatAck.Failure(send, chunkAck.Error!);

            if (chunkAck.Result?.MessageIds is { Count: > 0 } ids)
                messageIds.AddRange(ids);
        }

        return ChatAck.Success(send, messageIds.Count == 0 ? null : messageIds, _clock.UtcNow);
    }

    /// <summary>One transport call with the adapter's exception → NLMP error mapping.</summary>
    private async ValueTask<ChatAck> SendRequestAsync(
        ChatCommand command,
        SatoriApiRequest request,
        CancellationToken cancellationToken)
    {
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

        return result.Ok
            ? ChatAck.Success(command, result.MessageIds, _clock.UtcNow)
            : ChatAck.Failure(command, ToError(result));
    }

    private static ChatError ToError(SatoriSendResult result)
        => ChatError.Create(
            result.ErrorCode ?? ChatErrorCodes.Internal,
            result.ErrorMessage ?? "Satori API call failed.",
            result.RetryAfterMs);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connected = false;

        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        // Dispose owns the transport lifecycle: disconnect first (best effort) and release it.
        try
        {
            _transport.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown is best effort; a failed disconnect must not block disposal.
        }

        try
        {
            switch (_transport)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
            }
        }
        catch
        {
            // Shutdown is best effort.
        }

        _subject.Dispose();
    }

    private void OnTransportEvent(SatoriEvent satoriEvent)
    {
        try
        {
            if (satoriEvent.Login?.Features is { Count: > 0 } features)
                _capabilities = SatoriCapabilityMapper.Map(features, _capabilityOptions);

            // Replayed/older frames are skipped before mapping. The sequence is committed only
            // after a successful mapping: a mapping failure must not silently advance past the
            // event (otherwise a reconnect can never replay it).
            if (satoriEvent.Sn is { } incomingSn && !_snTracker.ShouldProcess(incomingSn))
                return;

            var chatEvent = _mapper.Map(satoriEvent);
            if (chatEvent is null)
            {
                LastError = $"Satori event mapping failed (type={satoriEvent.Type}, sn={satoriEvent.Sn}); sn not advanced.";
                _logger?.LogWarning(
                    "Satori event mapping failed type={Type} sn={Sn}; sn not advanced",
                    satoriEvent.Type,
                    satoriEvent.Sn);
                return;
            }

            if (satoriEvent.Sn is { } committedSn)
                _snTracker.Commit(committedSn);

            _events.OnNext(chatEvent);
        }
        catch (Exception exception)
        {
            // Mapping must never terminate the transport's event stream. The sequence is not
            // committed when processing throws, so a reconnect can replay the frame.
            LastError = exception.Message;
            _logger?.LogWarning(
                "Satori event processing failed type={Type} sn={Sn}; sn not advanced",
                satoriEvent.Type,
                satoriEvent.Sn);
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
