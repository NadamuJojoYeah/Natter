using System.Globalization;
using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary>One snapshot section. Unavailable sections always carry a gap code instead of fabricated data.</summary>
public sealed record WebConsoleSection<T>(
    bool Available,
    T? Data,
    string? GapCode = null,
    string? Reason = null,
    string? Doc = null,
    DateTimeOffset? ObservedAt = null)
{
    public static WebConsoleSection<T> Ok(T data, DateTimeOffset? observedAt = null, string? doc = null)
        => new(true, data, ObservedAt: observedAt, Doc: doc);

    public static WebConsoleSection<T> Unavailable(string gapCode, string reason, string? doc = null)
        => new(false, default, gapCode, reason, doc);
}

/// <summary>Registered host API gap surfaced to the UI (see docs/WEBCONSOLE_HOST_API_GAP.md).</summary>
public sealed record WebConsoleGap(
    string Code,
    string Area,
    string Summary,
    string Doc,
    string HostChange);

public static class WebConsoleGaps
{
    private const string DocFile = "docs/WEBCONSOLE_HOST_API_GAP.md";

    public static WebConsoleGap AdminDiagnostics { get; } = new(
        "host.admin_diagnostics.unavailable",
        "diagnostics",
        "宿主未提供 IAdminDiagnostics 聚合快照，控制台只使用 /health 与插件运行视图。",
        DocFile + "#2-g3g4诊断快照与插件运行视图",
        "实现 IAdminDiagnostics/AdminSnapshot 并注入插件 provider。");

    public static WebConsoleGap PluginRuntimeDiagnostics { get; } = new(
        "host.plugin_diagnostics.unavailable",
        "plugins",
        "IPluginRuntimeDiagnostics 已存在于宿主 DI，但未注入插件 provider；plugins 明细不可用。",
        DocFile + "#23-g4-的最小宿主改动建议一行桥接",
        "在 NpsPluginRuntime.BuildPluginProvider 中桥接 IPluginRuntimeDiagnostics。");

    public static WebConsoleGap AdminConfig { get; } = new(
        "host.admin_config.unavailable",
        "config",
        "宿主未提供 IAdminConfigStore，配置编辑不可用。",
        DocFile + "#4-g5配置编辑",
        "实现 IAdminConfigStore（id 白名单、ETag、备份、写锁）。");

    public static WebConsoleGap AdminCommand { get; } = new(
        "host.admin_command.unavailable",
        "commands",
        "宿主未提供 IAdminCommandRunner，受限命令终端不可用。",
        DocFile + "#5-g6受限命令终端",
        "实现 IAdminCommandRunner 并增加 admin.command.execute 权限词。");

    public static WebConsoleGap HostLogs { get; } = new(
        "host.logs.unavailable",
        "logs",
        "宿主没有日志读取端口（IPluginLog 只写），/logs 改用插件审计流水。",
        DocFile + "#7-g8g9日志与脱敏",
        "提供带硬上限的 IAdminLogReader。");

    public static WebConsoleGap AdminKind { get; } = new(
        "host.admin_kind.unavailable",
        "manifest",
        "NPS v1 没有 admin kind 与 admin.ui/admin.api contribution，控制台只能由 ActivateAsync 启动。",
        DocFile + "#3-g1g2manifest-与管理面贡献点",
        "新增 PluginKind.Admin 与 admin.* contribution/权限词。");

    public static WebConsoleGap AspNetCoreHosting { get; } = new(
        "host.aspnetcore.unavailable",
        "transport",
        "宿主 runtimeconfig/ALC 未共享 Microsoft.AspNetCore.App，Phase 1 使用 HttpListener。",
        DocFile + "#6-g7aspnet-core--alc-实测结论",
        "为宿主引入 ASP.NET Core 共享框架并扩展 PluginContractAssemblies。");

    public static IReadOnlyList<WebConsoleGap> All { get; } =
    [
        AdminDiagnostics,
        PluginRuntimeDiagnostics,
        AdminConfig,
        AdminCommand,
        HostLogs,
        AdminKind,
        AspNetCoreHosting,
    ];
}

