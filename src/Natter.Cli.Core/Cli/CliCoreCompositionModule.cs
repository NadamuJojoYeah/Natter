using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Application.Admin;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Commands;
using Natter.Infrastructure.Admin;

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
        // The NPS runtime withdraws a plugin's CLI commands on teardown through this sink. Resolving
        // it is deferred to that moment, so the registry itself stays lazily constructed (task-6).
        services.AddSingleton<Natter.Infrastructure.Plugins.Nps.IPluginCliCommandSink>(
            provider => provider.GetRequiredService<CliCommandRegistry>());
        // S1-11: host startup is data driven — built-in rules here, plugins may add their own.
        services.AddSingleton<ICliHostRequirementProvider, DefaultCliHostRequirements>();
        services.AddSingleton<CliHostRequirementCatalog>();
        services.AddSingleton<CliRunner>();
        services.AddSingleton<IAdminCommandRunner>(provider => new CliAdminCommandRunner(
            provider,
            provider.GetRequiredService<ConsoleAccessor>(),
            provider.GetService<AdminOptions>() ?? new AdminOptions(),
            provider.GetService<IAdminRedactor>() ?? AdminRedactor.Default,
            provider.GetService<IAdminAuditSink>() ?? NullAdminAuditSink.Instance,
            provider.GetRequiredService<ILogger<CliAdminCommandRunner>>()));

        services.AddSingleton<CliEventPrinter>();
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<CliEventPrinter>());

        services.AddTransient<VersionCommand>();
        services.AddTransient<ExecCommand>();
        services.AddTransient<DoctorCommand>();
        services.AddTransient<PortsCommand>();
        services.AddTransient<PluginsListCommand>();
        services.AddTransient<PluginsInspectCommand>();
        services.AddTransient<PluginsGraphCommand>();
        services.AddTransient<PluginsDoctorCommand>();
        services.AddTransient<PluginsContributionsCommand>();
        services.AddTransient<PluginsPermissionsCommand>();
        services.AddTransient<PluginsConfigValidateCommand>();
        services.AddTransient<PluginsStoreListCommand>();
        services.AddTransient<ServeCommand>();
        services.AddTransient<RememberCommand>();
        services.AddTransient<ForgetCommand>();
        services.AddTransient<MemoryListCommand>();
        services.AddTransient<MemoryExportCommand>();
        services.AddTransient<MemoryImportCommand>();
        services.AddTransient<HealthCommand>();
        services.AddTransient<PluginsStoreInstallCommand>();
        services.AddTransient<PluginsStoreRollbackCommand>();
        services.AddTransient<PluginsStoreUninstallCommand>();
        services.AddTransient<PluginsStoreCleanupCommand>();
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
