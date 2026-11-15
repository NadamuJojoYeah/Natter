using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Natter.Abstractions.Ports;
using Natter.Chat;

namespace Natter.Chat.DingTalk;

/// <summary>
/// Minimal DingTalk HTTP-callback transport. It verifies timestamp/sign, acks callbacks and
/// sends replies through the sessionWebhook returned by DingTalk. The listener is opt-in via
/// config; tests use the transport abstraction directly.
/// </summary>
public sealed class DingTalkWebhookTransport : IDingTalkTransport, IDisposable
{
    private readonly DingTalkOptions _options;
    private readonly string _appSecret;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Subject<DingTalkTransportEvent> _events = new();
    private readonly IObservable<DingTalkTransportEvent> _synchronized;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessionExpiry = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private bool _disposed;

    public DingTalkWebhookTransport(DingTalkOptions options, string appSecret, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _appSecret = appSecret ?? throw new ArgumentNullException(nameof(appSecret));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
        _synchronized = Subject.Synchronize(_events);
    }

    public IObservable<DingTalkTransportEvent> Events => _synchronized;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
            return ValueTask.CompletedTask;

        if (string.IsNullOrWhiteSpace(_options.ListenPrefix))
            throw new InvalidOperationException("DingTalk ListenPrefix is required.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add(_options.ListenPrefix);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
            }
            _acceptLoop = null;
        }

        _cts?.Dispose();
        _cts = null;
        _events.OnNext(new DingTalkTransportEvent { Kind = DingTalkTransportEventKind.Disconnected });
    }

    public async ValueTask<DingTalkSendResult> SendAsync(
        DingTalkReply reply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (string.IsNullOrWhiteSpace(reply.SessionWebhook))
            return DingTalkSendResult.Failure("unauthorized", "DingTalk sessionWebhook is missing or expired.");

        if (_sessionExpiry.TryGetValue(reply.ConversationId, out var expiry) && expiry <= DateTimeOffset.UtcNow)
            return DingTalkSendResult.Failure("not_found", "DingTalk sessionWebhook has expired.");

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
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return DingTalkSendResult.Failure(
                    response.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" : "unavailable",
                    $"DingTalk webhook returned HTTP {(int)response.StatusCode}.");

            using var document = JsonDocument.Parse(body);
            var errcode = document.RootElement.TryGetProperty("errcode", out var code) && code.TryGetInt32(out var number)
                ? number
                : 0;
            if (errcode != 0)
                return DingTalkSendResult.Failure("internal", $"DingTalk webhook error {errcode}.");

            return DingTalkSendResult.Success();
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
        _listener?.Close();
        _events.OnCompleted();
        _events.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = HandleContextAsync(context, cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var timestamp = context.Request.Headers["timestamp"];
            var sign = context.Request.Headers["sign"];
            if (!DingTalkSignature.Verify(
                    timestamp,
                    sign,
                    _appSecret,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMinutes(Math.Max(1, _options.MaxClockSkewMinutes))))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await WriteJsonAsync(context.Response, new { errcode = 401, errmsg = "invalid signature" }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var parsed = DingTalkCallbackParser.Parse(body);
            if (!parsed.Ok || parsed.Message is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonAsync(context.Response, new { errcode = 400, errmsg = parsed.Error }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(parsed.Message.SessionWebhook)
                && parsed.Message.SessionWebhookExpiresAt is { } expiry)
            {
                _sessionExpiry[parsed.Message.ConversationId] = expiry;
            }

            _events.OnNext(new DingTalkTransportEvent
            {
                Kind = DingTalkTransportEventKind.Message,
                Message = parsed.Message,
            });

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            await WriteJsonAsync(context.Response, new { errcode = 0, errmsg = "ok" }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _events.OnNext(new DingTalkTransportEvent
            {
                Kind = DingTalkTransportEventKind.Error,
                ErrorCode = "callback_failed",
                ErrorMessage = ex.Message,
            });
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }
}
