using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Composition;

namespace Natter.Infrastructure.Plugins;

internal sealed class PluginRegistrationContext : IPluginRegistrationContext
{
    private readonly IPortAdapterRegistry _registry;
    private readonly List<string> _capabilities = [];
    private readonly object _gate = new();

    public PluginRegistrationContext(
        IServiceCollection services,
        IPortAdapterRegistry registry,
        string pluginId,
        string pluginDirectory,
        string assemblyPath,
        PluginMetadata metadata)
    {
        Services = services;
        _registry = registry;
        PluginId = pluginId;
        PluginDirectory = pluginDirectory;
        AssemblyPath = assemblyPath;
        Metadata = metadata;
    }

    public IServiceCollection Services { get; }

    public string PluginId { get; }

    public string PluginDirectory { get; }

    public string AssemblyPath { get; }

    public PluginMetadata Metadata { get; }

    public IReadOnlyCollection<string> Capabilities
    {
        get
        {
            lock (_gate) return _capabilities.ToArray();
        }
    }

    public void AddCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return;

        lock (_gate)
        {
            if (!_capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                _capabilities.Add(capability);
        }
    }

    public void AddPortAdapter<TService, TImplementation>(
        ServiceLifetime lifetime = ServiceLifetime.Singleton,
        string? description = null)
        where TService : class
        where TImplementation : class, TService
    {
        Services.AddPortAdapter<TService, TImplementation>(
            _registry,
            lifetime,
            origin: $"plugin:{PluginId}",
            description: description);

        AddCapability($"port:{typeof(TService).Name}");
    }
}
