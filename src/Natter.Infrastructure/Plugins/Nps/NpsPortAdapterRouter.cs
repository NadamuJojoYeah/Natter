using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Minimal view of the runtime needed by late-bound port adapters.</summary>
internal interface INpsPluginRuntimeAccessor
{
    PluginState GetPluginState(string pluginId);

    IServiceProvider? GetPluginProvider(string pluginId);
}

/// <summary>One NPS port.adapter contribution ordered by plugin activation sequence.</summary>
internal sealed record NpsPortRegistration(
    string PluginId,
    Type ServiceType,
    Type ImplementationType,
    ServiceLifetime Lifetime,
    long Sequence);

/// <summary>
/// Late-bound host DI bridge for plugin port adapters. The active plugin implementation wins;
/// when the plugin is not activated the original host binding is used as fallback.
/// </summary>
internal sealed class NpsPortAdapterRouter
{
    private readonly INpsPluginRuntimeAccessor _runtime;
    private readonly List<NpsPortRegistration> _registrations = [];
    private readonly Dictionary<Type, Func<IServiceProvider, object?>> _fallbacks = [];
    private readonly Dictionary<Type, object?> _fallbackSingletons = [];
    private readonly Dictionary<(string PluginId, Type ServiceType), object?> _pluginSingletons = [];
    private readonly Lock _gate = new();
    private long _sequence;

    public NpsPortAdapterRouter(INpsPluginRuntimeAccessor runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public IReadOnlyList<NpsPortRegistration> Registrations
    {
        get
        {
            lock (_gate)
                return _registrations.ToArray();
        }
    }

    public void RegisterFallback(Type serviceType, Func<IServiceProvider, object?> factory)
    {
        lock (_gate)
            _fallbacks[serviceType] = factory;
    }

    public void Register(
        string pluginId,
        Type serviceType,
        Type implementationType,
        ServiceLifetime lifetime)
    {
        lock (_gate)
        {
            _registrations.Add(new NpsPortRegistration(
                pluginId,
                serviceType,
                implementationType,
                lifetime,
                ++_sequence));
        }
    }

    public object? Resolve(Type serviceType, IServiceProvider hostProvider)
    {
        NpsPortRegistration? active;

        lock (_gate)
        {
            active = _registrations
                .Where(registration => registration.ServiceType == serviceType)
                .OrderByDescending(registration => registration.Sequence)
                .FirstOrDefault(registration =>
                    _runtime.GetPluginState(registration.PluginId) == PluginState.Activated
                    && _runtime.GetPluginProvider(registration.PluginId) is not null);
        }

        if (active is not null)
        {
            var provider = _runtime.GetPluginProvider(active.PluginId)!;

            if (active.Lifetime == ServiceLifetime.Singleton)
            {
                lock (_gate)
                {
                    if (_pluginSingletons.TryGetValue((active.PluginId, serviceType), out var cached)
                        && cached is not null)
                    {
                        return cached;
                    }

                    var created = provider.GetRequiredService(active.ImplementationType);
                    _pluginSingletons[(active.PluginId, serviceType)] = created;
                    return created;
                }
            }

            return provider.GetRequiredService(active.ImplementationType);
        }

        Func<IServiceProvider, object?>? fallback;
        lock (_gate)
            _fallbacks.TryGetValue(serviceType, out fallback);

        if (fallback is null)
        {
            throw new InvalidOperationException(
                $"No active plugin or fallback adapter is available for '{serviceType.FullName}'.");
        }

        // The original descriptor's lifetime is respected for the common singleton/default case.
        lock (_gate)
        {
            if (_fallbackSingletons.TryGetValue(serviceType, out var cached))
                return cached;
        }

        var value = fallback(hostProvider);
        lock (_gate)
            _fallbackSingletons[serviceType] = value;

        return value;
    }

    public static Func<IServiceProvider, object?> CreateFallbackFactory(ServiceDescriptor? descriptor)
    {
        if (descriptor is null)
            return _ => null;

        if (descriptor.ImplementationInstance is not null)
            return _ => descriptor.ImplementationInstance;

        if (descriptor.ImplementationFactory is not null)
            return descriptor.ImplementationFactory;

        if (descriptor.ImplementationType is not null)
            return provider => ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);

        return _ => null;
    }
}
