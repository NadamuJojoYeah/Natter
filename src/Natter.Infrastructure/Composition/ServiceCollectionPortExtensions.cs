using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Composition;

/// <summary>Registration helpers that keep DI and the adapter catalog in sync.</summary>
public static class ServiceCollectionPortExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TImplementation"/> for <typeparamref name="TService"/>
    /// and records the binding so `ports` can display it.
    /// </summary>
    public static IServiceCollection AddPortAdapter<TService, TImplementation>(
        this IServiceCollection services,
        IPortAdapterRegistry registry,
        ServiceLifetime lifetime = ServiceLifetime.Singleton,
        string origin = "framework:core",
        string? description = null)
        where TService : class
        where TImplementation : class, TService
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);

        services.Add(new ServiceDescriptor(typeof(TService), typeof(TImplementation), lifetime));
        registry.Register(new PortAdapterDescriptor(
            Port: GetDisplayName(typeof(TService)),
            Adapter: GetDisplayName(typeof(TImplementation)),
            Lifetime: lifetime,
            Origin: origin,
            Description: description));

        return services;
    }

    /// <summary>Records a binding whose registration is custom (factory, open generic, ...).</summary>
    public static IPortAdapterRegistry RegisterAdapter(
        this IPortAdapterRegistry registry,
        Type portType,
        Type adapterType,
        ServiceLifetime lifetime,
        string origin,
        string? description = null)
    {
        registry.Register(new PortAdapterDescriptor(
            GetDisplayName(portType),
            GetDisplayName(adapterType),
            lifetime,
            origin,
            description));

        return registry;
    }

    internal static string GetDisplayName(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRepository<,>)
            ? "IRepository<TAggregate, TId>"
            : type.FullName ?? type.Name;
}
