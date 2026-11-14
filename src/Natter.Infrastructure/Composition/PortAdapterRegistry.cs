using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Composition;

/// <summary>Thread-safe registry of port/adapter bindings used by diagnostics and plugins.</summary>
public sealed class PortAdapterRegistry : IPortAdapterRegistry
{
    private readonly List<PortAdapterDescriptor> _adapters = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<PortAdapterDescriptor> Adapters
    {
        get
        {
            lock (_gate) return _adapters.ToArray();
        }
    }

    public void Register(PortAdapterDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_gate)
        {
            _adapters.RemoveAll(existing =>
                existing.Port == descriptor.Port
                && existing.Adapter == descriptor.Adapter
                && existing.Origin == descriptor.Origin);
            _adapters.Add(descriptor);
        }
    }

    public bool TryGet(string port, out PortAdapterDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);

        lock (_gate)
        {
            // Last registration wins: plugins override framework defaults.
            descriptor = _adapters.LastOrDefault(adapter =>
                adapter.Port == port
                || adapter.Port.EndsWith("." + port, StringComparison.Ordinal));
            return descriptor is not null;
        }
    }
}
