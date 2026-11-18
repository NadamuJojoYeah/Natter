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
        TimeProvider clock)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public WebConsoleIdentity Identity => _identity;

    public WebConsoleOptions Options => _options;

    public WebConsoleServerStatus Status => _status.Current;

    public bool TokenConfigured { get; private set; }

    public string? AuditPath => _audit?.FilePath;

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

        var auditDirectory = Path.Combine(_identity.PluginDirectory, WebConsoleDefaults.AuditDirectoryName);
        _audit = new WebConsoleAuditStore(auditDirectory, _clock);

        var assets = WebConsoleStaticAssets.Load(ResolveStaticRoot(_identity.PluginDirectory));

        _healthPort = new HealthEndpointHostPort(new Uri(_options.HostHealthUrl), client: null, _clock);
        var hostPort = new CompositeWebConsoleHostPort(
            _healthPort,
            new PluginDiagnosticsHostPort(provider));

        _sessions = new WebConsoleSessionStore(_clock);
        var origins = new WebConsoleOriginPolicy(_options.PublicBaseUrl, _options.Port);
        var diagnostics = new WebConsoleDiagnostics(
            _identity,
            _options,
            _config,
            hostPort,
            _status,
            _audit,
            authenticator,
            assets,
            _clock);

        var handler = new WebConsoleRequestHandler(
            _options,
            authenticator,
            _sessions,
            new WebConsoleLoginThrottle(_clock),
            origins,
            diagnostics,
            _audit,
            assets,
            _clock,
            _log);

        _server = new WebConsoleServer(_options, handler, _status, _log);
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        _started = true;

        _audit.Write("plugin.start", "system", null, _server.Url, "ok", TimeSpan.Zero);
        _log.Information(
            $"webconsole ready: url={_server.Url} health={_options.HostHealthUrl} audit={_audit.FilePath}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        _started = false;

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
            hostHealthUrl = _options.HostHealthUrl,
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
            gaps = WebConsoleGaps.All.Select(gap => new { code = gap.Code, area = gap.Area, doc = gap.Doc }),
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
        lines.Add($"audit: {AuditPath}");
        lines.Add($"gaps: {string.Join(", ", WebConsoleGaps.All.Select(gap => gap.Code))}");

        return string.Join(Environment.NewLine, lines);
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
