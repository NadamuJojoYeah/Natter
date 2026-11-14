using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Application;
using Natter.Abstractions.Composition;
using Natter.Application.Demo;
using Natter.Application.Dispatching;

namespace Natter.Application;

/// <summary>Composition module for the application layer.</summary>
public sealed class ApplicationCompositionModule : ICompositionModule
{
    public string Name => "Application";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        services.AddSingleton<IApplicationDispatcher, ApplicationDispatcher>();
        services.AddTransient<IRequestHandler<IncrementCounterCommand, CounterSnapshot>, IncrementCounterHandler>();
    }
}