public sealed record WebConsoleTasks(long Created, long Completed, long Failed, long Cancelled, long Active);

public sealed record WebConsoleProvider(string Id, bool Available, string Profile, int SupportTier);

public sealed record WebConsoleProfile(string Profile, string Provider, bool Available, int SupportTier);

public sealed record WebConsoleUpstream(
    string Source,
    bool Connected,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectAt,
    int ReconnectCount,
    string? LastError,
    int SendFailures,
    string? LastSendError,
    string? LastDisconnectError = null,
    string? LastDisconnectCode = null);

public sealed record WebConsolePluginFailure(string Id, string State, string Phase, string Code, string Message);

public sealed record WebConsolePluginNode(
    string Id,
    string Version,
    string Kind,
    string State,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> BlockedBy);

public sealed record WebConsolePluginContribution(string Type, string Id, string PluginId, int Priority, string Status);

public sealed record WebConsolePluginPermission(
    string PluginId,
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Granted,
    IReadOnlyList<string> Denied);

public sealed record WebConsolePlugins(
    int Activated,
    int Failed,
    IReadOnlyList<WebConsolePluginFailure> Failures,
    IReadOnlyList<WebConsolePluginNode> Nodes,
    IReadOnlyList<WebConsolePluginContribution> Contributions,
    IReadOnlyList<WebConsolePluginPermission> Permissions,
    string Source);

public sealed record WebConsoleHealth(
    string Status,
    DateTimeOffset SampledAt,
    long UptimeSeconds,
    DateTimeOffset? StartedAt,
    int Profiles,
    int Conversations,
    WebConsoleTasks Tasks,
    IReadOnlyList<WebConsoleProvider> Providers,
    int ActivatedPlugins,
    int FailedPlugins,
    IReadOnlyList<WebConsolePluginFailure> PluginFailures);

public sealed record WebConsoleHostSnapshot(
    WebConsoleSection<WebConsoleHealth> Health,
    WebConsoleSection<WebConsolePlugins> Plugins,
    WebConsoleSection<IReadOnlyList<WebConsoleUpstream>> Upstreams,
    WebConsoleSection<IReadOnlyList<WebConsoleProfile>> Profiles,
    IReadOnlyList<WebConsoleGap> Gaps);

/// <summary>Read-only host view port. Implementations must never fabricate data.</summary>
public interface IWebConsoleHostPort
{
    ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Port for tests and for hosts that expose nothing.</summary>
public sealed class UnavailableWebConsoleHostPort : IWebConsoleHostPort
{
    public static UnavailableWebConsoleHostPort Instance { get; } = new();

    public ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new WebConsoleHostSnapshot(
            WebConsoleSection<WebConsoleHealth>.Unavailable(
                "host.health.unavailable",
                "未配置宿主 health endpoint。",
                WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleSection<WebConsolePlugins>.Unavailable(
                WebConsoleGaps.PluginRuntimeDiagnostics.Code,
                WebConsoleGaps.PluginRuntimeDiagnostics.Summary,
                WebConsoleGaps.PluginRuntimeDiagnostics.Doc),
            WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Unavailable(
                "host.health.unavailable",
                "未配置宿主 health endpoint。",
                WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Unavailable(
                "host.health.unavailable",
                "未配置宿主 health endpoint。",
                WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleGaps.All));
}

/// <summary>
/// Reads the existing <c>natter serve --health-port</c> endpoint. The endpoint is loopback-only,
/// unauthenticated and documented, so the plugin bounds timeout, size and cache lifetime.
/// </summary>
public sealed class HealthEndpointHostPort : IWebConsoleHostPort, IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private const int MaxResponseBytes = 256 * 1024;
    private const string HealthDoc = "docs/P2_RUNTIME_PROFILE.md";

    private readonly Uri _healthUri;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebConsoleHostSnapshot? _cached;
    private DateTimeOffset _cachedAt;

    public HealthEndpointHostPort(Uri healthUri, HttpClient? client, TimeProvider clock)
    {
        _healthUri = healthUri ?? throw new ArgumentNullException(nameof(healthUri));
        _client = client ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        _ownsClient = client is null;
        _client.Timeout = RequestTimeout;
        _clock = clock;
    }

    public async ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && _clock.GetUtcNow() - _cachedAt < CacheLifetime)
            return _cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _clock.GetUtcNow() - _cachedAt < CacheLifetime)
                return _cached;

