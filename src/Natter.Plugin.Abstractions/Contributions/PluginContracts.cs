using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;

namespace Natter.Plugin.Abstractions;

/// <summary>NPS v1 plugin entry point.</summary>
public interface INatterPlugin
{
    /// <summary>Registration/preparation only; must not perform network or message side effects.</summary>
    ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>Starts plugin resources after staging has been committed.</summary>
    ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default);

    /// <summary>Stops plugin resources; must be idempotent.</summary>
    ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default);
}

/// <summary>Everything a plugin can see at configure/activate/deactivate time.</summary>
public interface IPluginContext
{
    PluginManifest Manifest { get; }

    IPluginConfig Config { get; }

    IPluginSecrets Secrets { get; }

    IPluginLog Log { get; }

    IPluginLifetime Lifetime { get; }

    IPluginServices Services { get; }

    IContributionRegistry Contributions { get; }

    IPluginDependencies Dependencies { get; }
}

/// <summary>Validated plugin configuration.</summary>
public interface IPluginConfig
{
    JsonElement Raw { get; }

    T Get<T>() where T : class;

    bool TryGet<T>(out T? value) where T : class;

    bool TryGetSecretReference(string jsonPointer, out SecretReference? reference);
}

/// <summary>Secret access guarded by manifest permissions.</summary>
public interface IPluginSecrets
{
    string GetRequired(string name);

    string? Get(string name);
}

/// <summary>Plugin-scoped logger; host prepends the plugin id.</summary>
public interface IPluginLog
{
    void Debug(string message, params object?[] args);

    void Information(string message, params object?[] args);

    void Warning(string message, params object?[] args);

    void Error(Exception? exception, string message, params object?[] args);
}

/// <summary>Host stop signal and cleanup callbacks.</summary>
public interface IPluginLifetime
{
    CancellationToken Stopping { get; }

    IDisposable OnStop(Func<CancellationToken, ValueTask> callback);
}

/// <summary>Plugin-local DI registrations. Host contracts are resolved from the plugin provider.</summary>
public interface IPluginServices
{
    IServiceCollection Collection { get; }

    /// <summary>Available after the host has committed contributions; null during ConfigureAsync.</summary>
    IServiceProvider? Provider { get; }
}

/// <summary>Dependency state visible to a plugin.</summary>
public interface IPluginDependencies
{
    bool IsLoaded(string pluginId);

    string? GetVersion(string pluginId);
}

/// <summary>Typed contribution registration API. Bare host DI access is intentionally absent.</summary>
public interface IContributionRegistry
{
    void AddChatUpstream<TUpstream>(string id)
        where TUpstream : class, IChatUpstream;

    void AddChatResponder<TResponder>(string id, int priority = 0)
        where TResponder : class, IChatResponder;

    void AddChatEventSubscriber<TSubscriber>(string id)
        where TSubscriber : class, IChatEventSubscriber;

    void AddCliCommand(CliCommandDescriptor descriptor);

    void AddPortAdapter<TService, TImplementation>(
        string id,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class
        where TImplementation : class, TService;

    void AddPromptProvider<TProvider>(string id)
        where TProvider : class, IPromptProvider;

    void AddContextAssembler<TAssembler>(string id)
        where TAssembler : class, IContextAssembler;

    void AddMemoryStore<TStore>(string id)
        where TStore : class, IMemoryStore;
}

/// <summary>Token-like secret reference used in config values; never contains the secret.</summary>
public readonly record struct SecretReference(string Name)
{
    public const string Prefix = "${secret:";

    public static bool TryParse(string? value, out SecretReference reference)
    {
        reference = default;

        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !value.EndsWith('}'))
        {
            return false;
        }

        var name = value[Prefix.Length..^1];
        if (string.IsNullOrWhiteSpace(name))
            return false;

        reference = new SecretReference(name);
        return true;
    }

    public override string ToString() => $"{Prefix}{Name}}}";
}

/// <summary>Raised when a plugin registers an undeclared or unauthorized contribution.</summary>
public sealed class PluginContributionException : Exception
{
    public PluginContributionException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when a plugin lacks a permission required by a contribution.</summary>
public sealed class PluginPermissionException : Exception
{
    public PluginPermissionException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when a required secret is missing or access is denied.</summary>
public sealed class PluginSecretException : Exception
{
    public PluginSecretException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when plugin configuration cannot be materialized into the requested type.</summary>
public sealed class PluginConfigException : Exception
{
    public PluginConfigException(string message)
        : base(message)
    {
    }

    public PluginConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
