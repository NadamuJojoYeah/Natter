using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;

namespace Natter.Chat.DingTalk;

/// <summary>
/// DingTalk Stream-mode transport: opens a gateway connection over HTTP, attaches a
/// ClientWebSocket using the returned endpoint+ticket, subscribes to the bot-message topic,
/// acks every callback and reconnects with bounded backoff. The callback payload is parsed by
/// the same <see cref="DingTalkCallbackParser"/> used by the legacy HTTP transport.
/// </summary>
public sealed class DingTalkStreamTransport : IDingTalkTransport, IDisposable
{
    private readonly DingTalkOptions _options;
    private readonly string _clientSecret;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Subject<DingTalkTransportEvent> _events = new();
    private readonly IObservable<DingTalkTransportEvent> _synchronized;
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly DingTalkStreamLiveness _liveness;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentCallbacks = new(StringComparer.Ordinal);
    private readonly int _dedupCapacity;
    private readonly TimeSpan _dedupTtl;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private readonly Lock _gate = new();
    private bool _connected;
    private bool _disposed;
    private int _halfOpen;

    public DingTalkStreamTransport(DingTalkOptions options, string clientSecret, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new ArgumentException("DingTalk Stream requires ClientId.", nameof(options));
        _clientSecret = string.IsNullOrWhiteSpace(clientSecret)
            ? throw new ArgumentException("DingTalk Stream requires ClientSecret.", nameof(clientSecret))
            : clientSecret;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsHttp = httpClient is null;
        _liveness = new DingTalkStreamLiveness(options.StreamIdleTimeoutMs, options.StreamPongTimeoutMs);
        _dedupCapacity = Math.Max(16, options.StreamCallbackDedupCapacity);
        _dedupTtl = TimeSpan.FromMilliseconds(Math.Max(1_000, options.StreamCallbackDedupTtlMs));
        _synchronized = Subject.Synchronize(_events);
    }

    public IObservable<DingTalkTransportEvent> Events => _synchronized;

    public bool IsConnected
    {
        get { lock (_gate) return _connected; }
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ConnectOnceAsync(_cts.Token).ConfigureAwait(false);
        _runTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        _cts?.Cancel();
        await CloseSocketAsync(CancellationToken.None).ConfigureAwait(false);
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
            _runTask = null;
        }

