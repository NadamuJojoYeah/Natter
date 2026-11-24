namespace Natter.Infrastructure.Plugins;

/// <summary>
/// Explicit host/plugin loading policy. Contracts are shared for type identity; everything
/// else is plugin-private. This list is the single source of truth and can be replaced by a
/// host composition module without editing the loader.
/// </summary>
public sealed class PluginLoadContextOptions
{
    public IReadOnlySet<string> ContractAssemblies { get; init; } = PluginContractAssemblies.All;

    /// <summary>Allows a plain plugin folder without deps.json to resolve sibling DLLs.</summary>
    public bool EnableDirectoryFallback { get; init; } = true;

    public static PluginLoadContextOptions Default { get; } = new();
}

/// <summary>
/// The host contract closure. Only assemblies listed here are shared with the default ALC;
/// adapters, providers, and third-party libraries are loaded from the plugin directory.
/// </summary>
public static class PluginContractAssemblies
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Natter.Abstractions",
        "Natter.Contracts",
        "Natter.Prompting",
        "Natter.Application",
        "Natter.Chat",
        "Natter.Cli.Abstractions",
        "Natter.Domain",
        "Natter.Plugin.Abstractions",
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.AI.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Primitives",
        "System.Reactive",
    };
}