            var snapshot = await FetchAsync(cancellationToken).ConfigureAwait(false);
            _cached = snapshot;
            _cachedAt = _clock.GetUtcNow();
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<WebConsoleHostSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _healthUri);
            request.Headers.Accept.ParseAdd("application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"宿主 /health 返回 HTTP {(int)response.StatusCode}。");
            }

            var body = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var sampledAt = ReadDateTime(root, "sampledAt") ?? _clock.GetUtcNow();
            var health = MapHealth(root, sampledAt);
            var upstreams = MapUpstreams(root);
            var profiles = health.Providers
                .Select(provider => new WebConsoleProfile(
                    provider.Profile,
                    provider.Id,
                    provider.Available,
                    provider.SupportTier))
                .ToArray();

            var plugins = new WebConsolePlugins(
                health.ActivatedPlugins,
                health.FailedPlugins,
                health.PluginFailures,
                Nodes: [],
                Contributions: [],
                Permissions: [],
                Source: "health-endpoint");

            return new WebConsoleHostSnapshot(
                WebConsoleSection<WebConsoleHealth>.Ok(health, sampledAt, HealthDoc),
                WebConsoleSection<WebConsolePlugins>.Ok(plugins, sampledAt, HealthDoc),
                WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Ok(upstreams, sampledAt, HealthDoc),
                WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Ok(profiles, sampledAt, HealthDoc),
                WebConsoleGaps.All);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable("宿主 /health 读取超时（2s）。");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return Unavailable($"宿主 /health 不可用：{exception.GetType().Name}。");
        }

        WebConsoleHostSnapshot Unavailable(string reason)
            => new(
                WebConsoleSection<WebConsoleHealth>.Unavailable("host.health.unavailable", reason, HealthDoc),
                WebConsoleSection<WebConsolePlugins>.Unavailable(
                    WebConsoleGaps.PluginRuntimeDiagnostics.Code,
                    "宿主 /health 不可用，plugins 计数也不可用。",
                    WebConsoleGaps.PluginRuntimeDiagnostics.Doc),
                WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Unavailable(
                    "host.health.unavailable", reason, HealthDoc),
                WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Unavailable(
                    "host.health.unavailable", reason, HealthDoc),
                WebConsoleGaps.All);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new JsonException("Host health response exceeded 256 KiB.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static WebConsoleHealth MapHealth(JsonElement root, DateTimeOffset sampledAt)
    {
        var providers = new List<WebConsoleProvider>();
        if (root.TryGetProperty("providers", out var providerArray) && providerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var provider in providerArray.EnumerateArray())
            {
                providers.Add(new WebConsoleProvider(
                    ReadString(provider, "id") ?? "unknown",
                    ReadBool(provider, "available"),
                    ReadString(provider, "profile") ?? "default",
                    ReadInt32(provider, "supportTier")));
            }
        }

        var failures = new List<WebConsolePluginFailure>();
        var activated = 0;
        var failed = 0;

        if (root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Object)
        {
            activated = ReadInt32(plugins, "activated");
            failed = ReadInt32(plugins, "failed");

            if (plugins.TryGetProperty("failures", out var failureArray) && failureArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var failure in failureArray.EnumerateArray())
                {
                    failures.Add(new WebConsolePluginFailure(
                        ReadString(failure, "id") ?? "unknown",
                        ReadString(failure, "state") ?? "Failed",
                        ReadString(failure, "phase") ?? "unknown",
                        ReadString(failure, "code") ?? "unknown",
                        ReadString(failure, "message") ?? string.Empty));
                }
            }
        }

        var tasks = new WebConsoleTasks(0, 0, 0, 0, 0);
        if (root.TryGetProperty("tasks", out var taskElement) && taskElement.ValueKind == JsonValueKind.Object)
        {
            tasks = new WebConsoleTasks(
                ReadInt64(taskElement, "created"),
                ReadInt64(taskElement, "completed"),
                ReadInt64(taskElement, "failed"),
                ReadInt64(taskElement, "cancelled"),
                ReadInt64(taskElement, "active"));
        }

        return new WebConsoleHealth(
            ReadString(root, "status") ?? "unknown",
            sampledAt,
            ReadInt64(root, "uptimeSeconds"),
            ReadDateTime(root, "startedAt"),
            ReadInt32(root, "profiles"),
            ReadInt32(root, "conversations"),
            tasks,
            providers,
            activated,
            failed,
            failures);
    }

    private static IReadOnlyList<WebConsoleUpstream> MapUpstreams(JsonElement root)
    {
        var upstreams = new List<WebConsoleUpstream>();
        if (!root.TryGetProperty("upstreams", out var array) || array.ValueKind != JsonValueKind.Array)
            return upstreams;

        foreach (var upstream in array.EnumerateArray())
        {
            upstreams.Add(new WebConsoleUpstream(
                ReadString(upstream, "source") ?? "unknown",
                ReadBool(upstream, "connected"),
                ReadDateTime(upstream, "lastEventAt"),
                ReadDateTime(upstream, "lastConnectedAt"),
                ReadDateTime(upstream, "lastDisconnectAt"),
                ReadInt32(upstream, "reconnectCount"),
                ReadRedacted(upstream, "lastError"),
                ReadInt32(upstream, "sendFailures"),
                ReadRedacted(upstream, "lastSendError"),
                ReadRedacted(upstream, "lastDisconnectError"),
                ReadString(upstream, "lastDisconnectCode")));
        }

        return upstreams;
    }

    private static string? ReadRedacted(JsonElement element, string name)
    {
        var value = ReadString(element, name);
        return value is null ? null : WebConsoleRedaction.RedactSecrets(value);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static long ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : 0;

    private static DateTimeOffset? ReadDateTime(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(
               value.GetString(),
               CultureInfo.InvariantCulture,
               DateTimeStyles.RoundtripKind,
               out var parsed)
            ? parsed
            : null;

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();

        _gate.Dispose();
    }
}

