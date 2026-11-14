using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins;

/// <summary>One successfully loaded plugin module plus the load context that owns it.</summary>
public sealed record LoadedPlugin(
    PluginDescriptor Descriptor,
    IPluginModule Module,
    PluginLoadContext LoadContext);

/// <summary>Result of scanning one plugin root directory.</summary>
public sealed class PluginLoadResult
{
    public PluginLoadResult(
        IReadOnlyList<LoadedPlugin> plugins,
        IReadOnlyList<PluginDescriptor> failures)
    {
        Plugins = plugins;
        Failures = failures;
    }

    public IReadOnlyList<LoadedPlugin> Plugins { get; }

    public IReadOnlyList<PluginDescriptor> Failures { get; }
}
