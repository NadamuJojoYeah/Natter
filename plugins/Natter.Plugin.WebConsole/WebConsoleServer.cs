using System.Net;
using System.Text;
using System.Text.Json;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary>Logical HTTP request, decoupled from HttpListener so handlers stay unit-testable.</summary>
public sealed record WebConsoleHttpRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string RemoteAddress,
    bool IsHttps);

public sealed record WebConsoleHttpResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public static WebConsoleHttpResponse Json(int statusCode, object value, IReadOnlyDictionary<string, string>? headers = null)
        => new(statusCode, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, WebConsoleJson.Options), headers ?? new Dictionary<string, string>());

    public static WebConsoleHttpResponse Text(int statusCode, string contentType, string value, IReadOnlyDictionary<string, string>? headers = null)
        => new(statusCode, contentType, Encoding.UTF8.GetBytes(value), headers ?? new Dictionary<string, string>());
}

public static class WebConsoleJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

public sealed record WebConsoleServerStatus(string State, string? Url, string? ErrorCode = null, string? Error = null)
{
    public static WebConsoleServerStatus Stopped { get; } = new("stopped", null);

    public static WebConsoleServerStatus Listening(string url) => new("listening", url);

    public static WebConsoleServerStatus External(string url) => new("external", url);

    public static WebConsoleServerStatus Failed(string code, string error) => new("failed", null, code, error);
}

/// <summary>Shared mutable status so diagnostics can describe the server without a circular dependency.</summary>
public sealed class WebConsoleServerStatusHolder
{
    private WebConsoleServerStatus _current = WebConsoleServerStatus.Stopped;

    public WebConsoleServerStatus Current => Volatile.Read(ref _current);

    public void Set(WebConsoleServerStatus status) => Volatile.Write(ref _current, status);
}

/// <summary>Static asset whitelist; no directory listing and no arbitrary file access.</summary>
public sealed class WebConsoleStaticAssets
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>Long-lived cache for content-hashed build output; the hash changes when bytes change.</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary><c>index.html</c> must never be cached: it is what points at the current hashed assets.</summary>
    public const string NoStoreCacheControl = "no-store";

    private readonly Dictionary<string, WebConsoleHttpResponse> _assets = new(StringComparer.Ordinal);

    private WebConsoleStaticAssets(string root, WebConsoleHttpResponse index)
    {
        Root = root;
        Index = index;
    }

    public string Root { get; }

    /// <summary>The SPA document served for `/` and for every unknown client-side route.</summary>
    public WebConsoleHttpResponse Index { get; }

    public IReadOnlyList<string> Files => _assets.Keys.OrderBy(route => route, StringComparer.Ordinal).ToArray();

    public static WebConsoleStaticAssets Load(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new WebConsoleStartupException(
                "webconsole.static-assets-missing",
                $"WebConsole static assets directory was not found: '{root}'. Run the UI build "
                + "(npm run build in ui/) or stage a plugin package that includes wwwroot/.");
        }

        var indexPath = Path.Combine(root, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new WebConsoleStartupException(
                "webconsole.static-assets-missing",
                $"WebConsole build output is incomplete under '{root}': missing index.html. "
                + "Run the UI build (npm run build in ui/) before packaging the plugin.");
        }

        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = new WebConsoleStaticAssets(root, BuildResponse("/index.html", indexPath, index: true));

        // `/` and `/index.html` stay addressable as assets (existing callers and health checks use
        // them); every other client-side route goes through TryGetSpaDocument.
        result._assets["/"] = result.Index;
        result._assets["/index.html"] = result.Index;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);

            // Traversal is impossible by construction: only files physically under root are reachable.
            if (!full.StartsWith(rootFull, StringComparison.Ordinal))
                continue;

            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            var route = "/" + relative;
            if (string.Equals(relative, "index.html", StringComparison.Ordinal))
                continue;

            result._assets[route] = BuildResponse(route, full, index: false);
        }

        return result;
    }

    public bool TryGet(string path, out WebConsoleHttpResponse response)
        => _assets.TryGetValue(path, out response!);

    /// <summary>
    /// SPA routing: a non-API GET that does not match a build artifact falls back to index.html.
    /// Anything that looks like a file request (has an extension) still 404s, so a missing asset is
    /// visible instead of silently returning HTML with the wrong content type.
    /// </summary>
    public bool TryGetSpaDocument(string path, out WebConsoleHttpResponse response)
    {
        response = Index;

        if (path.Contains('\\', StringComparison.Ordinal))
            return false;

        var lastSegment = path.AsSpan(path.LastIndexOf('/') + 1);
        if (lastSegment.IsEmpty)
            return true;

        // A dot in the last segment means "asset", not "client route".
        return lastSegment.IndexOf('.') < 0;
    }

    private static WebConsoleHttpResponse BuildResponse(string route, string file, bool index)
    {
        var contentType = ContentTypeFor(file);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cache-Control"] = index ? NoStoreCacheControl : ImmutableCacheControl,
        };

        if (index)
            headers["X-Content-Type-Options"] = "nosniff";

        if (route.StartsWith("/assets/", StringComparison.Ordinal))
            headers["X-Content-Type-Options"] = "nosniff";

        return new WebConsoleHttpResponse(200, contentType, File.ReadAllBytes(file), headers);
    }

    /// <summary>
    /// MIME map for build output. A wrong type is fatal for ES modules (`text/javascript`) and for
    /// fonts (the browser silently drops a mismatched <c>font/woff2</c>).
    /// </summary>
    public static string ContentTypeFor(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        return extension switch
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
    }
}

