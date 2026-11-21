using System.Text.Json;

namespace Natter.Plugin.WebConsole;

public sealed record WebConsoleIdentity(
    string PluginId,
    string Name,
    string Version,
    string Kind,
    string HostApi,
    string AssemblyName,
    string PluginDirectory,
    string ManifestPath);

public sealed record WebConsoleHealthCheck(string Name, string Status, string? Detail);

public sealed record WebConsoleSelf(
    string PluginId,
    string Name,
    string Version,
    string Kind,
    string HostApi,
    string HealthStatus,
    string ServerState,
    string? ServerUrl,
    string Bind,
    int Port,
    bool ReadOnly,
    bool AllowConfigWrite,
    bool AllowCommandConsole,
    int SessionMinutes,
    int MaxLogLines,
    bool TokenConfigured,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    string ServerRuntime,
    string HostDataSource);

public sealed record WebConsoleLogEntry(DateTimeOffset Timestamp, string Level, string Source, string Message);

/// <summary>Phase 2/3 availability as seen by the plugin (host contracts are permission-gated).</summary>
public sealed record WebConsoleAdminStatus(
    bool ConfigStoreAvailable,
    bool ConfigWriteEnabled,
    string? ConfigGapCode,
    bool CommandRunnerAvailable,
    bool CommandEnabled,
    string? CommandGapCode,
    IReadOnlyList<string> CommandAllowlist,
    int CommandTimeoutSeconds,
    bool LogReaderAvailable);

public sealed record WebConsoleSnapshot(
    DateTimeOffset GeneratedAt,
    WebConsoleSelf Self,
    IReadOnlyList<WebConsoleHealthCheck> Checks,
    WebConsoleSection<WebConsoleHealth> Health,
    WebConsoleSection<WebConsolePlugins> Plugins,
    WebConsoleSection<IReadOnlyList<WebConsoleUpstream>> Upstreams,
    WebConsoleSection<IReadOnlyList<WebConsoleProfile>> Profiles,
    WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>> Logs,
    WebConsoleConfigPreview Config,
    WebConsoleAdminStatus Admin,
    IReadOnlyList<WebConsoleGap> Gaps,
    IReadOnlyList<WebConsoleAuditEntry> Audit);

/// <summary>
/// Builds the console snapshot from the plugin's own state plus <see cref="IWebConsoleHostPort"/>.
/// Missing host contracts degrade individual sections; they never block login or health.
/// </summary>
public sealed class WebConsoleDiagnostics
{
    private readonly WebConsoleIdentity _identity;
    private readonly WebConsoleOptions _options;
    private readonly WebConsoleConfigPreview _config;
    private readonly IWebConsoleHostPort _hostPort;
    private readonly WebConsoleAdminGateway _admin;
    private readonly WebConsoleServerStatusHolder _serverStatus;
    private readonly WebConsoleAuditStore _audit;
    private readonly WebConsoleTokenAuthenticator _tokens;
    private readonly WebConsoleStaticAssets _assets;
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;

