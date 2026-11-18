using System.Globalization;
using System.Text.Json;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary>
/// Protocol-level request handler: auth, CSRF, origin, routing, redaction and audit live here.
/// The transport (HttpListener today, Kestrel later) only moves bytes.
/// </summary>
public sealed class WebConsoleRequestHandler
{
    private static readonly Dictionary<string, string> SecurityHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache-Control"] = "no-store",
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Referrer-Policy"] = "no-referrer",
        ["Cross-Origin-Opener-Policy"] = "same-origin",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()",
        ["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
            + "connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
    };

    private readonly WebConsoleOptions _options;
    private readonly WebConsoleTokenAuthenticator _tokens;
    private readonly WebConsoleSessionStore _sessions;
    private readonly WebConsoleLoginThrottle _throttle;
    private readonly WebConsoleOriginPolicy _origins;
    private readonly WebConsoleDiagnostics _diagnostics;
    private readonly WebConsoleAuditStore _audit;
    private readonly WebConsoleStaticAssets _assets;
    private readonly TimeProvider _clock;
    private readonly IPluginLog _log;

    public WebConsoleRequestHandler(
        WebConsoleOptions options,
        WebConsoleTokenAuthenticator tokens,
        WebConsoleSessionStore sessions,
        WebConsoleLoginThrottle throttle,
        WebConsoleOriginPolicy origins,
        WebConsoleDiagnostics diagnostics,
        WebConsoleAuditStore audit,
        WebConsoleStaticAssets assets,
        TimeProvider clock,
        IPluginLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _origins = origins ?? throw new ArgumentNullException(nameof(origins));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async ValueTask<WebConsoleHttpResponse> HandleAsync(
        WebConsoleHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _clock.GetUtcNow();

        try
        {
            return Secure(await HandleCoreAsync(request, startedAt, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WebConsoleMethodNotAllowedException exception)
        {
            return Secure(WebConsoleHttpResponse.Json(
                405,
                new { error = new { code = "method.not_allowed", message = exception.Message } },
                new Dictionary<string, string> { ["Allow"] = exception.AllowedMethod }));
        }
        catch (Exception exception)
        {
            _log.Error(exception, "webconsole handler failed");
            return Secure(Error(500, "internal.error", "Internal server error."));
        }
    }

    private async ValueTask<WebConsoleHttpResponse> HandleCoreAsync(
        WebConsoleHttpRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var origin = GetHeader(request, "Origin");
        var fetchSite = GetHeader(request, "Sec-Fetch-Site");

        if (!_origins.IsAllowed(origin, fetchSite))
        {
            Audit(request, "origin.rejected", "denied", startedAt);
            return Error(403, "request.origin_denied", "Origin is not allowed.");
        }

        if (string.Equals(request.Method, "GET", StringComparison.Ordinal)
            && _assets.TryGet(request.Path, out var asset))
        {
            return asset;
        }

        if (string.Equals(request.Path, "/favicon.ico", StringComparison.Ordinal)
            && string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            return new WebConsoleHttpResponse(204, "text/plain", [], new Dictionary<string, string>());
        }

        if (string.Equals(request.Path, "/api/session", StringComparison.Ordinal))
            return HandleSession(request, startedAt);

        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
            return Error(404, "route.not_found", "Route not found.");

        var session = ResolveSession(request);
        if (session is null)
            return Error(401, "auth.required", "Login required.");

        if (!IsSafeMethod(request.Method) && !ValidateCsrf(request, session))
        {
            Audit(request, "csrf.rejected", "denied", startedAt);
            return Error(403, "csrf.invalid", $"Missing or invalid {WebConsoleDefaults.CsrfHeaderName} header.");
        }

        var route = request.Path["/api/".Length..];

        switch (route)
        {
            case "snapshot":
                RequireMethod(request, "GET");
                return await SnapshotAsync(request, startedAt, cancellationToken).ConfigureAwait(false);

            case "health":
                RequireMethod(request, "GET");
                return await SnapshotSectionAsync(
                    request, startedAt, snapshot => snapshot.Health, cancellationToken).ConfigureAwait(false);

            case "plugins":
                RequireMethod(request, "GET");
                return await SnapshotSectionAsync(
                    request, startedAt, snapshot => snapshot.Plugins, cancellationToken).ConfigureAwait(false);

            case "upstreams":
                RequireMethod(request, "GET");
                return await SnapshotSectionAsync(
                    request, startedAt, snapshot => snapshot.Upstreams, cancellationToken).ConfigureAwait(false);

            case "profiles":
                RequireMethod(request, "GET");
                return await SnapshotSectionAsync(
                    request, startedAt, snapshot => snapshot.Profiles, cancellationToken).ConfigureAwait(false);

            case "logs":
            {
                RequireMethod(request, "GET");
                var limit = ReadLimit(request, Math.Min(_options.MaxLogLines, 200));
                var snapshot = await _diagnostics
                    .CreateSnapshotAsync(Math.Min(_options.MaxLogLines, 200), cancellationToken)
                    .ConfigureAwait(false);

                Audit(request, "logs.view", "ok", startedAt, $"limit={limit}");
                return WebConsoleHttpResponse.Json(200, new
                {
                    generatedAt = snapshot.GeneratedAt,
                    limit,
                    hostLogs = snapshot.Logs,
                    audit = _audit.Read(limit),
                });
            }

            case "audit":
            {
                RequireMethod(request, "GET");
                var limit = ReadLimit(request, 50);
                Audit(request, "audit.view", "ok", startedAt, $"limit={limit}");
                return WebConsoleHttpResponse.Json(200, new { entries = _audit.Read(limit) });
            }

            case "config":
                RequireMethod(request, "GET");
                Audit(request, "config.view", "ok", startedAt);
                return WebConsoleHttpResponse.Json(200, _diagnostics.ConfigPreview);

            case "commands":
                return HandleCommands(request, startedAt);
        }

        if (route.StartsWith("config/", StringComparison.Ordinal))
            return HandleConfigWrite(request, route["config/".Length..], startedAt);

        return Error(404, "route.not_found", "Route not found.");
    }

    private WebConsoleHttpResponse HandleSession(WebConsoleHttpRequest request, DateTimeOffset startedAt)
    {
        switch (request.Method)
        {
            case "GET":
            {
                var session = ResolveSession(request);
                if (session is null)
                    return WebConsoleHttpResponse.Json(401, new { authenticated = false });

                return WebConsoleHttpResponse.Json(200, new
                {
                    authenticated = true,
                    role = RoleName(session.Role),
                    csrf = session.Csrf,
                    expiresAt = session.ExpiresAt,
                });
            }

            case "POST":
                return Login(request, startedAt);

            case "DELETE":
            {
                var session = ResolveSession(request);
                if (session is null)
                    return Error(401, "auth.required", "Login required.");

                if (!ValidateCsrf(request, session))
                {
                    Audit(request, "csrf.rejected", "denied", startedAt);
                    return Error(403, "csrf.invalid", $"Missing or invalid {WebConsoleDefaults.CsrfHeaderName} header.");
                }

                _sessions.Revoke(session.Id);
                Audit(request, "auth.logout", "ok", startedAt, user: RoleName(session.Role));

                return WebConsoleHttpResponse.Json(
                    200,
                    new { authenticated = false },
                    new Dictionary<string, string>
                    {
                        ["Set-Cookie"] = WebConsoleCookies.BuildExpiredCookie(_origins.RequiresSecureCookie || request.IsHttps),
                    });
            }

            default:
                return Error(405, "method.not_allowed", "Method not allowed.");
        }
    }

    private WebConsoleHttpResponse Login(WebConsoleHttpRequest request, DateTimeOffset startedAt)
    {
        if (request.Body.Length > WebConsoleDefaults.MaxLoginBodyBytes)
            return Error(413, "auth.request_too_large", "Login body is too large.");

        string? token = null;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("token", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String)
            {
                token = tokenElement.GetString();
            }
        }
        catch (JsonException)
        {
            return Error(400, "auth.invalid_request", "Login body must be JSON with a token field.");
        }

        if (string.IsNullOrEmpty(token))
            return Error(400, "auth.invalid_request", "Login body must be JSON with a token field.");

        var client = ResolveClientAddress(request);

        if (_throttle.IsBlocked(client, out var retryAfter))
        {
            Audit(request, "auth.login", "blocked", startedAt, $"retryAfterSeconds={(int)Math.Ceiling(retryAfter.TotalSeconds)}");
            return WebConsoleHttpResponse.Json(
                429,
                new { error = new { code = "auth.too_many_attempts", message = "Too many failed login attempts. Try again later." } },
                new Dictionary<string, string>
                {
                    ["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
                });
        }

        if (!_tokens.Verify(token))
        {
            _throttle.RecordFailure(client);
            Audit(request, "auth.login", "denied", startedAt);
            return Error(401, "auth.invalid_token", "Invalid token.");
        }

        _throttle.Reset(client);
        var session = _sessions.Create(WebConsoleRole.Admin, TimeSpan.FromMinutes(_options.SessionMinutes));
        var secureCookie = _origins.RequiresSecureCookie || request.IsHttps;

        Audit(request, "auth.login", "ok", startedAt, user: RoleName(session.Role));

        return WebConsoleHttpResponse.Json(
            200,
            new
            {
                authenticated = true,
                role = RoleName(session.Role),
                csrf = session.Csrf,
                expiresAt = session.ExpiresAt,
            },
            new Dictionary<string, string>
            {
                ["Set-Cookie"] = WebConsoleCookies.BuildSessionCookie(
                    session.Id,
                    _options.SessionMinutes * 60,
                    secureCookie),
            });
    }

    private async ValueTask<WebConsoleHttpResponse> SnapshotAsync(
        WebConsoleHttpRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var snapshot = await _diagnostics
            .CreateSnapshotAsync(Math.Min(_options.MaxLogLines, 200), cancellationToken)
            .ConfigureAwait(false);

        Audit(request, "snapshot.view", "ok", startedAt);
        return WebConsoleHttpResponse.Json(200, snapshot);
    }

    private async ValueTask<WebConsoleHttpResponse> SnapshotSectionAsync(
        WebConsoleHttpRequest request,
        DateTimeOffset startedAt,
        Func<WebConsoleSnapshot, object> selector,
        CancellationToken cancellationToken)
    {
        var snapshot = await _diagnostics
            .CreateSnapshotAsync(Math.Min(_options.MaxLogLines, 200), cancellationToken)
            .ConfigureAwait(false);

        Audit(request, "snapshot.section", "ok", startedAt, request.Path);
        return WebConsoleHttpResponse.Json(200, selector(snapshot));
    }

    private WebConsoleHttpResponse HandleConfigWrite(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return Error(405, "method.not_allowed", "Method not allowed.");

        if (!IsAllowedConfigId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        if (_options.ReadOnly || !_options.AllowConfigWrite)
        {
            Audit(request, "config.write", "denied", startedAt, documentId);
            return Error(403, "config.write_disabled", "Config writing is disabled by plugin configuration.");
        }

        Audit(request, "config.write", "unavailable", startedAt, documentId);
        return Error(
            501,
            WebConsoleGaps.AdminConfig.Code,
            WebConsoleGaps.AdminConfig.Summary,
            WebConsoleGaps.AdminConfig.Doc);
    }

    private WebConsoleHttpResponse HandleCommands(WebConsoleHttpRequest request, DateTimeOffset startedAt)
    {
        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return Error(405, "method.not_allowed", "Method not allowed.");

        if (!_options.AllowCommandConsole || _options.ReadOnly)
        {
            Audit(request, "command.run", "denied", startedAt);
            return Error(403, "command.disabled", "Command console is disabled by plugin configuration.");
        }

        var command = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("command", out var commandElement)
                && commandElement.ValueKind == JsonValueKind.String)
            {
                command = commandElement.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            return Error(400, "command.invalid_request", "Command body must be JSON with a command field.");
        }

        if (string.IsNullOrWhiteSpace(command))
            return Error(400, "command.invalid_request", "Command body must be JSON with a command field.");

        Audit(request, "command.run", "unavailable", startedAt, command);
        return Error(
            501,
            WebConsoleGaps.AdminCommand.Code,
            WebConsoleGaps.AdminCommand.Summary,
            WebConsoleGaps.AdminCommand.Doc);
    }

    private WebConsoleSession? ResolveSession(WebConsoleHttpRequest request)
        => _sessions.Find(WebConsoleCookies.ReadCookie(GetHeader(request, "Cookie"), WebConsoleDefaults.SessionCookieName));

    private static bool ValidateCsrf(WebConsoleHttpRequest request, WebConsoleSession session)
        => WebConsoleTokens.FixedTimeEquals(
            GetHeader(request, WebConsoleDefaults.CsrfHeaderName),
            session.Csrf);

    private string ResolveClientAddress(WebConsoleHttpRequest request)
    {
        var remote = WebConsoleNetwork.NormalizeAddress(request.RemoteAddress) ?? request.RemoteAddress;
        if (!WebConsoleNetwork.IsInAnyCidr(remote, _options.TrustedProxies))
            return remote;

        var forwarded = GetHeader(request, "X-Forwarded-For");
        if (string.IsNullOrWhiteSpace(forwarded))
            return remote;

        var first = forwarded.Split(',')[0].Trim();
        return WebConsoleNetwork.NormalizeAddress(first) ?? remote;
    }

    private void Audit(
        WebConsoleHttpRequest request,
        string action,
        string result,
        DateTimeOffset startedAt,
        string? target = null,
        string? user = null)
    {
        _audit.Write(
            action,
            user,
            ResolveClientAddress(request),
            target,
            result,
            _clock.GetUtcNow() - startedAt);
    }

    private static bool IsAllowedConfigId(string id)
    {
        if (string.Equals(id, "profiles", StringComparison.Ordinal))
            return true;

        if (!id.StartsWith("plugin:", StringComparison.Ordinal))
            return false;

        var pluginId = id["plugin:".Length..];
        return pluginId.Length is > 0 and <= 128
               && pluginId.All(character => char.IsLetterOrDigit(character)
                                            || character is '.' or '_' or '-');
    }

    private static bool IsSafeMethod(string method)
        => method is "GET" or "HEAD" or "OPTIONS";

    private static string? GetHeader(WebConsoleHttpRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : null;

    private static void RequireMethod(WebConsoleHttpRequest request, string method)
    {
        if (!string.Equals(request.Method, method, StringComparison.Ordinal))
            throw new WebConsoleMethodNotAllowedException(method);
    }

    private static int ReadLimit(WebConsoleHttpRequest request, int fallback)
    {
        var query = ParseQuery(request.Query);
        return query.TryGetValue("limit", out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 1000)
            : Math.Clamp(fallback, 1, 1000);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static WebConsoleHttpResponse Secure(WebConsoleHttpResponse response)
    {
        var headers = new Dictionary<string, string>(SecurityHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
            headers[header.Key] = header.Value;

        return response with { Headers = headers };
    }

    private static WebConsoleHttpResponse Error(int statusCode, string code, string message, string? doc = null)
        => WebConsoleHttpResponse.Json(statusCode, new
        {
            error = new
            {
                code,
                message,
                doc = doc ?? WebConsoleDefaults.DefaultDocumentationFile,
            },
        });

    private static string RoleName(WebConsoleRole role)
        => role == WebConsoleRole.Admin ? "admin" : "viewer";
}

/// <summary>Thrown when a route exists but the HTTP method is wrong; mapped to 405 by the handler.</summary>
public sealed class WebConsoleMethodNotAllowedException : Exception
{
    public WebConsoleMethodNotAllowedException(string allowedMethod)
        : base($"Method not allowed; expected {allowedMethod}.")
    {
        AllowedMethod = allowedMethod;
    }

    public string AllowedMethod { get; }
}
