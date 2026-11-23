using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;
using Natter.Plugin.WebConsole.Tests.Phase23;

namespace Natter.Plugin.WebConsole.Tests;

internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class NullPluginLog : IPluginLog
{
    public List<string> Messages { get; } = [];

    public void Debug(string message, params object?[] args) => Messages.Add("debug: " + message);

    public void Information(string message, params object?[] args) => Messages.Add("info: " + message);

    public void Warning(string message, params object?[] args) => Messages.Add("warn: " + message);

    public void Error(Exception? exception, string message, params object?[] args)
        => Messages.Add("error: " + message + " " + exception?.Message);
}

internal sealed class FakeServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public FakeServiceProvider Add<TService>(TService service) where TService : class
    {
        _services[typeof(TService)] = service;
        return this;
    }

    public object? GetService(Type serviceType)
        => _services.TryGetValue(serviceType, out var service) ? service : null;
}

internal sealed class FakeRuntimeDiagnostics : IPluginRuntimeDiagnostics
{
    public IReadOnlyList<PluginGraphNode> Graph { get; init; } = [];

    public IReadOnlyList<PluginGraphEdge> GraphEdges { get; init; } = [];

    public IReadOnlyList<PluginContributionInfo> Contributions { get; init; } = [];

    public IReadOnlyList<PluginPermissionInfo> Permissions { get; init; } = [];

    public PluginDoctorReport GetDoctor(string pluginId)
        => new(
            pluginId,
            Manifest: null,
            PluginState.Failed,
            HostApi: "0.1.0",
            HostApiCompatible: true,
            ConfigPath: null,
            ConfigValid: true,
            ConfigIssues: [],
            MissingSecrets: [],
            Dependencies: [],
            Warnings: [],
            Error: new PluginErrorDescriptor("activate", "boom", "activation failed"));

    public PluginConfigValidationResult ValidateConfig(string pluginId)
        => new(pluginId, true, []);
}

internal sealed class FakePluginSecrets(string token) : IPluginSecrets
{
    public string GetRequired(string name)
        => name == WebConsoleDefaults.TokenSecretName
            ? token
            : throw new PluginSecretException($"Unexpected secret '{name}'.");

    public string? Get(string name)
        => name == WebConsoleDefaults.TokenSecretName ? token : null;
}

internal sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException("connection refused");
}

/// <summary>
/// Starts a real <see cref="WebConsoleServer"/> on a loopback port. The port is reserved, released and
/// then bound, which is a TOCTOU window (S2-F4); a collision is retried with a fresh port instead of
/// failing the run. Collections are also serialized via <c>xunit.runner.json</c>.
/// </summary>
internal sealed class ServerUnderTest : IAsyncDisposable
{
    private const int MaxAttempts = 6;

    private ServerUnderTest(WebConsoleHarness harness, WebConsoleServer server, int port)
    {
        Harness = harness;
        Server = server;
        Port = port;
    }

    public WebConsoleHarness Harness { get; }

    public WebConsoleServer Server { get; }

    public int Port { get; }

    public WebConsoleOptions Options => Harness.Options;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static async Task<ServerUnderTest> StartAsync(Func<int, WebConsoleOptions> optionsFactory)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = LoopbackPortPool.Reserve();
            var harness = WebConsoleHarness.Create(optionsFactory(port));
            var server = new WebConsoleServer(harness.Options, harness.Handler, harness.Status, new NullPluginLog());

            try
            {
                await server.StartAsync();
                return new ServerUnderTest(harness, server, port);
            }
            catch (WebConsoleStartupException exception)
                when (exception.Code == "webconsole.port-in-use" && attempt < MaxAttempts)
            {
                await server.DisposeAsync();
                harness.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.StopAsync();
            await Server.DisposeAsync();
        }
        catch (Exception)
        {
            // Best effort: the listener may already be gone.
        }

        Harness.Dispose();
    }
}

