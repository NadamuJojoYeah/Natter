using Natter.Application.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// Owns the console lifecycle: token, audit store, static assets, the read-only query gateway and
/// the loopback listener. Kept separate from <see cref="ConversationConsolePlugin"/> so the whole
/// console can be started against a fake plugin provider in tests.
/// </summary>
public sealed class ConversationConsoleRuntime : IAsyncDisposable
{
    private readonly ConversationConsoleOptions _options;
    private readonly IPluginLog _log;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _stateDirectoryProvider;
    private readonly string _pluginDirectory;
    private readonly ConversationConsoleServerStatusHolder _status = new();

    private ConversationConsoleServer? _server;
    private ConversationConsoleAuditStore? _audit;

    public ConversationConsoleRuntime(
        ConversationConsoleOptions options,
        string pluginDirectory,
        IPluginLog log,
        TimeProvider clock,
        Func<string?>? stateDirectoryProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        _pluginDirectory = pluginDirectory;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _stateDirectoryProvider = stateDirectoryProvider ?? ConversationConsoleAuditPaths.ReadStateDirectoryFromEnvironment;
    }

    public ConversationConsoleOptions Options => _options;

    public ConversationConsoleServerStatus Status => _status.Current;

    public ConversationConsoleAuditLocation? AuditLocation { get; private set; }

    public string? AuditPath => _audit?.FilePath;

    /// <summary>True when the host granted <c>chat.read</c> and bridged <see cref="IConversationQuery"/>.</summary>
    public bool QueryAvailable { get; private set; }

    /// <summary>Where the conversation data came from, for diagnostics ("unavailable" when ungranted).</summary>
    public string QuerySource { get; private set; } = "unavailable";

    public string Url => ConversationConsoleNetwork.BuildUrl(_options.Bind, _options.Port);

    public async Task StartAsync(
        IPluginSecrets secrets,
        IServiceProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(provider);

        var issues = ConversationConsoleOptionsValidator.Validate(_options);
        if (issues.Count > 0)
        {
            throw new ConversationConsoleStartupException(
                issues[0].Code,
                string.Join("; ", issues.Select(issue => $"{issue.Pointer} {issue.Message}")));
        }

        var token = ResolveToken(secrets);
        var authenticator = new ConversationConsoleTokenAuthenticator(token);
        if (!authenticator.IsUsable)
        {
            throw new ConversationConsoleStartupException(
                authenticator.Problem ?? "conversation-console.token-invalid",
                $"Secret '{ConversationConsoleDefaults.TokenSecretName}' is not usable. Generate at least "
                + $"{ConversationConsoleDefaults.MinTokenLength} characters (for example: openssl rand -base64 48).");
        }

        // The audit trail is mandatory (design §4.2): no writable state root means no console.
        var auditLocation = ConversationConsoleAuditPaths.Resolve(_stateDirectoryProvider());
        AuditLocation = auditLocation;

        try
        {
            _audit = new ConversationConsoleAuditStore(
                auditLocation.Directory,
                _clock,
                _options.AuditRetentionFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.audit-unwritable",
                $"Audit directory '{auditLocation.Directory}' is not writable ({exception.GetType().Name}). "
                + "Point NATTER_STATE_DIR at a writable state root (systemd: /var/lib/natter, container: /state) "
                + "instead of widening the plugin mount.");
        }

        if (!_audit.CanWrite(out var auditError))
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.audit-unwritable",
                $"Audit file '{auditLocation.FilePath}' cannot be appended ({auditError}).");
        }

        var assets = ConversationConsoleStaticAssets.Load(ConversationConsoleRuntime.ResolveStaticRoot(_pluginDirectory));

        // The only data source is the host port. When the host did not grant chat.read the service
        // is absent from this provider and the console reports it instead of reaching for SQLite.
        var query = provider.GetService(typeof(IConversationQuery)) as IConversationQuery;
        var gateway = new ConversationConsoleQueryGateway(query);
        QueryAvailable = gateway.IsAvailable;
        QuerySource = gateway.Source;

        if (!QueryAvailable)
        {
            _log.Warning(
                "conversation console started without IConversationQuery; grant 'chat.read' to "
                + "natter.plugin.conversation-console to enable conversation replay");
        }

        var handler = new ConversationConsoleRequestHandler(
            _options,
            authenticator,
            gateway,
            _audit,
            assets,
            _clock,
            _log);

        _server = new ConversationConsoleServer(_options, handler, _status, _log);

        try
        {
            await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null)
            return;

        await _server.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the console token from the plugin secret store. A missing secret is a startup
    /// failure rather than a silent "auth disabled" mode — an unauthenticated console would expose
    /// every stored conversation.
    /// </summary>
    private static string ResolveToken(IPluginSecrets secrets)
    {
        try
        {
            return secrets.GetRequired(ConversationConsoleDefaults.TokenSecretName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.token-missing",
                $"Secret '{ConversationConsoleDefaults.TokenSecretName}' is required but was not provided "
                + $"({exception.GetType().Name}). Set it in the host environment, independent from the "
                + "WebConsole token.");
        }
    }

    /// <summary>Package layouts put assets next to the DLL or one level up from <c>lib/</c>.</summary>
    public static string ResolveStaticRoot(string pluginDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(pluginDirectory, "wwwroot"),
            Path.Combine(pluginDirectory, "lib", "wwwroot"),
        };

        return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
    }

    /// <summary>Resolves the package root from an assembly location (handles the <c>lib/</c> layout).</summary>
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

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
            await _server.DisposeAsync().ConfigureAwait(false);

        _server = null;
    }
}
