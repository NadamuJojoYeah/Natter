using System.Net;
using System.Text.Json;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary>Owns the console server lifecycle and is shared with the <c>web status</c> command.</summary>
public sealed class WebConsoleRuntime : IAsyncDisposable
{
    private readonly WebConsoleIdentity _identity;
    private readonly WebConsoleOptions _options;
    private readonly WebConsoleConfigPreview _config;
    private readonly IPluginLog _log;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _stateDirectoryProvider;
    private readonly WebConsoleServerStatusHolder _status = new();

    private WebConsoleServer? _server;
    private WebConsoleAuditStore? _audit;
    private HealthEndpointHostPort? _healthPort;
    private WebConsoleSessionStore? _sessions;
    private bool _started;

    public WebConsoleRuntime(
        WebConsoleIdentity identity,
        WebConsoleOptions options,
        WebConsoleConfigPreview config,
        IPluginLog log,
        TimeProvider clock,
        Func<string?>? stateDirectoryProvider = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _stateDirectoryProvider = stateDirectoryProvider ?? WebConsoleAuditPaths.ReadStateDirectoryFromEnvironment;
    }

    public WebConsoleIdentity Identity => _identity;

    public WebConsoleOptions Options => _options;

    public WebConsoleServerStatus Status => _status.Current;

    public bool TokenConfigured { get; private set; }

    /// <summary>True when a usable viewer secret is present; viewer logins create read-only sessions.</summary>
    public bool ViewerLoginEnabled { get; private set; }

    /// <summary>Host admin contracts resolved at activation; empty when no <c>admin.*</c> permission.</summary>
    public WebConsoleAdminSurface AdminSurface { get; private set; } = WebConsoleAdminSurface.Empty;

    public string? AuditPath => _audit?.FilePath;

    /// <summary>Resolved audit location (state root or plugin fallback), exposed for `web status`/UI.</summary>
    public WebConsoleAuditLocation? AuditLocation { get; private set; }

    public async Task StartAsync(IPluginSecrets secrets, IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(provider);

        var issues = WebConsoleOptionsValidator.Validate(_options);
        if (issues.Count > 0)
        {
            throw new WebConsoleStartupException(
                issues[0].Code,
                string.Join("; ", issues.Select(issue => $"{issue.Pointer} {issue.Message}")));
        }

        var token = secrets.GetRequired(WebConsoleDefaults.TokenSecretName);
        var authenticator = new WebConsoleTokenAuthenticator(token);
        if (!authenticator.IsUsable)
        {
            throw new WebConsoleStartupException(
                authenticator.Problem ?? "webconsole.token-invalid",
                $"Secret '{WebConsoleDefaults.TokenSecretName}' is not usable. Generate at least "
                + $"{WebConsoleDefaults.MinTokenLength} characters (for example: openssl rand -base64 48).");
        }

        TokenConfigured = true;

        var viewerAuthenticator = ResolveViewerAuthenticator(secrets, authenticator);
        ViewerLoginEnabled = viewerAuthenticator is { IsUsable: true };

        // The plugin package may live on a read-only mount, so the writable state root wins.
        var auditLocation = WebConsoleAuditPaths.Resolve(_identity.PluginDirectory, _stateDirectoryProvider());
        AuditLocation = auditLocation;
        if (auditLocation.Warning is not null)
            _log.Warning(auditLocation.Warning);

        try
        {
            _audit = new WebConsoleAuditStore(auditLocation.Directory, _clock, _options.AuditRetentionFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WebConsoleStartupException(
                "webconsole.audit-unwritable",
                $"Audit directory '{auditLocation.Directory}' is not writable ({exception.GetType().Name}). "
                + "Set NATTER_STATE_DIR to a writable state root (systemd: /var/lib/natter, container: /state) "
                + "instead of widening the plugin mount.");
        }

        var assets = WebConsoleStaticAssets.Load(ResolveStaticRoot(_identity.PluginDirectory));

        _healthPort = new HealthEndpointHostPort(new Uri(_options.HostHealthUrl), client: null, _clock);
        var hostPort = new CompositeWebConsoleHostPort(
            _healthPort,
            new PluginDiagnosticsHostPort(provider));

        _sessions = new WebConsoleSessionStore(_clock);
        var origins = new WebConsoleOriginPolicy(_options.PublicBaseUrl, _options.Port);
        var surface = WebConsoleAdminSurface.FromServices(provider);
        var admin = new WebConsoleAdminGateway(surface, _options, _clock);
        var diagnostics = new WebConsoleDiagnostics(
            _identity,
            _options,
            _config,
            hostPort,
            admin,
            _status,
            _audit,
            authenticator,
            assets,
            viewerAuthenticator is { IsUsable: true },
            _clock,
            auditLocation);

        var handler = new WebConsoleRequestHandler(
            _options,
            authenticator,
            viewerAuthenticator,
            _sessions,
            new WebConsoleLoginThrottle(_clock),
            origins,
            diagnostics,
            admin,
            _audit,
            assets,
            _clock,
            _log);

        AdminSurface = surface;

        _server = new WebConsoleServer(_options, handler, _status, _log);

        try
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WebConsoleStartupException exception) when (exception.Code == "webconsole.port-in-use")
        {
            if (!await WebConsoleExternalProbe.IsExistingConsoleAsync(_options, cancellationToken).ConfigureAwait(false))
            {
                CleanUpAfterFailedStart();
                throw;
            }

            _server = null;
            _status.Set(WebConsoleServerStatus.External(WebConsoleServer.BuildUrl(_options)));
            _log.Warning(
                $"webconsole port is already served by another instance; running in external mode: {WebConsoleServer.BuildUrl(_options)}");
        }
        catch
        {
            CleanUpAfterFailedStart();
            throw;
        }

        _started = true;
        var url = _server?.Url ?? _status.Current.Url ?? WebConsoleServer.BuildUrl(_options);

        _audit.Write("plugin.start", "system", null, url, "ok", TimeSpan.Zero);
        _log.Information(
            $"webconsole ready: url={url} health={_options.HostHealthUrl} audit={_audit.FilePath}"
            + $" root={WebConsoleAuditPaths.RootName(auditLocation.Root)}");
    }

