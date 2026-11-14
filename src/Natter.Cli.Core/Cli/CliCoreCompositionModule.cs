using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Commands;

namespace Natter.Cli.Core;

/// <summary>Composition module for the CLI kernel (commands, runner, console accessor).</summary>
public sealed class CliCoreCompositionModule : ICompositionModule
{
    public string Name => "Host.Cli";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        services.AddSingleton(new NatterCliOptions());
        services.AddSingleton<ConsoleAccessor>();
        services.AddSingleton<IConsoleAccessor>(provider => provider.GetRequiredService<ConsoleAccessor>());
        services.AddSingleton<CliCommandRegistry>();
        services.AddSingleton<CliRunner>();

        services.AddSingleton<CliEventPrinter>();
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<CliEventPrinter>());

        services.AddTransient<VersionCommand>();
        services.AddTransient<ExecCommand>();
        services.AddTransient<DoctorCommand>();
        services.AddTransient<PortsCommand>();
        services.AddTransient<PluginsListCommand>();
        services.AddTransient<PluginsInspectCommand>();
        services.AddTransient<EventsListCommand>();
        services.AddTransient<EventsWatchCommand>();
        services.AddTransient<EventsClearCommand>();
        services.AddTransient<UpstreamsCommand>();
        services.AddTransient<SatoriStatusCommand>();
        services.AddTransient<DemoCommand>();
        services.AddTransient<HelpCommand>();

        foreach (var descriptor in BuiltInCommands.Create())
            services.AddSingleton(descriptor);
    }
}