/// <summary>Raised on startup failures that must mark the plugin Failed with a stable code.</summary>
public sealed class WebConsoleStartupException : Exception
{
    public WebConsoleStartupException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Loopback HTTP server using BCL HttpListener (gap G7: host ALC does not share ASP.NET Core).
/// Binds in <c>StartAsync</c> only; stops idempotently.
/// </summary>
public sealed class WebConsoleServer : IAsyncDisposable
{
    private readonly WebConsoleOptions _options;
    private readonly WebConsoleRequestHandler _handler;
    private readonly WebConsoleServerStatusHolder _status;
    private readonly IPluginLog _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _concurrency;
    private Task? _loop;
    private bool _started;

    public WebConsoleServer(
        WebConsoleOptions options,
        WebConsoleRequestHandler handler,
        WebConsoleServerStatusHolder status,
        IPluginLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _concurrency = new SemaphoreSlim(
            Math.Clamp(options.MaxConcurrentRequests, 1, WebConsoleDefaults.MaxConcurrentRequestsLimit));
    }

    public string Url => BuildUrl(_options);

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
            _status.Set(WebConsoleServerStatus.Failed("webconsole.port-in-use", exception.Message));
            throw new WebConsoleStartupException(
                "webconsole.port-in-use",
                $"Cannot bind '{Url}': {exception.Message}. Change 'port' or stop the process using it.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Security.SecurityException)
        {
            _status.Set(WebConsoleServerStatus.Failed("webconsole.bind-failed", exception.Message));
            throw new WebConsoleStartupException("webconsole.bind-failed", exception.Message);
        }

        _started = true;
        _status.Set(WebConsoleServerStatus.Listening(Url));
        _loop = Task.Run(() => AcceptLoopAsync(_stopping.Token), CancellationToken.None);
        _log.Information($"webconsole listening on {Url}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        _started = false;

        try
        {
            _stopping.Cancel();
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
                _log.Warning("webconsole accept loop did not stop within 2s");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        _status.Set(WebConsoleServerStatus.Stopped);
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

            // A slow or stuck client must not consume an unbounded number of worker slots (S2-D3);
            // when every slot is busy the listener answers 503 immediately instead of queueing.
            if (!_concurrency.Wait(0))
            {
                TryWriteError(context, 503, "server.busy", "The console is busy; retry shortly.", retryAfterSeconds: 1);
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
        // One budget for the whole exchange, body read included: a client that trickles the request
        // line/body can no longer hold a worker slot indefinitely (S2-D3).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(_options.RequestTimeoutSeconds, 1, WebConsoleDefaults.MaxRequestTimeoutSeconds)));

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
            // The request budget expired (typically a slow body); answer once and drop the connection
            // so the abandoned socket read cannot linger.
            TryWriteError(context, 408, "request.timeout", "The request exceeded the console time budget.");
            TryAbort(context);
        }
        catch (TimeoutException)
        {
            TryWriteError(context, 408, "request.timeout", "The request exceeded the console time budget.");
            TryAbort(context);
        }
        catch (WebConsoleStartupException exception) when (exception.Code == "webconsole.request-too-large")
        {
            TryWriteError(context, 413, exception.Code, "Request body exceeds 64 KiB.");
        }
        catch (Exception exception)
        {
            _log.Error(exception, "webconsole request failed");
            TryWriteError(context, 500, "internal.error", "Internal server error.");
        }
    }

    /// <summary>
    /// Route-aware request body budget. Config documents are validated against a 256 KiB contract,
    /// so the admin config routes need a larger transport cap than the rest of the API.
    /// </summary>
    public static int ResolveBodyLimit(string path)
        => path.StartsWith("/api/config/", StringComparison.Ordinal)
            ? WebConsoleDefaults.MaxAdminBodyBytes
            : WebConsoleDefaults.MaxRequestBodyBytes;

    private static async Task<WebConsoleHttpRequest> BuildRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var body = string.Empty;
        var limit = ResolveBodyLimit(request.Url?.AbsolutePath ?? "/");
        var reason = limit == WebConsoleDefaults.MaxAdminBodyBytes
            ? $"{WebConsoleDefaults.MaxAdminBodyBytes / 1024} KiB"
            : $"{limit / 1024} KiB";

        if (request.HasEntityBody)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;

            // WaitAsync races the socket read against the request budget: HttpListener's stream does
            // not abort a half-sent body on token cancellation alone, so the race is what guarantees
            // a slow client cannot hold the worker slot (S2-D3).
            while ((read = await request.InputStream.ReadAsync(chunk, cancellationToken)
                       .AsTask()
                       .WaitAsync(cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new WebConsoleStartupException("webconsole.request-too-large", $"Request body exceeds {reason}.");

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

        var path = request.Url?.AbsolutePath ?? "/";
        var query = request.Url?.Query.TrimStart('?') ?? string.Empty;

        return new WebConsoleHttpRequest(
            request.HttpMethod,
            path,
            query,
            headers,
            body,
            context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown",
            request.IsSecureConnection);
    }

    private static async Task WriteResponseAsync(
        HttpListenerContext context,
        WebConsoleHttpResponse response,
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

    private static void TryWriteError(
        HttpListenerContext context,
        int statusCode,
        string code,
        string message,
        int? retryAfterSeconds = null)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new { error = new { code, message } },
                WebConsoleJson.Options);

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

    public static string BuildUrl(WebConsoleOptions options)
    {
        return string.Equals(options.Bind, "localhost", StringComparison.OrdinalIgnoreCase)
            ? $"http://localhost:{options.Port}"
            : options.Bind.Contains(':')
                ? $"http://[{options.Bind}]:{options.Port}"
                : $"http://{options.Bind}:{options.Port}";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _listener.Close();
        _concurrency.Dispose();
    }
}