    /// <summary>
    /// Resolves the optional viewer secret; a missing or colliding credential only disables viewer
    /// logins (the admin console keeps working) and is reported through the <c>viewer-login</c> check.
    /// </summary>
    private WebConsoleTokenAuthenticator? ResolveViewerAuthenticator(
        IPluginSecrets secrets,
        WebConsoleTokenAuthenticator admin)
    {
        var viewer = WebConsoleCredentials.ResolveViewer(_options.ViewerToken, secrets, admin, out var problem);

        if (problem is null)
            return viewer;

        _log.Warning($"webconsole viewer login disabled: {problem} ({WebConsoleDefaults.ViewerTokenSecretName})");
        return null;
    }

    private void CleanUpAfterFailedStart()
    {
        _server = null;
        _healthPort?.Dispose();
        _healthPort = null;
        _audit?.Dispose();
        _audit = null;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var wasStarted = _started;
        _started = false;

        if (wasStarted)
            _audit?.Write("plugin.stop", "system", null, null, "ok", TimeSpan.Zero);

        try
        {
            _sessions?.Clear();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_server is not null)
        {
            try
            {
                await _server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warning($"webconsole server stop failed: {exception.Message}");
            }
        }

        _healthPort?.Dispose();
        _healthPort = null;

        if (wasStarted)
            _status.Set(WebConsoleServerStatus.Stopped);
    }