        lock (_gate)
            _connected = false;
        _cts?.Dispose();
        _cts = null;
        _events.OnNext(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Disconnected });
    }

    public async ValueTask<DingTalkSendResult> SendAsync(
        DingTalkReply reply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);

        // S2-A5: the sessionWebhook comes from the callback payload, so it is untrusted input.
        // Only https on DingTalk hosts may be posted to; anything else must not produce a request.
        if (!DingTalkSessionWebhookPolicy.TryValidate(reply.SessionWebhook, out var webhook, out var rejection))
        {
            return DingTalkSendResult.Failure(
                DingTalkSessionWebhookPolicy.RejectedCode,
                $"DingTalk sessionWebhook rejected ({rejection}).");
        }

        // S2-A2: reuse the injected/shared HttpClient (connection pool, TLS session cache, proxy)
        // instead of constructing one per reply.
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                msgtype = "text",
                text = new { content = reply.Content },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failureCode = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "rate_limited"
                    : "unavailable";
                return DingTalkSendResult.Failure(failureCode, $"DingTalk webhook returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(body);
            var code = document.RootElement.TryGetProperty("errcode", out var errcode) && errcode.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            return code == 0
                ? DingTalkSendResult.Success()
                : DingTalkSendResult.Failure("internal", $"DingTalk webhook error {code}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DingTalkSendResult.Failure("unavailable", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _socket?.Dispose();
        _sendGate.Dispose();
        _events.OnCompleted();
        _events.Dispose();
        if (_ownsHttp)
            _http.Dispose();
        _cts?.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishError("stream_receive_failed", ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            attempt++;
            var delay = Math.Min(
                Math.Max(0, _options.ReconnectBaseDelayMs) * Math.Pow(2, Math.Min(attempt, 8)),
                Math.Max(0, _options.ReconnectMaxDelayMs)) + Random.Shared.Next(0, 251);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken).ConfigureAwait(false);
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                PublishError("stream_reconnect_failed", ex.Message);
            }
        }

        lock (_gate)
            _connected = false;
        _events.OnNext(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Disconnected });
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var requestBody = DingTalkStreamProtocol.BuildOpenConnectionRequest(
            _options.ClientId!,
            _clientSecret,
            string.IsNullOrWhiteSpace(_options.StreamTopic)
                ? DingTalkStreamProtocol.BotTopic
                : _options.StreamTopic);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.StreamGatewayUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DingTalk Stream gateway returned HTTP {(int)response.StatusCode}.");

        var connection = DingTalkStreamProtocol.ParseOpenConnection(body);
        await CloseSocketAsync(cancellationToken).ConfigureAwait(false);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(
            new Uri(DingTalkStreamProtocol.BuildWebSocketUri(connection.Endpoint, connection.Ticket)),
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _socket = socket;
            _connected = true;
        }

        Volatile.Write(ref _halfOpen, 0);
        _liveness.Reset(DateTimeOffset.UtcNow);

        // S2-A6: an internal reconnect must tell the upstream, otherwise /health keeps showing
        // connected=false after the transport already recovered.
        _events.OnNext(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Connected });
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        lock (_gate)
            socket = _socket;
        if (socket is null)
            throw new InvalidOperationException("DingTalk Stream socket is not connected.");

        var buffer = new byte[8192];
        var message = new MemoryStream();
        var window = _liveness.ReceiveWindow;

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _halfOpen) != 0)
                return;

            WebSocketReceiveResult result;
            // S2-A4: bound every receive so an idle/half-open socket is judged instead of
            // blocking forever behind IsConnected=true.
            using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveCts.CancelAfter(window);
            try
            {
                result = await socket.ReceiveAsync(buffer, receiveCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var verdict = _liveness.Evaluate(now);
                if (verdict == DingTalkStreamLivenessState.Unknown)
                {
                    window = _liveness.ReceiveWindow;
                    continue;
                }

                var code = verdict == DingTalkStreamLivenessState.PongMissing
                    ? "stream_pong_timeout"
                    : "stream_idle_timeout";
                PublishError(
                    code,
                    $"No inbound Stream frame within {window.TotalMilliseconds:F0}ms (verdict={verdict}); "
                    + "the websocket is treated as half-open and will be reconnected.");
                return;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            _liveness.RecordInbound(DateTimeOffset.UtcNow);
            window = _liveness.ReceiveWindow;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            if (!result.EndOfMessage)
                continue;

            var json = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            await HandleFrameAsync(json, socket, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(string json, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        DingTalkStreamFrame frame;
        try
        {
            frame = DingTalkStreamProtocol.ParseFrame(json);
        }
        catch (JsonException ex)
        {
            PublishError("stream_invalid_frame", ex.Message);
            return;
        }

        if (frame.IsDisconnect)
        {
            await CloseSocketAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(frame.Type, "CALLBACK", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = DingTalkCallbackParser.Parse(frame.Data ?? string.Empty);
            if (parsed.Ok && parsed.Message is not null)
            {
                var dedupKey = string.IsNullOrWhiteSpace(frame.MessageId)
                    ? parsed.Message.MsgId
                    : frame.MessageId;
                var duplicate = IsRecentCallback(dedupKey);

                // S2-A3: acknowledge *before* dispatching. The platform retries when the ack is
                // late, and the downstream chain (orchestrator → AI → tool loop) routinely
                // outlives the ack deadline. An ack failure still dispatches the message so a
                // dead socket cannot swallow it; the socket is marked half-open for reconnection.
                await TrySendAckAsync(socket, frame.MessageId, 200, "OK", cancellationToken).ConfigureAwait(false);

                if (duplicate)
                {
                    PublishError(
                        "stream_callback_duplicate",
                        $"Callback '{dedupKey}' was re-delivered by the platform; acknowledged without dispatching.");
                    return;
                }

                _events.OnNext(new DingTalkTransportEvent
                {
                    Kind = DingTalkTransportEventKind.Message,
                    Message = parsed.Message,
                });
                RememberCallback(dedupKey);
            }
            else
            {
                PublishError("stream_callback_parse_failed", parsed.Error ?? "invalid callback payload");
                await TrySendAckAsync(socket, frame.MessageId, 400, "bad request", cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        // EVENT/SYSTEM callbacks are acknowledged but not part of this transport.
        if (!string.IsNullOrWhiteSpace(frame.MessageId))
            await TrySendAckAsync(socket, frame.MessageId, 200, "OK", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an ack but never lets a broken socket escape as an exception: a failed write means
    /// the peer stopped reading us (half-open), so the receive loop is asked to exit and the run
    /// loop reconnects.
    /// </summary>
    private async ValueTask<bool> TrySendAckAsync(
        ClientWebSocket socket,
        string? messageId,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendAckAsync(socket, messageId, code, message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _halfOpen, 1);
            PublishError("stream_ack_failed", ex.Message);
            return false;
        }
    }

    private bool IsRecentCallback(string? key)
        => !string.IsNullOrWhiteSpace(key)
           && _recentCallbacks.TryGetValue(key, out var seenAt)
           && DateTimeOffset.UtcNow - seenAt < _dedupTtl;

    private void RememberCallback(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var now = DateTimeOffset.UtcNow;
        if (_recentCallbacks.Count >= _dedupCapacity)
        {
            foreach (var (existing, seenAt) in _recentCallbacks)
            {
                if (now - seenAt >= _dedupTtl)
                    _recentCallbacks.TryRemove(existing, out _);
            }

            while (_recentCallbacks.Count >= _dedupCapacity)
            {
                var oldest = _recentCallbacks.OrderBy(pair => pair.Value).FirstOrDefault();
                if (oldest.Key is null)
                    break;
                _recentCallbacks.TryRemove(oldest.Key, out _);
            }
        }

        _recentCallbacks[key] = now;
    }

    private async Task SendAckAsync(
        ClientWebSocket socket,
        string? messageId,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId) || socket.State != WebSocketState.Open)
            return;

        var payload = Encoding.UTF8.GetBytes(DingTalkStreamProtocol.BuildAck(messageId, code, message));
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
            _liveness.RecordSend(DateTimeOffset.UtcNow);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task CloseSocketAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = _socket;
            _socket = null;
            _connected = false;
        }

        if (socket is null)
            return;

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // Bounded close handshake: a half-open peer never answers the close frame, and an
                // unbounded CloseAsync would stall DisconnectAsync/reconnect forever.
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken)
                        .WaitAsync(CloseHandshakeTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    socket.Abort();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    socket.Abort();
                }
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private void PublishError(string code, string message)
        => _events.OnNext(new DingTalkTransportEvent
        {
            Kind = DingTalkTransportEventKind.Error,
            ErrorCode = code,
            ErrorMessage = message,
        });
}