/// <summary>
/// Binds an ephemeral loopback port, then releases it. Callers must tolerate a collision: see
/// <see cref="ServerUnderTest.StartAsync"/> (S2-F4).
/// </summary>
internal static class LoopbackPortPool
{
    public static int Reserve()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>In-memory task inspection for handler tests (task-65).</summary>
internal sealed class FakeTaskInspection : Natter.Application.Chat.IAgentTaskInspection
{
    public List<Natter.Application.Chat.AgentTaskSummary> Recent { get; } = [];

    public Dictionary<string, Natter.Application.Chat.AgentTaskTrace> Traces { get; } = new(StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<Natter.Application.Chat.AgentTaskSummary>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<Natter.Application.Chat.AgentTaskSummary>>(Recent.Take(limit).ToArray());

    public ValueTask<Natter.Application.Chat.AgentTaskTrace?> GetTraceAsync(
        string taskId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Traces.TryGetValue(taskId, out var trace) ? trace : null);
}

internal sealed class WebConsoleHarness : IDisposable
{
    public const string Token = "test-token-0123456789abcdef";

    public const string ViewerToken = "test-viewer-token-0123456789abcdef";

    private WebConsoleHarness(
        WebConsoleOptions options,
        IWebConsoleHostPort hostPort,
        JsonElement config,
        string directory,
        FakeAdminConfigStore? adminConfig,
        FakeAdminCommandRunner? adminCommand,
        FakeAdminLogReader? logReader,
        Natter.Application.Admin.IAdminRedactor? redactor,
        Natter.Application.Chat.IAgentTaskInspection? tasks)
    {
        Options = options;
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero));
        Status = new WebConsoleServerStatusHolder();
        Status.Set(WebConsoleServerStatus.Listening($"http://127.0.0.1:{options.Port}"));
        Directory = directory;
        Audit = new WebConsoleAuditStore(Path.Combine(directory, "audit"), Clock);

        var identity = new WebConsoleIdentity(
            "natter.tool.admin.webconsole",
            "Natter WebConsole",
            "0.1.0",
            "tool",
            "0.1.0",
            "Natter.Plugin.WebConsole",
            directory,
            Path.Combine(directory, "plugin.json"));

        var preview = WebConsoleConfig.Build(config, "test", null, options);
        var tokens = new WebConsoleTokenAuthenticator(Token);
        var viewerTokens = string.IsNullOrEmpty(options.ViewerToken)
            ? null
            : new WebConsoleTokenAuthenticator(WebConsoleHarness.ViewerToken);
        var assets = WebConsoleStaticAssets.Load(Path.Combine(directory, "wwwroot"));

        AdminConfig = adminConfig;
        AdminCommand = adminCommand;
        LogReader = logReader;
        HostRedactor = redactor;
        var surface = new WebConsoleAdminSurface(adminConfig, adminCommand, logReader, redactor, tasks);
        Admin = new WebConsoleAdminGateway(surface, options, Clock);

        Diagnostics = new WebConsoleDiagnostics(
            identity,
            options,
            preview,
            hostPort,
            Admin,
            Status,
            Audit,
            tokens,
            assets,
            viewerTokens is { IsUsable: true },
            Clock);

        Handler = new WebConsoleRequestHandler(
            options,
            tokens,
            viewerTokens,
            new WebConsoleSessionStore(Clock),
            new WebConsoleLoginThrottle(Clock),
            new WebConsoleOriginPolicy(options.PublicBaseUrl, options.Port),
            Diagnostics,
            Admin,
            Audit,
            assets,
            Clock,
            new NullPluginLog());
    }

    public WebConsoleOptions Options { get; }

    public FakeTimeProvider Clock { get; }

    public WebConsoleServerStatusHolder Status { get; }

    public WebConsoleAuditStore Audit { get; }

    public WebConsoleDiagnostics Diagnostics { get; }

    public WebConsoleAdminGateway Admin { get; }

    public FakeAdminConfigStore? AdminConfig { get; }

    public FakeAdminCommandRunner? AdminCommand { get; }

    public FakeAdminLogReader? LogReader { get; }

