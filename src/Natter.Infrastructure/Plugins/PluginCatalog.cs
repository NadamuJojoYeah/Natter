using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins;

/// <summary>
/// Process-wide plugin catalog. It owns loaded modules and the collectible load
/// contexts, so disposing the catalog releases plugin assemblies.
/// </summary>
public sealed class PluginCatalog : IPluginCatalog, IDisposable
{
    private readonly object _gate = new();
    private readonly List<PluginDescriptor> _descriptors = [];
    private readonly List<IPluginModule> _modules = [];
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get
        {
            lock (_gate) return _descriptors.ToArray();
        }
    }

    public IReadOnlyList<IPluginModule> Modules
    {
        get
        {
            lock (_gate) return _modules.ToArray();
        }
    }

    public bool Contains(string pluginId)
    {
        lock (_gate) return _ids.Contains(pluginId);
    }

    /// <summary>Adds a loader result and returns the plugins that were actually accepted.</summary>
    public IReadOnlyList<LoadedPlugin> Add(PluginLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var accepted = new List<LoadedPlugin>();

        lock (_gate)
        {
            _descriptors.AddRange(result.Failures);

            foreach (var loaded in result.Plugins)
            {
                if (!_ids.Add(loaded.Descriptor.Id))
                {
                    _descriptors.Add(loaded.Descriptor with
                    {
                        State = PluginState.Failed,
                        Error = $"Duplicate plugin id '{loaded.Descriptor.Id}'.",
                    });
                    TryUnload(loaded.LoadContext);
                    continue;
                }

                _descriptors.Add(loaded.Descriptor);
                _modules.Add(loaded.Module);
                _contexts.Add(loaded.LoadContext);
                accepted.Add(loaded);
            }
        }

        return accepted;
    }

    /// <summary>Registers a plugin that is compiled into the host (e.g. the Android head).</summary>
    public PluginDescriptor RegisterEmbedded(IPluginModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var metadata = module.Metadata;

        lock (_gate)
        {
            var id = metadata.Id;
            var suffix = 2;
            while (!_ids.Add(id))
                id = $"{metadata.Id}.{suffix++}";

            var descriptor = new PluginDescriptor(
                Id: id,
                Name: metadata.Name,
                Version: metadata.Version,
                AssemblyPath: "<embedded>",
                Directory: AppContext.BaseDirectory,
                State: PluginState.Loaded,
                Capabilities: metadata.Capabilities,
                Description: metadata.Description,
                IsDynamic: true);

            _descriptors.Add(descriptor);
            _modules.Add(module);
            return descriptor;
        }
    }

    public void UpdateCapabilities(PluginDescriptor descriptor, IReadOnlyCollection<string> capabilities)
    {
        lock (_gate)
        {
            var index = IndexOf(descriptor);
            if (index < 0) return;

            _descriptors[index] = _descriptors[index] with
            {
                Capabilities = (_descriptors[index].Capabilities ?? [])
                    .Concat(capabilities)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
        }
    }

    public void MarkFailed(PluginDescriptor descriptor, Exception exception)
    {
        lock (_gate)
        {
            var index = IndexOf(descriptor);
            if (index < 0) return;

            _descriptors[index] = _descriptors[index] with
            {
                State = PluginState.Failed,
                Error = $"{exception.GetType().Name}: {exception.Message}",
            };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var context in _contexts)
                TryUnload(context);
        }
    }

    private int IndexOf(PluginDescriptor descriptor)
        => _descriptors.FindIndex(existing =>
            string.Equals(existing.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.AssemblyPath, descriptor.AssemblyPath, StringComparison.OrdinalIgnoreCase));

    private static void TryUnload(PluginLoadContext context)
    {
        try
        {
            context.Unload();
        }
        catch
        {
            // Unloading is best-effort; a referenced assembly keeps the context alive.
        }
    }
}
