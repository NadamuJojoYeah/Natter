using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Plugin-local DI collection; host contracts are added when the provider is built.</summary>
internal sealed class NpsPluginServices : IPluginServices
{
    public IServiceCollection Collection { get; } = new ServiceCollection();

    public IServiceProvider? Provider { get; internal set; }

    /// <summary>
    /// Drops the plugin-scoped service descriptors (they reference plugin types) and the built
    /// provider, so the collectible load context is not rooted after unload (S2-C3).
    /// </summary>
    public void Unload()
    {
        Provider = null;
        Collection.Clear();
    }
}

/// <summary>Plugin-scoped logger with a small in-memory buffer for diagnostics.</summary>
internal sealed class NpsPluginLog : IPluginLog
{
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();
    private readonly string _pluginId;
    private ILogger? _logger;

    public NpsPluginLog(string pluginId) => _pluginId = pluginId;

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToArray();
        }
    }

    public void Attach(ILogger logger) => _logger = logger;

    public void Debug(string message, params object?[] args) => Write(LogLevel.Debug, null, message, args);

    public void Information(string message, params object?[] args) => Write(LogLevel.Information, null, message, args);

    public void Warning(string message, params object?[] args) => Write(LogLevel.Warning, null, message, args);

    public void Error(Exception? exception, string message, params object?[] args)
        => Write(LogLevel.Error, exception, message, args);

    private void Write(LogLevel level, Exception? exception, string message, object?[] args)
    {
        var rendered = Render(message, args);

        lock (_gate)
        {
            _entries.Add($"[{level}] {rendered}");
            if (_entries.Count > 128)
                _entries.RemoveAt(0);
        }

        _logger?.Log(level, exception, "{PluginId}: {PluginMessage}", _pluginId, rendered);
    }

    private static string Render(string message, object?[] args)
    {
        if (args.Length == 0)
            return message;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, message, args);
        }
        catch (FormatException)
        {
            return message + " " + string.Join(", ", args.Select(arg => arg?.ToString()));
        }
    }
}

/// <summary>Plugin stop signal and cleanup callbacks.</summary>
internal sealed class NpsPluginLifetime : IPluginLifetime, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Func<CancellationToken, ValueTask>> _callbacks = [];
    private readonly Lock _gate = new();
    private bool _stopped;

    public CancellationToken Stopping => _cts.Token;

    public IDisposable OnStop(Func<CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            if (_stopped)
                return new NoopDisposable();

            _callbacks.Add(callback);
        }

        return new CallbackHandle(this, callback);
    }

    public async ValueTask StopAsync()
    {
        Func<CancellationToken, ValueTask>[] callbacks;
        CancellationToken token;

        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            callbacks = _callbacks.ToArray();
            _callbacks.Clear();
            token = _cts.Token;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        for (var index = callbacks.Length - 1; index >= 0; index--)
        {
            try
            {
                await callbacks[index](token).ConfigureAwait(false);
            }
            catch
            {
                // Cleanup callbacks are best-effort; plugin deactivation reports its own errors.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private void Remove(Func<CancellationToken, ValueTask> callback)
    {
        lock (_gate)
            _callbacks.Remove(callback);
    }

    private sealed class CallbackHandle(NpsPluginLifetime owner, Func<CancellationToken, ValueTask> callback) : IDisposable
    {
        private Func<CancellationToken, ValueTask>? _callback = callback;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _callback, null);
            if (current is not null)
                owner.Remove(current);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Dependency view backed by the runtime state.</summary>
internal sealed class NpsPluginDependencies : IPluginDependencies
{
    private readonly Func<string, PluginState> _state;
    private readonly Func<string, string?> _version;

    public NpsPluginDependencies(Func<string, PluginState> state, Func<string, string?> version)
    {
        _state = state;
        _version = version;
    }

    public bool IsLoaded(string pluginId)
        => _state(pluginId) is PluginState.Activated or PluginState.Loaded;

    public string? GetVersion(string pluginId) => _version(pluginId);
}

/// <summary>Validated plugin configuration with schema defaults applied in memory.</summary>
internal sealed class NpsPluginConfig : IPluginConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _pluginId;

    public NpsPluginConfig(string pluginId, JsonElement raw)
    {
        _pluginId = pluginId;
        Raw = raw.Clone();
    }

    public JsonElement Raw { get; }

    public T Get<T>() where T : class
    {
        if (TryGet<T>(out var value) && value is not null)
            return value;

        throw new PluginConfigException($"Config for plugin '{_pluginId}' is not a valid {typeof(T).Name}.");
    }

    public bool TryGet<T>(out T? value) where T : class
    {
        try
        {
            value = Raw.Deserialize<T>(SerializerOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    public bool TryGetSecretReference(string jsonPointer, out SecretReference? reference)
    {
        reference = null;
        var element = PluginConfigSchema.ResolvePointer(Raw, jsonPointer);
        return element is { ValueKind: JsonValueKind.String }
               && SecretReference.TryParse(element.Value.GetString(), out var parsed)
               && (reference = parsed) is not null;
    }
}

/// <summary>Secret access guarded by granted <c>secrets:NAME</c> permissions.</summary>
internal sealed class NpsPluginSecrets : IPluginSecrets
{
    private readonly ISecretProvider _provider;
    private readonly IReadOnlySet<string> _granted;
    private readonly string _pluginId;

    public NpsPluginSecrets(string pluginId, ISecretProvider provider, IReadOnlyList<string> granted)
    {
        _pluginId = pluginId;
        _provider = provider;
        _granted = new HashSet<string>(granted, StringComparer.Ordinal);
    }

    public string GetRequired(string name)
        => Get(name) ?? throw new PluginSecretException(
            $"Required secret '{name}' for plugin '{_pluginId}' is missing.");

    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new PluginSecretException("Secret name must not be empty.");

        if (!_granted.Contains(PluginPermissions.Secret(name)))
            throw new PluginSecretException(
                $"Plugin '{_pluginId}' does not have permission '{PluginPermissions.Secret(name)}'.");

        return _provider.TryGet(name, out var value) ? value : null;
    }
}

/// <summary>Concrete NPS plugin context handed to INatterPlugin.</summary>
internal sealed class NpsPluginContext : IPluginContext
{
    public NpsPluginContext(
        PluginManifest manifest,
        NpsPluginConfig config,
        NpsPluginSecrets secrets,
        NpsPluginLog log,
        NpsPluginLifetime lifetime,
        NpsPluginServices services,
        IContributionRegistry contributions,
        NpsPluginDependencies dependencies)
    {
        Manifest = manifest;
        Config = config;
        Secrets = secrets;
        Log = log;
        Lifetime = lifetime;
        Services = services;
        Contributions = contributions;
        Dependencies = dependencies;
    }

    public PluginManifest Manifest { get; }

    public IPluginConfig Config { get; }

    public IPluginSecrets Secrets { get; }

    public IPluginLog Log { get; }

    public IPluginLifetime Lifetime { get; }

    public IPluginServices Services { get; }

    public IContributionRegistry Contributions { get; }

    public IPluginDependencies Dependencies { get; }
}
