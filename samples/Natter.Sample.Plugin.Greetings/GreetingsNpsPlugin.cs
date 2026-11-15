using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>
/// NPS v1 sample: a plugin-local container contributes a port adapter and CLI commands,
/// and starts its domain-event subscription from ActivateAsync.
/// </summary>
public sealed class GreetingsNpsPlugin : INatterPlugin
{
    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var services = context.Services.Collection;

        services.AddSingleton<RecentEventBuffer>();
        services.AddSingleton<RecentEventTracker>();
        services.AddSingleton<FancyGreetingAdapter>();
        services.AddSingleton<IGreetingPort>(provider => provider.GetRequiredService<FancyGreetingAdapter>());
        services.AddTransient<GreetCommandHandler>();
        services.AddTransient<RecentEventsCommandHandler>();

        context.Contributions.AddPortAdapter<IGreetingPort, FancyGreetingAdapter>("greeting");

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "greet",
            Description = "Greet someone through the plugin-provided IGreetingPort adapter.",
            Arguments = [new CliArgumentDescriptor("name", "Name to greet.", Required: true)],
            HandlerFactory = services => services.GetRequiredService<GreetCommandHandler>(),
        });

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "greetings recent",
            Description = "Show the domain events captured by the plugin's Rx subscription.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = services => services.GetRequiredService<RecentEventsCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var provider = context.Services.Provider
                       ?? throw new InvalidOperationException("The plugin provider is not available.");
        var tracker = provider.GetRequiredService<RecentEventTracker>();
        await tracker.StartAsync(provider, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        if (context.Services.Provider?.GetService<RecentEventTracker>() is { } tracker)
            await tracker.StopAsync(context.Services.Provider, cancellationToken).ConfigureAwait(false);
    }
}
