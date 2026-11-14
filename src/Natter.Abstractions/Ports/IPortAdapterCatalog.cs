using Microsoft.Extensions.DependencyInjection;

namespace Natter.Abstractions.Ports;

/// <summary>Describes one port-to-adapter binding for diagnostics and `ports` CLI output.</summary>
public sealed record PortAdapterDescriptor(
    string Port,
    string Adapter,
    ServiceLifetime Lifetime,
    string Origin,
    string? Description = null);

/// <summary>Read access to every port/adapter binding registered in the composition root.</summary>
public interface IPortAdapterCatalog
{
    IReadOnlyList<PortAdapterDescriptor> Adapters { get; }

    bool TryGet(string port, out PortAdapterDescriptor? descriptor);
}

/// <summary>Write access used by composition modules and plugin registration contexts.</summary>
public interface IPortAdapterRegistry : IPortAdapterCatalog
{
    void Register(PortAdapterDescriptor descriptor);
}
