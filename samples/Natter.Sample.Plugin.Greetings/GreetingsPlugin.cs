using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>
/// Sample plugin module. It demonstrates the three plugin extension points:
/// a port adapter override, a CLI command contributor and a lifecycle hook that
/// subscribes to the Rx domain event stream.
/// </summary>
public sealed class GreetingsPlugin : IPluginModule
{
    public PluginMetadata Metadata { get; } = new(
        Id: "greetings",
        Name: "Greetings Plugin",
        Version: "0.1.0",
        Description: "Overrides IGreetingPort and contributes the greet command.",
        Author: "Natter samples",
        Capabilities: ["port:IGreetingPort", "cli:greet", "events:subscriber"]);

    public void Configure(IPluginRegistrationContext context)
    {
        context.AddPortAdapter<IGreetingPort, FancyGreetingAdapter>(
            description: "Plugin greeting adapter (overrides the core binding).");

        context.Services.AddSingleton<RecentEventBuffer>();
        context.Services.AddSingleton<ICliCommandContributor, GreetingsCommandContributor>();
        context.Services.AddTransient<GreetCommandHandler>();
        context.Services.AddTransient<RecentEventsCommandHandler>();
        context.Services.AddSingleton<IPluginLifecycle, RecentEventTracker>();

        context.AddCapability("cli:greetings recent");
        context.AddCapability("lifecycle");
    }
}
