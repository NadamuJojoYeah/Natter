using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class PluginLoadingTests
{
    private static string PluginRoot => Path.Combine(AppContext.BaseDirectory, "plugins");
    private static string GreetingsDirectory => Path.Combine(PluginRoot, "greetings");

    [Fact]
    public void Probe_reports_manifest_and_entry_assembly()
    {
        var result = new PluginProbe().Probe(GreetingsDirectory);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("greetings", result.Id);
        Assert.Equal("Natter.Sample.Plugin.Greetings.dll", result.EntryAssembly);
        Assert.Contains("cli:greet", result.Capabilities!);
    }

    [Fact]
    public async Task Host_loads_plugin_overrides_adapter_and_contributes_command()
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddPluginDirectory(PluginRoot);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        var descriptor = Assert.Single(catalog.Plugins, plugin => plugin.Id == "greetings");
        Assert.Equal(PluginState.Loaded, descriptor.State);
        Assert.Contains("port:IGreetingPort", descriptor.Capabilities!);

        var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("tester");
        Assert.Contains("greetings plugin adapter", greeting);

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("greet", out var command));
        Assert.Equal("plugin:greetings", command.Origin);

        var adapters = host.Services.GetRequiredService<IPortAdapterCatalog>();
        Assert.Contains(adapters.Adapters, adapter =>
            adapter.Origin == "plugin:greetings"
            && adapter.Port.EndsWith(nameof(IGreetingPort), StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public void Missing_directory_is_reported_without_throwing()
    {
        var result = new PluginLoader().LoadDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.Empty(result.Plugins);
        Assert.Single(result.Failures);
        Assert.Equal(PluginState.Failed, result.Failures[0].State);
    }
}
