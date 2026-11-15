using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Result of classifying plugin directories into NPS candidates and legacy modules.</summary>
internal sealed class PluginScanResult
{
    public List<NpsPluginCandidate> Candidates { get; } = [];

    public List<LoadedPlugin> LegacyPlugins { get; } = [];

    public List<PluginDescriptor> LegacyDescriptors { get; } = [];

    public List<PluginDescriptor> Failures { get; } = [];
}
