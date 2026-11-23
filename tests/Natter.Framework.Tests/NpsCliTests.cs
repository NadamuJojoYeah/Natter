using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NpsCliTests
{
    [Fact]
    public async Task Nps_cli_command_runs_through_the_plugin_provider()
    {
        var console = new StringConsole();
        var plugin = new NpsRuntimeTests.HelloPlugin();

        var exitCode = await NatterCli.RunAsync(
            ["nps", "hello"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("hello from nps", console.Output);
    }

    [Fact]
    public async Task Plugin_graph_contributions_permissions_and_doctor_are_machine_readable()
    {
        var console = new StringConsole();
        var plugin = new NpsRuntimeTests.HelloPlugin();

        var graphExit = await NatterCli.RunAsync(
            ["plugins", "graph", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        Assert.Equal(CliExitCodes.Success, graphExit);
        using (var graph = JsonDocument.Parse(console.Output))
        {
            var node = Assert.Single(graph.RootElement.GetProperty("nodes").EnumerateArray());
            Assert.Equal("natter.test.hello", node.GetProperty("id").GetString());
            Assert.Equal("activated", node.GetProperty("state").GetString(), ignoreCase: true);
        }

        console = new StringConsole();
        await NatterCli.RunAsync(
            ["plugins", "contributions", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        using (var contributions = JsonDocument.Parse(console.Output))
        {
            var items = contributions.RootElement.EnumerateArray().ToArray();
            var contribution = Assert.Single(items, item => item.GetProperty("source").GetString() == "plugin");
            Assert.Equal("cli.command", contribution.GetProperty("type").GetString());
            Assert.Equal("nps.hello", contribution.GetProperty("id").GetString());
            Assert.Equal("active", contribution.GetProperty("status").GetString());
            Assert.Contains(items, item => item.GetProperty("owner").GetString() == "host"
                                           && item.GetProperty("type").GetString() == "prompt.provider");
            Assert.Contains(items, item => item.GetProperty("owner").GetString() == "host"
                                           && item.GetProperty("type").GetString() == "memory.store");
        }

        console = new StringConsole();
        await NatterCli.RunAsync(
            ["plugins", "permissions", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        using (var permissions = JsonDocument.Parse(console.Output))
        {
            var permission = Assert.Single(permissions.RootElement.EnumerateArray());
            Assert.Equal("natter.test.hello", permission.GetProperty("pluginId").GetString());
            Assert.Contains("cli.commands", permission.GetProperty("granted").EnumerateArray().Select(item => item.GetString()));
        }

        console = new StringConsole();
        await NatterCli.RunAsync(
            ["plugins", "doctor", "natter.test.hello", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        using (var doctor = JsonDocument.Parse(console.Output))
        {
            Assert.Equal("natter.test.hello", doctor.RootElement.GetProperty("pluginId").GetString());
            Assert.Equal("activated", doctor.RootElement.GetProperty("state").GetString(), ignoreCase: true);
            Assert.True(doctor.RootElement.GetProperty("hostApiCompatible").GetBoolean());
        }

        console = new StringConsole();
        var validateExit = await NatterCli.RunAsync(
            ["plugins", "config", "validate", "natter.test.hello", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        Assert.Equal(CliExitCodes.Success, validateExit);
        using var validation = JsonDocument.Parse(console.Output);
        Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Doctor_summary_includes_nps_contribution_count()
    {
        var console = new StringConsole();
        var plugin = new NpsRuntimeTests.HelloPlugin();

        var exitCode = await NatterCli.RunAsync(
            ["doctor", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal(1, document.RootElement.GetProperty("plugins").GetProperty("contributions").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("plugins").GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task Plugin_list_detail_exposes_nps_fields()
    {
        var console = new StringConsole();
        var plugin = new NpsRuntimeTests.HelloPlugin();

        var exitCode = await NatterCli.RunAsync(
            ["plugins", "list", "--detail", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(Manifest(), plugin));

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        var descriptor = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("feature", descriptor.GetProperty("kind").GetString());
        Assert.Equal(">=0.1.0 <1.0.0", descriptor.GetProperty("hostApi").GetString());
    }

    private static PluginManifest Manifest()
        => new()
        {
            SchemaVersion = 1,
            Id = "natter.test.hello",
            Name = "NPS hello",
            Version = "0.1.0",
            Kind = PluginKind.Feature,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = typeof(NpsRuntimeTests.HelloPlugin).AssemblyQualifiedName!,
            },
            Permissions = [PluginPermissions.CliCommands],
            Contributions =
            [
                new PluginContribution
                {
                    Type = PluginContributionTypes.CliCommand,
                    Id = "nps.hello",
                },
            ],
        };

    private static NatterCliOptions NoPlugins() => new() { LoadPlugins = false };
    [Fact]
    public async Task Host_and_plugin_contributions_are_deduplicated()
    {
        var console = new StringConsole();
        var plugin = new Natter.Sample.Plugin.Memory.MemoryStorePlugin();
        var exitCode = await NatterCli.RunAsync(
            ["plugins", "contributions", "--json"],
            console,
            NoPlugins(),
            builder => builder.AddEmbeddedNpsPlugin(
                Natter.Sample.Plugin.Memory.MemoryStorePlugin.Manifest,
                plugin));

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        var rows = document.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("owner").GetString() == "natter.sample.memory.demo")
            .ToArray();
        Assert.Single(rows);
        Assert.Equal("memory.store", rows[0].GetProperty("type").GetString());
    }

}
