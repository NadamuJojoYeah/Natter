using Microsoft.Extensions.DependencyInjection;

namespace Natter.Abstractions.Plugins;

/// <summary>Static identity of a plugin.</summary>
public sealed record PluginMetadata(
    string Id,
    string Name,
    string Version,
    string? Description = null,
    string? Author = null,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>
/// Entry point of a plugin. Implementations must expose a public parameterless
/// constructor so the loader can instantiate them inside the plugin load context.
/// </summary>
public interface IPluginModule
{
    PluginMetadata Metadata { get; }

    void Configure(IPluginRegistrationContext context);
}

/// <summary>Registration surface handed to a plugin during composition.</summary>
public interface IPluginRegistrationContext
{
    IServiceCollection Services { get; }

    string PluginId { get; }

    string PluginDirectory { get; }

    string AssemblyPath { get; }

    PluginMetadata Metadata { get; }

    IReadOnlyCollection<string> Capabilities { get; }

    /// <summary>Adds a capability marker shown by `plugins list`.</summary>
    void AddCapability(string capability);

    /// <summary>Registers an adapter for a port and records the binding in the adapter catalog.</summary>
    void AddPortAdapter<TService, TImplementation>(
        ServiceLifetime lifetime = ServiceLifetime.Singleton,
        string? description = null)
        where TService : class
        where TImplementation : class, TService;
}

/// <summary>Optional plugin lifecycle (started/stopped by the host, e.g. to subscribe to Rx).</summary>
public interface IPluginLifecycle
{
    ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default);

    ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}

public enum PluginState
{
    Discovered,
    Validated,
    Loaded,
    Configured,
    Activated,
    Stopped,
    Unloaded,
    Failed,
    Blocked,
    Disabled,
    Skipped,
}

/// <summary>Declared or runtime contribution, shown by `plugins contributions`.</summary>
public sealed record PluginContributionDescriptor(
    string Type,
    string Id,
    int Priority = 0,
    string Status = "declared");

/// <summary>Structured plugin error (manifest/dependency/config/permission/load/configure/activate/runtime).</summary>
public sealed record PluginErrorDescriptor(
    string Phase,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string>? Detail = null);

/// <summary>Immutable snapshot of a plugin as seen by the catalog.</summary>
public sealed record PluginDescriptor(
    string Id,
    string Name,
    string Version,
    string AssemblyPath,
    string Directory,
    PluginState State,
    string? Error = null,
    IReadOnlyList<string>? Capabilities = null,
    string? Description = null,
    bool IsDynamic = false,
    string? Kind = null,
    string? HostApi = null,
    string? Trust = null,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyList<string>? Permissions = null,
    IReadOnlyList<string>? GrantedPermissions = null,
    IReadOnlyList<PluginContributionDescriptor>? Contributions = null,
    PluginErrorDescriptor? ErrorDetail = null);

/// <summary>Runtime view of loaded plugins.</summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginDescriptor> Plugins { get; }

    IReadOnlyList<IPluginModule> Modules { get; }

    bool Contains(string pluginId);
}

/// <summary>Result of inspecting a plugin directory without registering it.</summary>
public sealed record PluginProbeResult(
    string Directory,
    bool IsValid,
    string? Error = null,
    string? Id = null,
    string? Name = null,
    string? Version = null,
    string? EntryAssembly = null,
    IReadOnlyList<string>? Assemblies = null,
    IReadOnlyList<string>? Capabilities = null,
    int? SchemaVersion = null,
    string? Kind = null,
    string? HostApi = null,
    string? EntryType = null,
    IReadOnlyList<string>? Dependencies = null,
    IReadOnlyList<string>? Permissions = null,
    IReadOnlyList<PluginContributionDescriptor>? Contributions = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Read-only plugin inspection port (used by the CLI).</summary>
public interface IPluginProbe
{
    PluginProbeResult Probe(string directory);
}