    public string DescribeStatus(bool json)
    {
        var status = _status.Current;
        var payload = new
        {
            plugin = _identity.PluginId,
            name = _identity.Name,
            version = _identity.Version,
            hostApi = _identity.HostApi,
            kind = _identity.Kind,
            pluginDirectory = _identity.PluginDirectory,
            server = new
            {
                state = status.State,
                url = status.Url,
                errorCode = status.ErrorCode,
                error = status.Error,
                runtime = "httplistener",
            },
            tokenConfigured = TokenConfigured,
            roles = new
            {
                adminLoginEnabled = TokenConfigured,
                viewerLoginEnabled = ViewerLoginEnabled,
                readOnly = _options.ReadOnly,
            },
            hostHealthUrl = _options.HostHealthUrl,
            // "host" = the bridge handed the host IAdminRedactor to the plugin (default when
            // admin.read is granted, S2-D5); "plugin" = the bridge is absent and the plugin-local
            // rule set is in effect. The field reports which rules actually produced the output.
            redaction = new
            {
                source = AdminSurface.Redactor is null ? "plugin" : "host",
                pemBlocks = true,
            },
            admin = new
            {
                configStore = AdminSurface.DescribeConfig(),
                commandRunner = AdminSurface.DescribeCommand(),
                logReader = AdminSurface.HasLogReader ? "available" : WebConsoleGaps.HostLogs.Code,
            },
            config = new
            {
                bind = _options.Bind,
                port = _options.Port,
                readOnly = _options.ReadOnly,
                allowConfigWrite = _options.AllowConfigWrite,
                allowCommandConsole = _options.AllowCommandConsole,
                sessionMinutes = _options.SessionMinutes,
                maxLogLines = _options.MaxLogLines,
            },
            auditPath = AuditPath,
            audit = AuditLocation is null
                ? null
                : new
                {
                    path = AuditPath,
                    directory = AuditLocation.Directory,
                    root = WebConsoleAuditPaths.RootName(AuditLocation.Root),
                    warning = AuditLocation.Warning,
                },
            gaps = ActiveGaps().Select(gap => new { code = gap.Code, area = gap.Area, doc = gap.Doc }),
        };

        if (json)
            return JsonSerializer.Serialize(payload, WebConsoleJson.Options);

        var lines = new List<string>
        {
            $"plugin: {_identity.PluginId} {_identity.Version} (hostApi {_identity.HostApi})",
            $"server: {status.State}{(status.Url is null ? string.Empty : " " + status.Url)}",
        };

        if (status.ErrorCode is not null)
            lines.Add($"error: {status.ErrorCode} {status.Error}");

        lines.Add($"token: {(TokenConfigured ? "configured" : "missing")}");
        lines.Add($"health: {_options.HostHealthUrl}");
        lines.Add($"admin: config={AdminSurface.DescribeConfig()} command={AdminSurface.DescribeCommand()}"
                  + $" logs={(AdminSurface.HasLogReader ? "available" : WebConsoleGaps.HostLogs.Code)}");
        lines.Add($"redaction: {(AdminSurface.Redactor is null ? "plugin" : "host")}");
        lines.Add($"audit: {AuditPath}"
                  + (AuditLocation is null
                      ? string.Empty
                      : $" (root={WebConsoleAuditPaths.RootName(AuditLocation.Root)})"));
        lines.Add($"gaps: {string.Join(", ", ActiveGaps().Select(gap => gap.Code))}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Gaps that still apply to this deployment (bridged contracts are not reported).</summary>
    public IReadOnlyList<WebConsoleGap> ActiveGaps()
    {
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        if (AdminSurface.HasConfigStore)
            satisfied.Add(WebConsoleGaps.AdminConfig.Code);
        if (AdminSurface.HasCommandRunner)
            satisfied.Add(WebConsoleGaps.AdminCommand.Code);
        if (AdminSurface.HasLogReader)
            satisfied.Add(WebConsoleGaps.HostLogs.Code);

        return WebConsoleGaps.All.Where(gap => !satisfied.Contains(gap.Code)).ToArray();
    }

    public static string ResolveStaticRoot(string pluginDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(pluginDirectory, "wwwroot"),
            Path.Combine(pluginDirectory, "lib", "wwwroot"),
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _audit?.Dispose();
    }
}

/// <summary>Resolves the plugin package root from the loaded assembly location.</summary>
public static class WebConsolePluginPaths
{
    public static string ResolvePluginDirectory(string? assemblyLocation)
    {
        var directory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        if (string.Equals(Path.GetFileName(directory), "lib", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }

        return directory;
    }
}

/// <summary>
/// Detects an already-running WebConsole on the configured port so <c>web status</c> keeps working
/// while the operator's host owns the listener. Only the plugin's own session endpoint is accepted.
/// </summary>
public static class WebConsoleExternalProbe
{
    public static async Task<bool> IsExistingConsoleAsync(
        WebConsoleOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
            var url = WebConsoleServer.BuildUrl(options) + "/api/session";

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Unauthorized))
                return false;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("authenticated", out var authenticated)
                   && authenticated.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
