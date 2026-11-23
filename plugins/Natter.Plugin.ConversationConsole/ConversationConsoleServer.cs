using System.Net;
using System.Text;
using System.Text.Json;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// Serves the Vite build output under the plugin's <c>wwwroot</c>. Only files that physically exist
/// under that directory are reachable (the URL never maps to an arbitrary filesystem path, so
/// traversal is impossible by construction), and unknown client-side routes fall back to
/// <c>index.html</c>.
/// </summary>
public sealed class ConversationConsoleStaticAssets
{
    /// <summary>Content-hashed build output can be cached forever; the hash changes with the bytes.</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary><c>index.html</c> points at the current hashes and must never be cached.</summary>
    public const string NoStoreCacheControl = "no-store";

    private readonly Dictionary<string, ConversationConsoleHttpResponse> _assets = new(StringComparer.Ordinal);

    private ConversationConsoleStaticAssets(string root, ConversationConsoleHttpResponse index)
    {
        Root = root;
        Index = index;
    }

    public string Root { get; }

    public ConversationConsoleHttpResponse Index { get; }

    public IReadOnlyList<string> Files => _assets.Keys.OrderBy(route => route, StringComparer.Ordinal).ToArray();

    public static ConversationConsoleStaticAssets Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.static-assets-missing",
                $"Console assets directory was not found: '{root}'. Run the UI build (npm run build in ui/) "
                + "or stage a plugin package that includes wwwroot/.");
        }

        var indexPath = Path.Combine(root, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.static-assets-missing",
                $"Console build output is incomplete under '{root}': missing index.html. "
                + "Run the UI build (npm run build in ui/) before packaging the plugin.");
        }

        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = new ConversationConsoleStaticAssets(root, BuildResponse(indexPath, index: true));
        result._assets["/"] = result.Index;
        result._assets["/index.html"] = result.Index;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(rootFull, StringComparison.Ordinal))
                continue;

            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "index.html", StringComparison.Ordinal))
                continue;

            result._assets["/" + relative] = BuildResponse(full, index: false);
        }

        return result;
    }

    public bool TryGet(string path, out ConversationConsoleHttpResponse response)
        => _assets.TryGetValue(path, out response!);

    /// <summary>
    /// SPA routing: unknown non-API GETs render the document, but a request whose last segment looks
    /// like a file (contains a dot) still 404s so missing assets stay visible.
    /// </summary>
    public bool TryGetSpaDocument(string path, out ConversationConsoleHttpResponse response)
    {
        response = Index;

        if (path.Contains('\\', StringComparison.Ordinal))
            return false;

        var lastSegment = path.AsSpan(path.LastIndexOf('/') + 1);
        if (lastSegment.IsEmpty)
            return true;

        return lastSegment.IndexOf('.') < 0;
    }

    public static string ContentTypeFor(string file)
        => Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".map" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf" => "font/ttf",
            ".txt" => "text/plain; charset=utf-8",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };

    private static ConversationConsoleHttpResponse BuildResponse(string file, bool index)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cache-Control"] = index ? NoStoreCacheControl : ImmutableCacheControl,
            ["X-Content-Type-Options"] = "nosniff",
        };

        return new ConversationConsoleHttpResponse(
            200,
            ContentTypeFor(file),
            File.ReadAllBytes(file),
            headers);
    }
}

/// <summary>Shared mutable status so diagnostics can describe the listener without a circular dependency.</summary>
public sealed record ConversationConsoleServerStatus(string State, string? Url, string? ErrorCode = null, string? Error = null)
{
    public static ConversationConsoleServerStatus Stopped { get; } = new("stopped", null);

    public static ConversationConsoleServerStatus Listening(string url) => new("listening", url);

    public static ConversationConsoleServerStatus Failed(string code, string error) => new("failed", null, code, error);
}

/// <summary>
/// Loopback HTTP listener (BCL <c>HttpListener</c>, same hosting choice as WebConsole: the host ALC
/// does not share ASP.NET Core). Binds in <see cref="StartAsync"/> only and stops idempotently.
/// </summary>
public sealed class ConversationConsoleServer : IAsyncDisposable
{
    private readonly ConversationConsoleOptions _options;
    private readonly ConversationConsoleRequestHandler _handler;
    private readonly ConversationConsoleServerStatusHolder _status;
    private readonly IPluginLog _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _concurrency;
    private Task? _loop;
    private bool _started;

    public ConversationConsoleServer(
        ConversationConsoleOptions options,
        ConversationConsoleRequestHandler handler,
        ConversationConsoleServerStatusHolder status,
        IPluginLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _concurrency = new SemaphoreSlim(
            Math.Clamp(options.MaxConcurrentRequests, 1, ConversationConsoleDefaults.MaxConcurrentRequestsLimit));
    }

