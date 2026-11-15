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
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private readonly Lock _gate = new();
    private bool _connected;
    private bool _disposed;

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
        // Stream mode still returns a sessionWebhook for replies. Keep the same send path as
        // the HTTP callback transport; the bot can later add an active API sender.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                msgtype = "text",
                text = new { content = reply.Content },
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, reply.SessionWebhook)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return DingTalkSendResult.Failure("unavailable", $"DingTalk webhook returned HTTP {(int)response.StatusCode}.");

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

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
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
                _events.OnNext(new DingTalkTransportEvent
                {
                    Kind = DingTalkTransportEventKind.Message,
                    Message = parsed.Message,
                });
                await SendAckAsync(socket, frame.MessageId, 200, "OK", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                PublishError("stream_callback_parse_failed", parsed.Error ?? "invalid callback payload");
                await SendAckAsync(socket, frame.MessageId, 400, "bad request", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // EVENT/SYSTEM callbacks are acknowledged but not part of this transport.
        if (!string.IsNullOrWhiteSpace(frame.MessageId))
            await SendAckAsync(socket, frame.MessageId, 200, "OK", cancellationToken).ConfigureAwait(false);
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
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken)
                    .ConfigureAwait(false);
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