    /// <summary>Host redactor handed to the surface (null = plugin-local rules only).</summary>
    public Natter.Application.Admin.IAdminRedactor? HostRedactor { get; }

    /// <summary>Absolute path of the JSONL audit file used by this harness.</summary>
    public string AuditFilePath => Audit.FilePath;

    public WebConsoleRequestHandler Handler { get; }

    public string Directory { get; }

    public static WebConsoleHarness Create(
        WebConsoleOptions? options = null,
        IWebConsoleHostPort? hostPort = null,
        JsonElement? config = null,
        FakeAdminConfigStore? adminConfig = null,
        FakeAdminCommandRunner? adminCommand = null,
        FakeAdminLogReader? logReader = null,
        Natter.Application.Admin.IAdminRedactor? redactor = null,
        string? wwwrootSource = null,
        Natter.Application.Chat.IAgentTaskInspection? tasks = null)
    {
        options ??= new WebConsoleOptions { Port = 5090 };
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-tests", Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(directory, "wwwroot");
        System.IO.Directory.CreateDirectory(wwwroot);
        if (string.IsNullOrWhiteSpace(wwwrootSource))
        {
            File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><html><body><script src=\"/app.js\"></script></body></html>");
            File.WriteAllText(Path.Combine(wwwroot, "app.js"), "\"use strict\";");
            File.WriteAllText(Path.Combine(wwwroot, "app.css"), "body { color: #fff; }");
        }
        else
        {
            CopyDirectory(wwwrootSource!, wwwroot);
        }

        var defaultConfig = config ?? JsonSerializer.SerializeToElement(new
        {
            port = options.Port,
            token = WebConsoleDefaults.TokenReference,
            nested = new { password = "hunter2", apiKey = "sk-abcdefghijklmnop" },
            name = "console",
        });

        return new WebConsoleHarness(
            options,
            hostPort ?? UnavailableWebConsoleHostPort.Instance,
            defaultConfig,
            directory,
            adminConfig,
            adminCommand,
            logReader,
            redactor,
            tasks);
    }

    /// <summary>Copies the real <c>wwwroot</c> (modules included) into the harness plugin directory.</summary>
    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public WebConsoleHttpRequest Request(
        string method,
        string path,
        string? body = null,
        string? cookie = null,
        string? csrf = null,
        string? origin = null,
        string ip = "127.0.0.1",
        string query = "",
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cookie is not null)
            headers["Cookie"] = cookie;
        if (csrf is not null)
            headers[WebConsoleDefaults.CsrfHeaderName] = csrf;
        if (origin is not null)
            headers["Origin"] = origin;

        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
                headers[header.Key] = header.Value;
        }

        return new WebConsoleHttpRequest(method, path, query, headers, body ?? string.Empty, ip, false);
    }

    /// <summary>Logs in with the configured viewer secret (requires <c>viewerToken</c> in options).</summary>
    public Task<LoginResult> LoginViewerAsync(IReadOnlyDictionary<string, string>? extraHeaders = null)
        => LoginAsync(ViewerToken, extraHeaders);

    public async Task<LoginResult> LoginAsync(
        string? token = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        string ip = "127.0.0.1")
    {
        var response = await Handler.HandleAsync(Request(
            "POST",
            "/api/session",
            JsonSerializer.Serialize(new { token = token ?? Token }),
            ip: ip,
            extraHeaders: extraHeaders));

        var cookie = response.Headers.TryGetValue("Set-Cookie", out var setCookie)
            ? setCookie.Split(';')[0]
            : null;

        string? csrf = null;
        if (response.StatusCode == 200)
        {
            using var document = JsonDocument.Parse(response.Body);
            csrf = document.RootElement.GetProperty("csrf").GetString();
        }

        return new LoginResult(response, cookie, csrf);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record LoginResult(WebConsoleHttpResponse Response, string? Cookie, string? Csrf)
{
    public static JsonElement Json(WebConsoleHttpResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.Clone();
    }
}
