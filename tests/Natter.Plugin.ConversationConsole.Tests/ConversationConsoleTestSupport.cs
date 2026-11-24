using Natter.Application.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.ConversationConsole.Tests;

internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class RecordingPluginLog : IPluginLog
{
    public List<string> Messages { get; } = [];

    public void Debug(string message, params object?[] args) => Messages.Add("debug: " + message);

    public void Information(string message, params object?[] args) => Messages.Add("info: " + message);

    public void Warning(string message, params object?[] args) => Messages.Add("warn: " + message);

    public void Error(Exception? exception, string message, params object?[] args)
        => Messages.Add("error: " + message + " " + exception?.Message);
}

internal sealed class FakeSecrets(params string[] names) : IPluginSecrets
{
    private readonly Dictionary<string, string> _values = names
        .Select((name, index) => (name, value: "test-token-" + new string((char)('a' + index), 24)))
        .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal);

    public FakeSecrets With(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public string GetRequired(string name)
        => _values.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Secret '{name}' is not configured.");

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>A token of the required minimum length, for authenticator tests.</summary>
    public string Token => _values[ConversationConsoleDefaults.TokenSecretName];
}

internal sealed class FakeServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public FakeServiceProvider Add<TService>(TService service)
        where TService : class
    {
        _services[typeof(TService)] = service;
        return this;
    }

    public object? GetService(Type serviceType)
        => _services.TryGetValue(serviceType, out var service) ? service : null;
}

/// <summary>Query port double that records what the console asked for.</summary>
internal sealed class FakeConversationQuery : IConversationQuery
{
    public List<ConversationFilter> Filters { get; } = [];

    public List<(string SessionId, long After, int Limit)> MessageCalls { get; } = [];

    public List<ConversationOverview> Sessions { get; } = [];

    public Dictionary<string, List<StoredMessage>> Messages { get; } = new(StringComparer.Ordinal);

    public string? NextCursor { get; set; }

    public ValueTask<ConversationPage> ListAsync(
        ConversationFilter filter,
        CancellationToken cancellationToken = default)
    {
        Filters.Add(filter);

        var limit = ConversationQueryLimits.ClampPageSize(filter.Limit);
        return ValueTask.FromResult(new ConversationPage(Sessions.Take(limit).ToArray(), NextCursor));
    }

    public ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        MessageCalls.Add((sessionId, afterMessageId, limit));

        var capped = ConversationQueryLimits.ClampMessagePageSize(limit);
        var rows = Messages.TryGetValue(sessionId, out var stored) ? stored : [];
        return ValueTask.FromResult<IReadOnlyList<StoredMessage>>(
            rows.Where(message => message.MessageId > afterMessageId).Take(capped).ToArray());
    }

    public FakeConversationQuery WithSession(string sessionId, string sourceKey, int messageCount = 2, string? actorId = null)
    {
        var now = DateTimeOffset.UtcNow;
        Sessions.Add(new ConversationOverview(
            sessionId,
            sourceKey,
            "discord",
            "discord",
            sourceKey.Split(':').Last(),
            GuildId: null,
            actorId,
            messageCount,
            now.AddMinutes(-5),
            now));
        return this;
    }

    public FakeConversationQuery WithMessages(string sessionId, params string[] texts)
    {
        var list = new List<StoredMessage>();
        long id = 0;
        foreach (var text in texts)
        {
            id++;
            list.Add(new StoredMessage(id % 2 == 1 ? "user" : "assistant", text, null, DateTimeOffset.UtcNow, id));
        }

        Messages[sessionId] = list;
        return this;
    }
}

/// <summary>Temp state/plugin layout, so audit-path assertions never touch the real filesystem layout.</summary>
internal sealed class ConversationConsoleWorkspace : IDisposable
{
    public ConversationConsoleWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "natter-console-" + Guid.NewGuid().ToString("N"));
        StateDir = Path.Combine(Root, "state");
        PluginDir = Path.Combine(Root, "plugin");
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(PluginDir);

        var package = Path.Combine(AppContext.BaseDirectory, "plugin-package");
        if (Directory.Exists(package))
        {
            foreach (var file in Directory.GetFiles(package, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(package, file);
                var target = Path.Combine(PluginDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
    }

    public string Root { get; }

    public string StateDir { get; }

    public string PluginDir { get; }

    public string AuditFile => Path.Combine(
        StateDir,
        ConversationConsoleDefaults.StateDirectoryName,
        ConversationConsoleDefaults.AuditDirectoryName,
        ConversationConsoleDefaults.AuditFileName);

    public IReadOnlyList<ConversationConsoleAuditEntry> ReadAudit()
        => File.Exists(AuditFile)
            ? new ConversationConsoleAuditStore(
                    Path.GetDirectoryName(AuditFile)!,
                    TimeProvider.System,
                    ConversationConsoleDefaults.AuditRetentionFiles)
                .Read(1000)
            : [];

    public string ReadAuditText() => File.Exists(AuditFile) ? File.ReadAllText(AuditFile) : string.Empty;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Builds the handler the runtime would build, with explicit doubles.</summary>
internal static class ConversationConsoleTestFactory
{
    public const string ValidToken = "console-token-0123456789abcdef";

    public static ConversationConsoleOptions Options(Action<ConversationConsoleOptions>? configure = null)
    {
        var options = new ConversationConsoleOptions();
        configure?.Invoke(options);
        return options;
    }

    public static ConversationConsoleHttpRequest Request(
        string path,
        string method = "GET",
        string? token = ValidToken,
        string query = "",
        string remote = "127.0.0.1",
        string? origin = null,
        string? fetchSite = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (token is not null)
            headers[ConversationConsoleDefaults.AuthorizationHeader] = "Bearer " + token;
        if (origin is not null)
            headers["Origin"] = origin;
        if (fetchSite is not null)
            headers["Sec-Fetch-Site"] = fetchSite;

        return new ConversationConsoleHttpRequest(method, path, query, headers, string.Empty, remote);
    }

    public static (ConversationConsoleRequestHandler Handler, ConversationConsoleAuditStore Audit) CreateHandler(
        ConversationConsoleWorkspace workspace,
        ConversationConsoleOptions? options = null,
        IConversationQuery? query = null,
        TimeProvider? clock = null)
    {
        options ??= Options();
        var time = clock ?? TimeProvider.System;
        var audit = new ConversationConsoleAuditStore(
            Path.Combine(
                workspace.StateDir,
                ConversationConsoleDefaults.StateDirectoryName,
                ConversationConsoleDefaults.AuditDirectoryName),
            time,
            options.AuditRetentionFiles);

        var handler = new ConversationConsoleRequestHandler(
            options,
            new ConversationConsoleTokenAuthenticator(ValidToken),
            new ConversationConsoleQueryGateway(query),
            audit,
            ConversationConsoleStaticAssets.Load(ConversationConsoleRuntime.ResolveStaticRoot(workspace.PluginDir)),
            time,
            new RecordingPluginLog());

        return (handler, audit);
    }
}
