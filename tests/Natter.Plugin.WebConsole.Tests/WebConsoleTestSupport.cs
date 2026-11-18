using System.Text;
using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

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

internal sealed class WebConsoleHarness : IDisposable
{
    public const string Token = "test-token-0123456789abcdef";

    private WebConsoleHarness(
        WebConsoleOptions options,
        IWebConsoleHostPort hostPort,
        JsonElement config,
        string directory)
    {
        Options = options;
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero));
        Status = new WebConsoleServerStatusHolder();
        Status.Set(WebConsoleServerStatus.Listening($"http://127.0.0.1:{options.Port}"));
        Directory = directory;
        Audit = new WebConsoleAuditStore(Path.Combine(directory, "audit"), Clock);

        var identity = new WebConsoleIdentity(
            "natter.plugin.webconsole",
            "Natter WebConsole",
            "0.1.0",
            "tool",
            "0.1.0",
            "Natter.Plugin.WebConsole",
            directory,
            Path.Combine(directory, "plugin.json"));

        var preview = WebConsoleConfig.Build(config, "test", null, options);
        var tokens = new WebConsoleTokenAuthenticator(Token);
        var assets = WebConsoleStaticAssets.Load(Path.Combine(directory, "wwwroot"));

        Diagnostics = new WebConsoleDiagnostics(
            identity,
            options,
            preview,
            hostPort,
            Status,
            Audit,
            tokens,
            assets,
            Clock);

        Handler = new WebConsoleRequestHandler(
            options,
            tokens,
            new WebConsoleSessionStore(Clock),
            new WebConsoleLoginThrottle(Clock),
            new WebConsoleOriginPolicy(options.PublicBaseUrl, options.Port),
            Diagnostics,
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

    public WebConsoleRequestHandler Handler { get; }

    public string Directory { get; }

    public static WebConsoleHarness Create(
        WebConsoleOptions? options = null,
        IWebConsoleHostPort? hostPort = null,
        JsonElement? config = null)
    {
        options ??= new WebConsoleOptions { Port = 5090 };
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-tests", Guid.NewGuid().ToString("N"));
        var wwwroot = Path.Combine(directory, "wwwroot");
        System.IO.Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><html><body><script src=\"/app.js\"></script></body></html>");
        File.WriteAllText(Path.Combine(wwwroot, "app.js"), "\"use strict\";");
        File.WriteAllText(Path.Combine(wwwroot, "app.css"), "body { color: #fff; }");

        var defaultConfig = config ?? JsonSerializer.SerializeToElement(new
        {
            port = options.Port,
            token = WebConsoleDefaults.TokenReference,
            nested = new { password = "hunter2", apiKey = "sk-abcdefghijklmnop" },
            name = "console",
        });

        return new WebConsoleHarness(options, hostPort ?? UnavailableWebConsoleHostPort.Instance, defaultConfig, directory);
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

    public async Task<LoginResult> LoginAsync(
        string? token = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var response = await Handler.HandleAsync(Request(
            "POST",
            "/api/session",
            JsonSerializer.Serialize(new { token = token ?? Token }),
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