    public WebConsoleDiagnostics(
        WebConsoleIdentity identity,
        WebConsoleOptions options,
        WebConsoleConfigPreview config,
        IWebConsoleHostPort hostPort,
        WebConsoleAdminGateway admin,
        WebConsoleServerStatusHolder serverStatus,
        WebConsoleAuditStore audit,
        WebConsoleTokenAuthenticator tokens,
        WebConsoleStaticAssets assets,
        TimeProvider clock)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _hostPort = hostPort ?? throw new ArgumentNullException(nameof(hostPort));
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
        _serverStatus = serverStatus ?? throw new ArgumentNullException(nameof(serverStatus));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _startedAt = clock.GetUtcNow();
    }

    /// <summary>Config preview with the live Phase 2 write gap resolved from the admin surface.</summary>
    public WebConsoleConfigPreview ConfigPreview => _config with { WriteGapCode = BuildAdminStatus().ConfigGapCode };

    public WebConsoleAdminStatus AdminStatus => BuildAdminStatus();

    public IReadOnlyList<WebConsoleGap> Gaps => BuildGaps([]);

    public async ValueTask<WebConsoleSnapshot> CreateSnapshotAsync(
        int auditLimit = 50,
        CancellationToken cancellationToken = default)
    {
        var host = await _hostPort.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var status = _serverStatus.Current;
        var admin = BuildAdminStatus();
        var logs = await _admin
            .ReadLogsAsync(Math.Clamp(auditLimit <= 0 ? 50 : auditLimit, 1, Math.Max(1, _options.MaxLogLines)), cancellationToken)
            .ConfigureAwait(false);

        var auditWritable = _audit.CanWrite(out var auditError);

        var checks = new List<WebConsoleHealthCheck>
        {
            new(
                "server",
                status.State is "listening" or "external" ? "ok" : "degraded",
                status.State == "external" ? $"external instance {status.Url}" : status.Url ?? status.ErrorCode),
            new("token", _tokens.IsUsable ? "ok" : "error", _tokens.Problem),
            new("static-assets", "ok", $"{_assets.Files.Count} whitelisted assets"),
            new("audit", auditWritable ? "ok" : "error", auditError),
            new("host.health", host.Health.Available ? "ok" : "degraded", host.Health.GapCode ?? host.Health.Reason),
            new("host.plugins", host.Plugins.Available ? "ok" : "degraded", host.Plugins.Data?.Source ?? host.Plugins.GapCode),
            new("host.admin_config", admin.ConfigStoreAvailable ? "ok" : "degraded", admin.ConfigGapCode ?? "available"),
            new("host.admin_command", admin.CommandRunnerAvailable ? "ok" : "degraded", admin.CommandGapCode ?? "available"),
            new("host.logs", logs.Available ? "ok" : "degraded", logs.GapCode ?? $"{logs.Data?.Count ?? 0} entries"),
        };

        var overall = checks.Any(check => check.Status == "error")
            ? "error"
            : checks.Any(check => check.Status == "degraded")
                ? "degraded"
                : "ok";

        var self = new WebConsoleSelf(
            _identity.PluginId,
            _identity.Name,
            _identity.Version,
            _identity.Kind,
            _identity.HostApi,
            overall,
            status.State,
            status.Url,
            _options.Bind,
            _options.Port,
            _options.ReadOnly,
            _options.AllowConfigWrite,
            _options.AllowCommandConsole,
            _options.SessionMinutes,
            _options.MaxLogLines,
            _tokens.IsUsable,
            _startedAt,
            Math.Max(0, (long)(now - _startedAt).TotalSeconds),
            "httplistener",
            host.Health.Available ? "health-endpoint" : "unavailable");

        return new WebConsoleSnapshot(
            now,
            self,
            checks,
            host.Health,
            host.Plugins,
            host.Upstreams,
            host.Profiles,
            logs,
            ConfigPreview,
            admin,
            BuildGaps(host.Gaps, host),
            _audit.Read(auditLimit));
    }

    private WebConsoleAdminStatus BuildAdminStatus()
    {
        var surface = _admin.Surface;
        var writeEnabled = !_options.ReadOnly && _options.AllowConfigWrite;
        var commandEnabled = !_options.ReadOnly && _options.AllowCommandConsole;

        return new WebConsoleAdminStatus(
            surface.HasConfigStore,
            writeEnabled,
            writeEnabled
                ? (surface.HasConfigStore ? null : WebConsoleGaps.AdminConfig.Code)
                : "webconsole.write_disabled",
            surface.HasCommandRunner,
            commandEnabled,
            commandEnabled
                ? (surface.HasCommandRunner ? null : WebConsoleGaps.AdminCommand.Code)
                : "webconsole.command_disabled",
            _options.CommandAllowlist,
            _options.CommandTimeoutSeconds,
            surface.HasLogReader);
    }

    /// <summary>
    /// Gaps are "what the host still cannot do". Once a host contract is bridged the matching gap
    /// disappears from the console instead of being reported forever.
    /// </summary>
    private IReadOnlyList<WebConsoleGap> BuildGaps(
        IReadOnlyList<WebConsoleGap> hostGaps,
        WebConsoleHostSnapshot? host = null)
    {
        var satisfied = new HashSet<string>(StringComparer.Ordinal);

        if (_admin.Surface.HasConfigStore)
            satisfied.Add(WebConsoleGaps.AdminConfig.Code);
        if (_admin.Surface.HasCommandRunner)
            satisfied.Add(WebConsoleGaps.AdminCommand.Code);
        if (_admin.Surface.HasLogReader)
            satisfied.Add(WebConsoleGaps.HostLogs.Code);
        if (host?.Plugins.Available == true)
            satisfied.Add(WebConsoleGaps.PluginRuntimeDiagnostics.Code);

        return WebConsoleGaps.All
            .Concat(hostGaps)
            .DistinctBy(gap => gap.Code)
            .Where(gap => !satisfied.Contains(gap.Code))
            .ToArray();
    }

    public static string Serialize(WebConsoleSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, WebConsoleJson.Options);
}
