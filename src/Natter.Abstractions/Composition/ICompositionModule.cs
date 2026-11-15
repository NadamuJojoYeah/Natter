using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;

namespace Natter.Abstractions.Composition;

/// <summary>
/// A composition module contributes service registrations to the composition root.
/// Framework layers and applications use the same mechanism, so the host never needs
/// to know concrete adapter types.
/// </summary>
public interface ICompositionModule
{
    string Name { get; }

    void Configure(IServiceCollection services, CompositionContext context);
}

/// <summary>Ambient information available while a module is being composed.</summary>
public sealed class CompositionContext
{
    public required CompositionOptions Options { get; init; }

    public required IPortAdapterRegistry Adapters { get; init; }
}

/// <summary>Host-independent composition options.</summary>
public sealed class CompositionOptions
{
    public string ApplicationName { get; set; } = "Natter";

    public string EnvironmentName { get; set; } =
        Environment.GetEnvironmentVariable("NATTER_ENVIRONMENT") ?? "Production";

    /// <summary>Enables the in-memory journal that records every domain event envelope.</summary>
    public bool EnableEventJournal { get; set; } = true;

    /// <summary>Enables scanning of plugin directories at build time.</summary>
    public bool EnablePluginLoading { get; set; } = true;

    /// <summary>Directories scanned for plugins (each subdirectory is one plugin).</summary>
    public IList<string> PluginDirectories { get; } = new List<string>();

    /// <summary>
    /// Optional external config directory. NPS plugins read <c>&lt;dir&gt;/&lt;pluginId&gt;.json</c>
    /// before falling back to <c>pluginDirectory/config.json</c> (used by app-private stores).
    /// </summary>
    public string? PluginConfigDirectory { get; set; }

    public bool ValidateOnBuild { get; set; } = true;
}
