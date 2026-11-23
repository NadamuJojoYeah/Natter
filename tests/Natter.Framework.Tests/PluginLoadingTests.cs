using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Natter.Sample.Plugin.Greetings;
using Natter.Sample.Plugin.Metrics;
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
        Assert.Equal("natter.sample.port.greetings", result.Id);
        Assert.Equal("Natter.Sample.Plugin.Greetings.dll", result.EntryAssembly);
        Assert.Contains("cli:greet", result.Capabilities!);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal("sample", result.Kind);
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
        var descriptor = Assert.Single(catalog.Plugins, plugin => plugin.Id == "natter.sample.port.greetings");
        Assert.Equal(PluginState.Activated, descriptor.State);
        Assert.Contains("port:IGreetingPort", descriptor.Capabilities!);

        var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("tester");
        Assert.Contains("greetings plugin adapter", greeting);

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("greet", out var command));
        Assert.Equal("plugin:natter.sample.port.greetings", command.Origin);

        var adapters = host.Services.GetRequiredService<IPortAdapterCatalog>();
        Assert.Contains(adapters.Adapters, adapter =>
            adapter.Origin == "plugin:natter.sample.port.greetings"
            && adapter.Port.EndsWith(nameof(IGreetingPort), StringComparison.Ordinal));

        await host.StopAsync();
    }

    [Fact]
    public async Task Embedded_nps_sample_manifests_are_loadable_on_android_profiles()
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddEmbeddedNpsPlugin(GreetingsNpsPlugin.Manifest, new GreetingsNpsPlugin())
            .AddEmbeddedNpsPlugin(MetricsNpsPlugin.Manifest, new MetricsNpsPlugin());

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        Assert.All(
            catalog.Plugins,
            plugin => Assert.Equal(PluginState.Activated, plugin.State));

        var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("android");
        Assert.Contains("greetings plugin adapter", greeting);

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("metrics", out _));
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
