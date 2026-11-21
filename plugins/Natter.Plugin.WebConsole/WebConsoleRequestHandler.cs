using System.Globalization;
using System.Text;
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
    private readonly WebConsoleAdminGateway _admin;
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
        WebConsoleAdminGateway admin,
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
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
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

        if ((string.Equals(request.Method, "GET", StringComparison.Ordinal)
             || string.Equals(request.Method, "HEAD", StringComparison.Ordinal))
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
            {
                RequireMethod(request, "GET");
                Audit(request, "config.view", "ok", startedAt);

                var preview = _diagnostics.ConfigPreview;
                var admin = _diagnostics.AdminStatus;
                var documents = await _admin.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);

                return WebConsoleHttpResponse.Json(200, new
                {
                    preview.Source,
                    preview.SchemaPath,
                    preview.WriteEnabled,
                    preview.WriteGapCode,
                    preview.Values,
                    preview.Redacted,
                    admin = new
                    {
                        configStoreAvailable = admin.ConfigStoreAvailable,
                        commandRunnerAvailable = admin.CommandRunnerAvailable,
                        logReaderAvailable = admin.LogReaderAvailable,
                    },
                    documents,
                });
            }

            case "admin":
            {
                RequireMethod(request, "GET");
                Audit(request, "admin.status", "ok", startedAt);
                return WebConsoleHttpResponse.Json(200, _diagnostics.AdminStatus);
            }

            case "admin/documents":
            {
                RequireMethod(request, "GET");
                if (!_admin.Surface.HasConfigStore)
                {
                    Audit(request, "config.documents", "unavailable", startedAt);
                    return Failure(WebConsoleAdminFailures.ConfigUnavailable);
                }

                var documents = await _admin.ListDocumentsAsync(cancellationToken).ConfigureAwait(false);
                Audit(request, "config.documents", "ok", startedAt, $"count={documents.Count}");
                return WebConsoleHttpResponse.Json(200, new { documents });
            }

            case "commands":
                return await HandleCommandsAsync(request, startedAt, cancellationToken).ConfigureAwait(false);

            case "commands/validate":
                return HandleCommandValidate(request, startedAt);
        }

        if (route.StartsWith("config/", StringComparison.Ordinal))
            return await HandleConfigRouteAsync(request, route["config/".Length..], startedAt, cancellationToken)
                .ConfigureAwait(false);

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
                        ["Set-Cookie"] = WebConsoleCookies.BuildExpiredCookie(IsSecureRequest(request)),
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
        var secureCookie = IsSecureRequest(request);

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

    private async ValueTask<WebConsoleHttpResponse> HandleConfigRouteAsync(
        WebConsoleHttpRequest request,
        string remainder,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        // Only known sub-resources are split off; anything else stays part of the id so a
        // path-like id is rejected by the whitelist (400) instead of looking like a missing route.
        var slash = remainder.LastIndexOf('/');
        var last = slash > 0 ? remainder[(slash + 1)..] : null;
        var verb = last is "validate" or "undo" or "rollback" ? last : null;
        var documentId = verb is null ? remainder : remainder[..slash];

        if (string.Equals(verb, "validate", StringComparison.Ordinal))
            return await HandleConfigValidateAsync(request, documentId, startedAt, cancellationToken)
                .ConfigureAwait(false);

        if (string.Equals(verb, "undo", StringComparison.Ordinal))
        {
            RequireMethod(request, "GET");
            return HandleConfigUndoList(request, documentId, startedAt);
        }

        if (string.Equals(verb, "rollback", StringComparison.Ordinal))
            return await HandleConfigRollbackAsync(request, documentId, startedAt, cancellationToken)
                .ConfigureAwait(false);

        return request.Method switch
        {
            "GET" => await HandleConfigReadAsync(request, documentId, startedAt, cancellationToken).ConfigureAwait(false),
            "POST" => await HandleConfigSaveAsync(request, documentId, startedAt, cancellationToken).ConfigureAwait(false),
            _ => Error(405, "method.not_allowed", "Method not allowed."),
        };
    }

    private async ValueTask<WebConsoleHttpResponse> HandleConfigReadAsync(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (!WebConsoleConfigEditPolicy.IsAllowedId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        if (!_admin.Surface.HasConfigStore)
        {
            Audit(request, "config.read", "unavailable", startedAt, documentId);
            return Failure(WebConsoleAdminFailures.ConfigUnavailable);
        }

        var result = await _admin.ReadAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            Audit(request, "config.read", "denied", startedAt, documentId);
            return Failure(result.Failure!);
        }

        Audit(request, "config.read", "ok", startedAt, documentId);
        return WebConsoleHttpResponse.Json(200, result.Value!);
    }

    private WebConsoleHttpResponse HandleConfigUndoList(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt)
    {
        if (!WebConsoleConfigEditPolicy.IsAllowedId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        var entries = _admin.ListUndo(documentId);
        Audit(request, "config.undo.list", "ok", startedAt, $"{documentId} count={entries.Count}");
        return WebConsoleHttpResponse.Json(200, new { id = documentId, entries });
    }

    private async ValueTask<WebConsoleHttpResponse> HandleConfigRollbackAsync(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        RequireMethod(request, "POST");

        if (!WebConsoleConfigEditPolicy.IsAllowedId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        if (_options.ReadOnly || !_options.AllowConfigWrite)
        {
            Audit(request, "config.rollback", "denied", startedAt, documentId);
            return Error(403, "config.write_disabled", "Config writing is disabled by plugin configuration.");
        }

        if (BodyTooLarge(request, WebConsoleDefaults.MaxAdminBodyBytes))
            return Error(413, "config.request_too_large", "Config body is too large.");

        if (!TryParseRollbackBody(request.Body, out var etag, out var undoId, out var parseError))
            return Error(400, "config.invalid_request", parseError ?? "Rollback body must be JSON with an optional etag.");

        var result = await _admin.RollbackAsync(documentId, etag, undoId, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            Audit(request, "config.rollback", result.Failure!.StatusCode == 501 ? "unavailable" : "denied", startedAt, documentId);
            return Failure(result.Failure);
        }

        Audit(request, "config.rollback", "ok", startedAt, documentId);
        return WebConsoleHttpResponse.Json(200, new { id = documentId, saved = result.Value! });
    }

    private async ValueTask<WebConsoleHttpResponse> HandleConfigSaveAsync(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (!WebConsoleConfigEditPolicy.IsAllowedId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        if (_options.ReadOnly || !_options.AllowConfigWrite)
        {
            Audit(request, "config.write", "denied", startedAt, documentId);
            return Error(403, "config.write_disabled", "Config writing is disabled by plugin configuration.");
        }

        if (BodyTooLarge(request, WebConsoleDefaults.MaxAdminBodyBytes))
            return Error(413, "config.request_too_large", "Config body is too large.");

        if (!TryParseConfigEditBody(request.Body, out var content, out var etag, out var parseError))
            return Error(400, "config.invalid_request", parseError ?? "Config body must be JSON with a content field.");

        // Plugin-side policy runs first so the documented 400 contract holds even without a host store.
        var validation = WebConsoleConfigEditPolicy.Validate(documentId, content);
        if (!validation.Valid)
        {
            Audit(request, "config.write", "invalid", startedAt, documentId);
            return WebConsoleHttpResponse.Json(400, new
            {
                error = new
                {
                    code = "config.invalid",
                    message = "Config content failed validation.",
                    doc = WebConsoleGaps.AdminConfig.Doc,
                },
                id = documentId,
                etag,
                validation,
            });
        }

        if (!_admin.Surface.HasConfigStore)
        {
            Audit(request, "config.write", "unavailable", startedAt, documentId);
            return Failure(WebConsoleAdminFailures.ConfigUnavailable);
        }

        var result = await _admin.SaveAsync(documentId, content, etag, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            Audit(
                request,
                "config.write",
                result.Failure!.StatusCode == 501 ? "unavailable" : "denied",
                startedAt,
                documentId);
            return Failure(result.Failure);
        }

        Audit(request, "config.write", "ok", startedAt, documentId);
        return WebConsoleHttpResponse.Json(200, result.Value!);
    }

    private static bool TryParseRollbackBody(string body, out string? etag, out string? undoId, out string? error)
    {
        etag = null;
        undoId = null;
        error = null;

        if (string.IsNullOrWhiteSpace(body))
            return true;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Rollback body must be a JSON object.";
                return false;
            }

            if (root.TryGetProperty("etag", out var etagElement))
            {
                if (etagElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error = "etag must be a string or null.";
                    return false;
                }

                etag = etagElement.GetString();
            }

            if (root.TryGetProperty("undoId", out var undoElement))
            {
                if (undoElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error = "undoId must be a string or null.";
                    return false;
                }

                undoId = undoElement.GetString();
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = $"Rollback body is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private async ValueTask<WebConsoleHttpResponse> HandleConfigValidateAsync(
        WebConsoleHttpRequest request,
        string documentId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        RequireMethod(request, "POST");

        if (!WebConsoleConfigEditPolicy.IsAllowedId(documentId))
            return Error(400, "config.id_invalid", "Config id must be 'profiles' or 'plugin:<plugin-id>'.");

        if (BodyTooLarge(request, WebConsoleDefaults.MaxAdminBodyBytes))
            return Error(413, "config.request_too_large", "Config body is too large.");

        if (!TryParseConfigEditBody(request.Body, out var content, out _, out var parseError))
            return Error(400, "config.invalid_request", parseError ?? "Config body must be JSON with a content field.");

        // Host validation adds schema checks, the bounded diff against the stored document and the
        // reload/restart hint. Without a store the plugin policy still answers (Phase 1 contract).
        if (_admin.Surface.HasConfigStore)
        {
            var hostResult = await _admin.ValidateAsync(documentId, content, cancellationToken).ConfigureAwait(false);
            if (hostResult.Ok && hostResult.Value is { } host)
            {
                Audit(request, "config.validate", host.Valid ? "ok" : "invalid", startedAt, documentId);
                return WebConsoleHttpResponse.Json(200, new
                {
                    id = documentId,
                    validation = new
                    {
                        host.Valid,
                        host.ETag,
                        host.SizeBytes,
                        host.ContainsSecretLiteral,
                        host.Issues,
                        host.Code,
                        host.Source,
                        host.ReloadHint,
                        host.RestartRequired,
                        host.Diff,
                    },
                });
            }
        }

        var validation = WebConsoleConfigEditPolicy.Validate(documentId, content);
        Audit(request, "config.validate", validation.Valid ? "ok" : "invalid", startedAt, documentId);

        return WebConsoleHttpResponse.Json(200, new
        {
            id = documentId,
            validation = new
            {
                validation.Valid,
                validation.ETag,
                validation.SizeBytes,
                validation.ContainsSecretLiteral,
                validation.Issues,
                Code = validation.Valid ? "config.valid" : "config.invalid",
                Source = "plugin",
                ReloadHint = "none",
                RestartRequired = false,
                Diff = (object?)null,
            },
        });
    }

    private async ValueTask<WebConsoleHttpResponse> HandleCommandsAsync(
        WebConsoleHttpRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            var status = _diagnostics.AdminStatus;
            Audit(request, "command.status", "ok", startedAt);
            return WebConsoleHttpResponse.Json(200, new
            {
                enabled = status.CommandEnabled,
                readOnly = _options.ReadOnly,
                runnerAvailable = status.CommandRunnerAvailable,
                gapCode = status.CommandGapCode,
                allowlist = status.CommandAllowlist,
                timeoutSeconds = status.CommandTimeoutSeconds,
            });
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            return Error(405, "method.not_allowed", "Method not allowed.");

        if (!_options.AllowCommandConsole || _options.ReadOnly)
        {
            Audit(request, "command.run", "denied", startedAt);
            return Error(403, "command.disabled", "Command console is disabled by plugin configuration.");
        }

        if (BodyTooLarge(request, WebConsoleDefaults.MaxCommandBodyBytes))
            return Error(413, "command.request_too_large", "Command body is too large.");

        if (!WebConsoleCommandPolicy.TryParseRequest(request.Body, out var commandRequest, out var parseError))
            return Error(400, "command.invalid_request", parseError ?? "Invalid command request.");

        var decision = WebConsoleCommandPolicy.Evaluate(commandRequest, _options.CommandAllowlist);
        if (!decision.Allowed)
        {
            Audit(request, "command.run", "denied", startedAt, decision.Command);
            return Error(403, decision.Code, decision.Message);
        }

        if (!_admin.Surface.HasCommandRunner)
        {
            Audit(request, "command.run", "unavailable", startedAt, decision.Command);
            return Failure(WebConsoleAdminFailures.CommandUnavailable);
        }

        var result = await _admin
            .RunAsync(decision.Command, decision.Args, cancellationToken)
            .ConfigureAwait(false);

        if (result.Value is { } output)
        {
            var outcome = result.Ok
                ? (output.ExitCode == 0 ? "ok" : "failed")
                : output.TimedOut ? "timeout" : "denied";
            Audit(request, "command.run", outcome, startedAt, decision.Command);
        }
        else
        {
            Audit(request, "command.run", "error", startedAt, decision.Command);
        }

        if (!result.Ok)
            return Failure(result.Failure!, result.Value);

        return WebConsoleHttpResponse.Json(200, result.Value!);
    }

    private WebConsoleHttpResponse HandleCommandValidate(WebConsoleHttpRequest request, DateTimeOffset startedAt)
    {
        RequireMethod(request, "POST");

        if (!_options.AllowCommandConsole || _options.ReadOnly)
        {
            Audit(request, "command.validate", "denied", startedAt);
            return Error(403, "command.disabled", "Command console is disabled by plugin configuration.");
        }

        if (BodyTooLarge(request, WebConsoleDefaults.MaxCommandBodyBytes))
            return Error(413, "command.request_too_large", "Command body is too large.");

        if (!WebConsoleCommandPolicy.TryParseRequest(request.Body, out var commandRequest, out var parseError))
            return Error(400, "command.invalid_request", parseError ?? "Invalid command request.");

        var decision = WebConsoleCommandPolicy.Evaluate(commandRequest, _options.CommandAllowlist);
        Audit(request, "command.validate", decision.Allowed ? "ok" : "denied", startedAt, decision.Command);

        return WebConsoleHttpResponse.Json(200, new { decision });
    }

    private static bool TryParseConfigEditBody(
        string body,
        out string content,
        out string? etag,
        out string? error)
    {
        content = string.Empty;
        etag = null;
        error = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                error = "Config body must be a JSON object with a content field.";
                return false;
            }

            content = contentElement.GetString() ?? string.Empty;

            if (root.TryGetProperty("etag", out var etagElement))
            {
                if (etagElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    error = "etag must be a string or null.";
                    return false;
                }

                etag = etagElement.GetString();
            }

            return true;
        }
        catch (JsonException exception)
        {
            error = $"Config body is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static bool BodyTooLarge(WebConsoleHttpRequest request, int limit)
        => Encoding.UTF8.GetByteCount(request.Body) > limit;

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

    private bool IsSecureRequest(WebConsoleHttpRequest request)
    {
        if (request.IsHttps)
            return true;

        var remote = WebConsoleNetwork.NormalizeAddress(request.RemoteAddress) ?? request.RemoteAddress;
        if (!WebConsoleNetwork.IsInAnyCidr(remote, _options.TrustedProxies))
            return false;

        return string.Equals(GetHeader(request, "X-Forwarded-Proto"), "https", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Maps a gateway failure to the documented error envelope, keeping any partial result.</summary>
    private static WebConsoleHttpResponse Failure(WebConsoleAdminFailure failure, object? result = null)
        => WebConsoleHttpResponse.Json(failure.StatusCode, new
        {
            error = new
            {
                code = failure.Code,
                message = failure.Message,
                doc = failure.Documentation,
            },
            detail = failure.Detail,
            result,
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