/// <summary>
/// Optional richer view through <see cref="IPluginRuntimeDiagnostics"/>. The host currently does not
/// inject it into the plugin provider, so this port usually reports the documented gap (G4).
/// </summary>
public sealed class PluginDiagnosticsHostPort : IWebConsoleHostPort
{
    private readonly IServiceProvider _services;

    public PluginDiagnosticsHostPort(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        IPluginRuntimeDiagnostics? diagnostics;
        try
        {
            diagnostics = _services.GetService(typeof(IPluginRuntimeDiagnostics)) as IPluginRuntimeDiagnostics;
        }
        catch (Exception)
        {
            diagnostics = null;
        }

        if (diagnostics is null)
        {
            return ValueTask.FromResult(new WebConsoleHostSnapshot(
                WebConsoleSection<WebConsoleHealth>.Unavailable(
                    "host.health.unavailable", "不提供 health。", WebConsoleGaps.AdminDiagnostics.Doc),
                WebConsoleSection<WebConsolePlugins>.Unavailable(
                    WebConsoleGaps.PluginRuntimeDiagnostics.Code,
                    WebConsoleGaps.PluginRuntimeDiagnostics.Summary,
                    WebConsoleGaps.PluginRuntimeDiagnostics.Doc),
                WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Unavailable(
                    "host.health.unavailable", "不提供 upstreams。", WebConsoleGaps.AdminDiagnostics.Doc),
                WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Unavailable(
                    "host.health.unavailable", "不提供 profiles。", WebConsoleGaps.AdminDiagnostics.Doc),
                WebConsoleGaps.All));
        }

        var nodes = diagnostics.Graph
            .Select(node => new WebConsolePluginNode(
                node.Id,
                node.Version,
                node.Kind,
                node.State.ToString(),
                node.DependsOn,
                node.BlockedBy))
            .ToArray();

        var failures = new List<WebConsolePluginFailure>();
        foreach (var node in diagnostics.Graph.Where(node => node.State == PluginState.Failed))
        {
            try
            {
                var doctor = diagnostics.GetDoctor(node.Id);
                failures.Add(new WebConsolePluginFailure(
                    node.Id,
                    node.State.ToString(),
                    doctor.Error?.Phase ?? "unknown",
                    doctor.Error?.Code ?? "unknown",
                    WebConsoleRedaction.SanitizeText(doctor.Error?.Message ?? "plugin failed", 300)));
            }
            catch (Exception)
            {
                failures.Add(new WebConsolePluginFailure(node.Id, node.State.ToString(), "unknown", "doctor-failed", "插件诊断不可用。"));
            }
        }

        var plugins = new WebConsolePlugins(
            nodes.Count(node => string.Equals(node.State, nameof(PluginState.Activated), StringComparison.Ordinal)),
            nodes.Count(node => string.Equals(node.State, nameof(PluginState.Failed), StringComparison.Ordinal)),
            failures,
            nodes,
            diagnostics.Contributions
                .Select(item => new WebConsolePluginContribution(
                    item.Type, item.Id, item.PluginId, item.Priority, item.Status))
                .ToArray(),
            diagnostics.Permissions
                .Select(item => new WebConsolePluginPermission(
                    item.PluginId, item.Requested, item.Granted, item.Denied))
                .ToArray(),
            Source: "plugin-runtime");

        return ValueTask.FromResult(new WebConsoleHostSnapshot(
            WebConsoleSection<WebConsoleHealth>.Unavailable(
                "host.health.unavailable", "不提供 health。", WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleSection<WebConsolePlugins>.Ok(plugins),
            WebConsoleSection<IReadOnlyList<WebConsoleUpstream>>.Unavailable(
                "host.health.unavailable", "不提供 upstreams。", WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleSection<IReadOnlyList<WebConsoleProfile>>.Unavailable(
                "host.health.unavailable", "不提供 profiles。", WebConsoleGaps.AdminDiagnostics.Doc),
            WebConsoleGaps.All));
    }
}

/// <summary>Merges host ports section-by-section; the first available section wins.</summary>
public sealed class CompositeWebConsoleHostPort : IWebConsoleHostPort
{
    private readonly IReadOnlyList<IWebConsoleHostPort> _ports;

    public CompositeWebConsoleHostPort(params IWebConsoleHostPort[] ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    public async ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<WebConsoleHostSnapshot>(_ports.Count);
        foreach (var port in _ports)
        {
            try
            {
                snapshots.Add(await port.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                snapshots.Add(await UnavailableWebConsoleHostPort.Instance.ReadAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        var health = snapshots.Select(item => item.Health).FirstOrDefault(item => item.Available)
                     ?? snapshots[0].Health;
        var upstreams = snapshots.Select(item => item.Upstreams).FirstOrDefault(item => item.Available)
                        ?? snapshots[0].Upstreams;
        var profiles = snapshots.Select(item => item.Profiles).FirstOrDefault(item => item.Available)
                       ?? snapshots[0].Profiles;

        var pluginSection = snapshots
                                .Select(item => item.Plugins)
                                .Where(item => item.Available)
                                .OrderByDescending(item => string.Equals(
                                    item.Data?.Source,
                                    "plugin-runtime",
                                    StringComparison.Ordinal))
                                .FirstOrDefault()
                            ?? snapshots[0].Plugins;

        if (pluginSection.Available
            && health.Data is { } healthData
            && pluginSection.Data is { Source: "plugin-runtime" } runtimePlugins)
        {
            pluginSection = WebConsoleSection<WebConsolePlugins>.Ok(
                runtimePlugins with
                {
                    Activated = runtimePlugins.Activated > 0 ? runtimePlugins.Activated : healthData.ActivatedPlugins,
                    Failed = runtimePlugins.Failed > 0 ? runtimePlugins.Failed : healthData.FailedPlugins,
                },
                pluginSection.ObservedAt,
                pluginSection.Doc);
        }

        var gaps = snapshots
            .SelectMany(item => item.Gaps)
            .DistinctBy(gap => gap.Code)
            .ToArray();

        return new WebConsoleHostSnapshot(health, pluginSection, upstreams, profiles, gaps);
    }
}
