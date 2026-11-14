using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>Contributes commands to the CLI through the ICliCommandContributor contract.</summary>
public sealed class GreetingsCommandContributor : ICliCommandContributor
{
    public IEnumerable<CliCommandDescriptor> GetCommands()
    {
        yield return new CliCommandDescriptor
        {
            Name = "greet",
            Description = "Greet someone through the plugin-provided IGreetingPort adapter.",
            Arguments = [new CliArgumentDescriptor("name", "Name to greet.", Required: true)],
            Origin = "plugin:greetings",
            HandlerFactory = services => services.GetRequiredService<GreetCommandHandler>(),
        };

        yield return new CliCommandDescriptor
        {
            Name = "greetings recent",
            Description = "Show the domain events captured by the plugin's Rx subscription.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            Origin = "plugin:greetings",
            HandlerFactory = services => services.GetRequiredService<RecentEventsCommandHandler>(),
        };
    }
}