    public string Url => ConversationConsoleNetwork.BuildUrl(_options.Bind, _options.Port);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return Task.CompletedTask;

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(Url + "/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException exception)
        {
            _status.Set(ConversationConsoleServerStatus.Failed("conversation-console.port-in-use", exception.Message));
            throw new ConversationConsoleStartupException(
                "conversation-console.port-in-use",
                $"Cannot bind '{Url}': {exception.Message}. Change 'port' or stop the process using it.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException)
        {
            _status.Set(ConversationConsoleServerStatus.Failed("conversation-console.bind-failed", exception.Message));
            throw new ConversationConsoleStartupException("conversation-console.bind-failed", exception.Message);
        }

        _started = true;
        _status.Set(ConversationConsoleServerStatus.Listening(Url));
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);
        _log.Information($"conversation console listening on {Url}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        _started = false;

        try
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Warning("conversation console accept loop did not stop within 2s");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        _status.Set(ConversationConsoleServerStatus.Stopped);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                                  or HttpListenerException
                                                  or ObjectDisposedException
                                                  or InvalidOperationException)
            {
                return;
            }

            // A saturated console answers 503 immediately instead of queueing unbounded work.
            if (!_concurrency.Wait(0))
            {
                TryWriteError(context, 503, ConversationConsoleErrorCodes.ServerBusy, "The console is busy; retry shortly.");
                continue;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await HandleAsync(context, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _concurrency.Release();
                    }
                },
                CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(_options.RequestTimeoutSeconds, 1, ConversationConsoleDefaults.MaxRequestTimeoutSeconds)));

        try
        {
            var request = await BuildRequestAsync(context, timeout.Token).ConfigureAwait(false);
            var response = await _handler.HandleAsync(request, timeout.Token).ConfigureAwait(false);
            var isHead = string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.Ordinal);
            await WriteResponseAsync(context, response, isHead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryAbort(context);
        }
        catch (OperationCanceledException)
        {
            TryWriteError(context, 408, ConversationConsoleErrorCodes.RequestTimeout, "The request exceeded the console time budget.");
            TryAbort(context);
        }
        catch (ConversationConsoleStartupException exception) when (exception.Code == "conversation-console.request-too-large")
        {
            TryWriteError(context, 413, "request.too_large", "Request body exceeds 4 KiB.");
        }
        catch (Exception exception)
        {
            _log.Error(exception, "conversation console request failed");
            TryWriteError(context, 500, ConversationConsoleErrorCodes.Internal, "Internal server error.");
        }
    }

    private static async Task<ConversationConsoleHttpRequest> BuildRequestAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var body = string.Empty;

        if (request.HasEntityBody)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;

            while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken)
                       .AsTask()
                       .WaitAsync(cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > ConversationConsoleDefaults.MaxRequestBodyBytes)
                {
                    throw new ConversationConsoleStartupException(
                        "conversation-console.request-too-large",
                        "Request body exceeds the console limit.");
                }

                buffer.Write(chunk, 0, read);
            }

            body = Encoding.UTF8.GetString(buffer.ToArray());
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.Headers.AllKeys)
        {
            if (key is null)
                continue;

            headers[key] = request.Headers[key] ?? string.Empty;
        }

        return new ConversationConsoleHttpRequest(
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "/",
            request.Url?.Query.TrimStart('?') ?? string.Empty,
            headers,
            body,
            context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown");
    }

    private static async Task WriteResponseAsync(
        HttpListenerContext context,
        ConversationConsoleHttpResponse response,
        bool isHead,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.ContentLength64 = response.Body.Length;
        context.Response.KeepAlive = false;

        foreach (var header in response.Headers)
        {
            if (header.Key is "Content-Length" or "Content-Type" or "Transfer-Encoding" or "Connection")
                continue;

            context.Response.Headers[header.Key] = header.Value;
        }

        if (!isHead)
            await context.Response.OutputStream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);

        context.Response.Close();
    }

    private static void TryWriteError(HttpListenerContext context, int statusCode, string code, string message)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { error = new { code, message } },
                ConversationConsoleJson.Options);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
        catch
        {
            TryAbort(context);
        }
    }

    private static void TryAbort(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _listener.Close();
        _concurrency.Dispose();
    }
}

/// <summary>Shared mutable status holder (kept separate from the server so diagnostics can read it).</summary>
public sealed class ConversationConsoleServerStatusHolder
{
    private ConversationConsoleServerStatus _current = ConversationConsoleServerStatus.Stopped;

    public ConversationConsoleServerStatus Current => Volatile.Read(ref _current);

    public void Set(ConversationConsoleServerStatus status) => Volatile.Write(ref _current, status);
}
