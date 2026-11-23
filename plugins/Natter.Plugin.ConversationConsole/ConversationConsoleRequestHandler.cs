using System.Globalization;
using Natter.Application.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// Protocol-level request handler: origin, auth, read-only routing, query clamping, audit and
/// security headers live here; the transport only moves bytes.
/// <para>
/// There is exactly one verb family (<c>GET</c>/<c>HEAD</c>) and it is enforced here rather than by
/// the UI: P1 has no write path, so anything else is answered with 405 before any handler runs.
/// </para>
/// </summary>
public sealed class ConversationConsoleRequestHandler
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

    private readonly ConversationConsoleOptions _options;
    private readonly ConversationConsoleTokenAuthenticator _tokens;
    private readonly ConversationConsoleQueryGateway _query;
    private readonly ConversationConsoleAuditStore _audit;
    private readonly ConversationConsoleAuditGate _preAuthAudit;
    private readonly ConversationConsoleStaticAssets _assets;
    private readonly TimeProvider _clock;
    private readonly IPluginLog _log;

    public ConversationConsoleRequestHandler(
        ConversationConsoleOptions options,
        ConversationConsoleTokenAuthenticator tokens,
        ConversationConsoleQueryGateway query,
        ConversationConsoleAuditStore audit,
        ConversationConsoleStaticAssets assets,
        TimeProvider clock,
        IPluginLog log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _preAuthAudit = new ConversationConsoleAuditGate(clock);
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>True when the host granted <c>chat.read</c> and the query port was resolved.</summary>
    public bool QueryAvailable => _query.IsAvailable;

    public async ValueTask<ConversationConsoleHttpResponse> HandleAsync(
        ConversationConsoleHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Secure(await HandleCoreAsync(request, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log.Error(exception, "conversation console handler failed");
            return Secure(ConversationConsoleHttpResponse.Error(
                500,
                ConversationConsoleErrorCodes.Internal,
                "Internal server error."));
        }
    }

    private async ValueTask<ConversationConsoleHttpResponse> HandleCoreAsync(
        ConversationConsoleHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsSameOrigin(request))
        {
            AuditPreAuth(request, ConversationConsoleAuditActions.OriginDenied);
            return ConversationConsoleHttpResponse.Error(
                403,
                ConversationConsoleErrorCodes.OriginDenied,
                "Origin is not allowed.");
        }

        var isRead = string.Equals(request.Method, "GET", StringComparison.Ordinal)
                     || string.Equals(request.Method, "HEAD", StringComparison.Ordinal);

        if (isRead && _assets.TryGet(request.Path, out var asset))
            return asset;

        if (!request.Path.StartsWith("/api/", StringComparison.Ordinal))
            return ConversationConsoleHttpResponse.Error(404, ConversationConsoleErrorCodes.RouteNotFound, "Route not found.");

        // P1 is read-only: no route is reachable with a mutating verb, even with a valid token.
        if (!isRead)
        {
            return ConversationConsoleHttpResponse.Json(
                405,
                new { error = new { code = ConversationConsoleErrorCodes.MethodNotAllowed, message = "This console is read-only." } },
                new Dictionary<string, string> { ["Allow"] = "GET, HEAD" });
        }

        var authorization = GetHeader(request, ConversationConsoleDefaults.AuthorizationHeader);
        if (string.IsNullOrWhiteSpace(authorization))
            return Unauthorized(request, ConversationConsoleErrorCodes.AuthRequired, "A console token is required.");

        if (!_tokens.IsAuthorized(authorization))
            return Unauthorized(request, ConversationConsoleErrorCodes.AuthInvalid, "The console token is not valid.");

        if (string.Equals(request.Path, "/api/health", StringComparison.Ordinal))
            return Health();

        if (string.Equals(request.Path, "/api/sessions", StringComparison.Ordinal))
            return await ListAsync(request, cancellationToken).ConfigureAwait(false);

        if (TryMatchMessagesRoute(request.Path, out var sessionId))
            return await MessagesAsync(request, sessionId, cancellationToken).ConfigureAwait(false);

        return ConversationConsoleHttpResponse.Error(404, ConversationConsoleErrorCodes.RouteNotFound, "Route not found.");
    }

    private ConversationConsoleHttpResponse Health()
    {
        var writable = _audit.CanWrite(out var auditError);

        return ConversationConsoleHttpResponse.Json(200, new
        {
            status = "ok",
            readOnly = _options.ReadOnly,
            writePaths = 0,
            query = new
            {
                source = _query.Source,
                available = _query.IsAvailable,
            },
            pageSize = _options.PageSize,
            messagePageSize = _options.MessagePageSize,
            limits = new
            {
                maxPageSize = ConversationQueryLimits.MaxPageSize,
                maxMessagePageSize = ConversationQueryLimits.MaxMessagePageSize,
                maxMessageCharacters = ConversationConsoleQueryGateway.MaxMessageCharacters,
            },
            audit = new
            {
                root = ConversationConsoleAuditPaths.StateDirectoryRoot,
                writable,
                error = auditError,
            },
        });
    }

    private async ValueTask<ConversationConsoleHttpResponse> ListAsync(
        ConversationConsoleHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!_query.IsAvailable)
            return QueryUnavailable();

        var parameters = ParseQuery(request.Query);

        if (!TryGetLimit(parameters, _options.PageSize, ConversationQueryLimits.MaxPageSize, out var limit, out var limitError))
            return InvalidFilter(limitError);

        if (!TryGetInstant(parameters, "since", out var since, out var sinceError))
            return InvalidFilter(sinceError);

        if (!TryGetInstant(parameters, "until", out var until, out var untilError))
            return InvalidFilter(untilError);

        var filter = new ConversationFilter(
            SourceKey: Value(parameters, "source"),
            ActorId: Value(parameters, "actor"),
            Since: since,
            Until: until,
            Query: Value(parameters, "q"),
            Limit: limit,
            Cursor: Value(parameters, "cursor"));

        var page = await _query.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        if (page is null)
            return QueryUnavailable();

        // Audit records how much was exposed, never what was in it.
        _audit.Write(ConversationConsoleAuditActions.List, null, page.Items.Count, ConversationConsoleTokenAuthenticator.Actor);

        return ConversationConsoleHttpResponse.Json(200, new
        {
            items = page.Items,
            nextCursor = page.NextCursor,
            limit,
        });
    }

    private async ValueTask<ConversationConsoleHttpResponse> MessagesAsync(
        ConversationConsoleHttpRequest request,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (!_query.IsAvailable)
            return QueryUnavailable();

        var parameters = ParseQuery(request.Query);

        if (!TryGetLimit(parameters, _options.MessagePageSize, ConversationQueryLimits.MaxMessagePageSize, out var limit, out var limitError))
            return InvalidFilter(limitError);

        if (!TryGetLong(parameters, "after", out var after, out var afterError))
            return InvalidFilter(afterError);

        var messages = await _query.GetMessagesAsync(sessionId, after, limit, cancellationToken).ConfigureAwait(false);
        if (messages is null)
            return QueryUnavailable();

        _audit.Write(
            ConversationConsoleAuditActions.View,
            sessionId,
            messages.Count,
            ConversationConsoleTokenAuthenticator.Actor);

        var nextAfter = messages.Count > 0 ? messages[^1].MessageId : after;

        return ConversationConsoleHttpResponse.Json(200, new
        {
            sessionId,
            items = messages,
            nextAfter,
            limit,
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Same-origin policy: the console is a loopback page, so a request that carries a foreign
    /// <c>Origin</c> (or is flagged cross-site by the browser) is a cross-site read attempt.
    /// </summary>
    private bool IsSameOrigin(ConversationConsoleHttpRequest request)
    {
        var fetchSite = GetHeader(request, "Sec-Fetch-Site");
        if (!string.IsNullOrWhiteSpace(fetchSite)
            && (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var origin = GetHeader(request, "Origin");
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        if (!ConversationConsoleNetwork.TryGetOriginAuthority(origin, out var authority))
            return false;

        foreach (var allowed in AllowedAuthorities())
        {
            if (string.Equals(authority, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private IEnumerable<string> AllowedAuthorities()
    {
        yield return ConversationConsoleNetwork.BuildUrl(_options.Bind, _options.Port)["http://".Length..];

        // The browser sends whatever the address bar says, and 127.0.0.1 / localhost / [::1] are the
        // same server. Only echoing back the exact configured Bind made `Origin:
        // http://127.0.0.1:5091` fail with request.origin_denied when Bind was `localhost`, and the
        // other way round. Bound to loopback => accept every loopback spelling on that port.
        if (ConversationConsoleNetwork.IsLoopbackHost(_options.Bind))
        {
            var port = _options.Port.ToString(CultureInfo.InvariantCulture);
            yield return "127.0.0.1:" + port;
            yield return "localhost:" + port;
            yield return "[::1]:" + port;
        }
    }

    private ConversationConsoleHttpResponse Unauthorized(
        ConversationConsoleHttpRequest request,
        string code,
        string message)
    {
        AuditPreAuth(request, ConversationConsoleAuditActions.AuthDenied);

        return ConversationConsoleHttpResponse.Json(
            401,
            new { error = new { code, message } },
            new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" });
    }

    private ConversationConsoleHttpResponse QueryUnavailable()
        => ConversationConsoleHttpResponse.Error(
            501,
            ConversationConsoleErrorCodes.QueryUnavailable,
            "The host did not expose IConversationQuery to this plugin. Grant 'chat.read' to the "
            + "conversation console in the Natter plugin configuration and restart the host.");

    private static ConversationConsoleHttpResponse InvalidFilter(string message)
        => ConversationConsoleHttpResponse.Error(400, ConversationConsoleErrorCodes.FilterInvalid, message);

    private void AuditPreAuth(ConversationConsoleHttpRequest request, string action)
    {
        // Unauthenticated writes are attacker-influenceable, so they are throttled per client.
        if (!_preAuthAudit.TryAcquire(action, request.RemoteAddress, out _))
            return;

        _audit.Write(action, null, null, "anonymous@" + request.RemoteAddress);
    }

    private static ConversationConsoleHttpResponse Secure(ConversationConsoleHttpResponse response)
    {
        var headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var header in SecurityHeaders)
            headers[header.Key] = header.Value;

        return response with { Headers = headers };
    }

    private static string? GetHeader(ConversationConsoleHttpRequest request, string name)
        => request.Headers.TryGetValue(name, out var value) ? value : null;

    private static bool TryMatchMessagesRoute(string path, out string sessionId)
    {
        sessionId = string.Empty;

        const string prefix = "/api/sessions/";
        const string suffix = "/messages";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var segment = path[prefix.Length..^suffix.Length];
        if (segment.Length < 2 || segment.Contains('/', StringComparison.Ordinal))
            return false;

        sessionId = Uri.UnescapeDataString(segment);
        return sessionId.Length > 0;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
            return result;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];

            var decodedKey = Uri.UnescapeDataString(key.Replace('+', ' ')).Trim();
            if (decodedKey.Length == 0)
                continue;

            result[decodedKey] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private static string? Value(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool TryGetLimit(
        Dictionary<string, string> parameters,
        int fallback,
        int hardLimit,
        out int limit,
        out string error)
    {
        limit = Math.Clamp(fallback, 1, hardLimit);
        error = string.Empty;

        if (!parameters.TryGetValue("limit", out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
        {
            error = $"limit must be an integer, got '{raw}'.";
            return false;
        }

        limit = requested <= 0 ? fallback : Math.Min(requested, hardLimit);
        return true;
    }

    private static bool TryGetLong(
        Dictionary<string, string> parameters,
        string name,
        out long value,
        out string error)
    {
        value = 0;
        error = string.Empty;

        if (!parameters.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            error = $"{name} must be a non-negative integer, got '{raw}'.";
            return false;
        }

        return true;
    }

    private static bool TryGetInstant(
        Dictionary<string, string> parameters,
        string name,
        out DateTimeOffset? value,
        out string error)
    {
        value = null;
        error = string.Empty;

        if (!parameters.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = $"{name} must be an ISO-8601 timestamp, got '{raw}'.";
            return false;
        }

        value = parsed;
        return true;
    }
}
