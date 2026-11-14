using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Application;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Events;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Adapters;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Dispatching;
using Natter.Infrastructure.Events;

namespace Natter.Infrastructure.Modules;

/// <summary>
/// Core composition module: registers the framework's default adapters for every port.
/// Everything registered here can be replaced by a later registration or a plugin.
/// </summary>
public sealed class CoreCompositionModule : ICompositionModule
{
    public string Name => "Framework.Core";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        var registry = context.Adapters;

        // Time
        services.AddPortAdapter<IClock, SystemClock>(
            registry,
            ServiceLifetime.Singleton,
            origin: "framework:core",
            description: "System UTC clock.");

        // Domain event stream (Rx adapter)
        services.AddSingleton<RxDomainEventBus>();
        services.AddSingleton<IDomainEventStream>(sp => sp.GetRequiredService<RxDomainEventBus>());
        services.AddSingleton<IDomainEventPublisher>(sp => sp.GetRequiredService<RxDomainEventBus>());
        services.AddSingleton<IDomainEventBusDiagnostics>(sp => sp.GetRequiredService<RxDomainEventBus>());
        registry.Register(new PortAdapterDescriptor(
            nameof(IDomainEventStream),
            nameof(RxDomainEventBus),
            ServiceLifetime.Singleton,
            "framework:core",
            "Rx Subject based hot stream with subscriber isolation."));
        registry.Register(new PortAdapterDescriptor(
            nameof(IDomainEventPublisher),
            nameof(RxDomainEventBus),
            ServiceLifetime.Singleton,
            "framework:core",
            "Publishes domain events into the Rx stream."));

        services.AddSingleton<IDomainEventDispatcher, DomainEventDispatcher>();
        registry.Register(new PortAdapterDescriptor(
            nameof(IDomainEventDispatcher),
            nameof(DomainEventDispatcher),
            ServiceLifetime.Singleton,
            "framework:core",
            "Drains aggregate events into the event stream."));

        // Event journal (replay/diagnostics adapter)
        if (context.Options.EnableEventJournal)
        {
            services.AddSingleton<IEventJournal, InMemoryEventJournal>();
            registry.Register(new PortAdapterDescriptor(
                nameof(IEventJournal),
                nameof(InMemoryEventJournal),
                ServiceLifetime.Singleton,
                "framework:core",
                "Bounded in-memory replay buffer."));
        }

        // Persistence
        services.AddSingleton(typeof(IRepository<,>), typeof(InMemoryRepository<,>));
        registry.Register(new PortAdapterDescriptor(
            "IRepository<TAggregate, TId>",
            "InMemoryRepository<TAggregate, TId>",
            ServiceLifetime.Singleton,
            "framework:core",
            "Open-generic in-memory repository; replace with a real adapter."));

        services.AddPortAdapter<IUnitOfWork, InMemoryUnitOfWork>(
            registry,
            ServiceLifetime.Singleton,
            origin: "framework:core",
            description: "No-op transaction boundary.");

        // Example driven port
        services.AddPortAdapter<IGreetingPort, DefaultGreetingAdapter>(
            registry,
            ServiceLifetime.Singleton,
            origin: "framework:core",
            description: "Default greeting adapter.");

        // Cross-cutting application pipeline
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>));
    }
}
