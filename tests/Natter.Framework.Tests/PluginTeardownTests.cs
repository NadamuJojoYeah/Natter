using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Framework.Tests;

/// <summary>
/// task-43: a plugin's CLI commands must disappear when the plugin is torn down.
///
/// Before this fix the registry snapshotted <c>NpsCliCommandContributor.GetCommands()</c> once and the
/// teardown path only called <c>candidate.Unload()</c>. A stopped plugin stayed resolvable
/// (<c>natter help</c> listed it, invoking it reached an unloaded handler) and its
/// <c>HandlerFactory</c> closure kept the plugin provider alive.
///
/// Host-side strong references to plugin types were audited while fixing this. Status:
///  * CLI command registry            — FIXED here (owner-scoped withdrawal + contributor reload);
///  * port adapter router             — already cleared by S2-C12 (<c>UnregisterPlugin</c>);
///  * upstream/responder/context caps — already cleared in the stop path;
///  * subscription disposables        — already disposed in the stop path;
///  * plugin provider / DI singletons — disposed and nulled in the stop path;
///  * <c>PluginCatalog._contexts</c>  — holds the collectible ALC until the catalog is disposed.
///    That file (<c>src/Natter.Infrastructure/Plugins/PluginCatalog.cs</c>) is outside this task's
///    write scope, so it is recorded rather than "fixed" by loosening a test.
/// End-to-end ALC collection therefore still is not proven; the assertions below cover routing and
/// command visibility, which is what actually misbehaves in production.
/// </summary>
public sealed class PluginTeardownTests
{
    private const string GreetingId = "natter.test.greeting";
    private const string HelperId = "natter.test.helper";

    private readonly ITestOutputHelper _output;

    public PluginTeardownTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The merge-verify smoke, reproduced verbatim: start the host, print the command table, stop the
    /// host, print it again and try to invoke the withdrawn command.
    /// </summary>
    [Fact]
    public async Task Smoke_stop_async_withdraws_plugin_commands()
    {
        await using var host = BuildHost((GreetingId, "greet", new TeardownPlugin("greet")));
        await host.StartAsync();

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        var runner = host.Services.GetRequiredService<CliRunner>();

        _output.WriteLine("before StopAsync: " + Describe(host, registry));
        _output.WriteLine($"  resolve('greet') = {registry.TryGet("greet", out var before)} origin={before?.Origin}");

        await host.StopAsync();

        _output.WriteLine("after StopAsync : " + Describe(host, registry));
        _output.WriteLine($"  resolve('greet') = {registry.TryGet("greet", out _)}");

        var console = new StringConsole();
        var exitCode = await runner.RunAsync(["greet"], console);
        _output.WriteLine($"  invoke 'greet'  -> exit={exitCode} stderr={console.ErrorText.Trim()}");

        Assert.False(registry.TryGet("greet", out _));
        Assert.Equal(CliExitCodes.Usage, exitCode);
    }

    [Fact]
    public async Task Stopped_plugin_loses_its_cli_commands_while_the_host_is_alive()
    {
        await using var host = BuildHost(
            (GreetingId, "greet", new TeardownPlugin("greet")),
            (HelperId, "helper", new TeardownPlugin("helper")));

        await host.StartAsync();

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("greet", out var greet), Describe(host, registry));
        Assert.Equal($"plugin:{GreetingId}", greet.Origin);
        Assert.True(registry.TryGet("helper", out _));

        // Real teardown: StopAsync walks each started plugin through StopCandidateAsync.
        await host.StopAsync();

        Assert.False(registry.TryGet("greet", out _));
        Assert.False(registry.TryGet("helper", out _));
        Assert.DoesNotContain(registry.Commands, command => command.Name == "greet");
        Assert.DoesNotContain(registry.Commands, command => command.Name == "helper");
    }

    [Fact]
    public async Task Withdrawing_one_owner_keeps_the_other_plugins_commands()
    {
        await using var host = BuildHost(
            (GreetingId, "greet", new TeardownPlugin("greet")),
            (HelperId, "helper", new TeardownPlugin("helper")));

        await host.StartAsync();
        var registry = host.Services.GetRequiredService<CliCommandRegistry>();

        // The runtime has no single-plugin stop API, so this pins the owner-level isolation the
        // per-plugin withdrawal relies on: removing one owner must not touch another's commands.
        registry.ReplaceOwnerCommands($"plugin:{GreetingId}", []);

        Assert.False(registry.TryGet("greet", out _));
        Assert.True(registry.TryGet("helper", out var helper));
        Assert.Equal($"plugin:{HelperId}", helper.Origin);

        // Idempotent: repeating the withdrawal is a no-op, not an exception.
        registry.ReplaceOwnerCommands($"plugin:{GreetingId}", []);
        registry.UnregisterOwner($"plugin:{GreetingId}");
        Assert.True(registry.TryGet("helper", out _));
    }

    [Fact]
    public async Task Withdrawn_command_is_reported_as_unknown_instead_of_reaching_the_handler()
    {
        var plugin = new TeardownPlugin("greet");
        await using var host = BuildHost((GreetingId, "greet", plugin));
        await host.StartAsync();

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        var runner = host.Services.GetRequiredService<CliRunner>();

        var console = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await runner.RunAsync(["greet"], console));
        Assert.Equal(1, plugin.InvocationCount);

        registry.ReplaceOwnerCommands($"plugin:{GreetingId}", []);

        console = new StringConsole();
        var exitCode = await runner.RunAsync(["greet"], console);

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("Unknown command", console.ErrorText);
        // The handler must not run again: the provider is gone with the plugin.
        Assert.Equal(1, plugin.InvocationCount);
    }

    [Fact]
    public async Task Repeated_stop_and_dispose_are_idempotent()
    {
        var host = BuildHost(
            (GreetingId, "greet", new TeardownPlugin("greet")),
            (HelperId, "helper", new TeardownPlugin("helper")));

        await host.StartAsync();
        var registry = host.Services.GetRequiredService<CliCommandRegistry>();

        await host.StopAsync();
        await host.StopAsync();      // guarded by _started
        await host.DisposeAsync();
        await host.DisposeAsync();   // guarded by _disposed

        Assert.False(registry.TryGet("greet", out _));
        Assert.False(registry.TryGet("helper", out _));
    }

    [Fact]
    public async Task Plugin_commands_are_visible_after_activation_only()
    {
        // task-6 regression lock: the registry is constructed lazily, after the runtime activated the
        // plugins. A pre-activation snapshot would contain no plugin commands at all.
        await using var host = BuildHost((GreetingId, "greet", new TeardownPlugin("greet")));

        // Resolve only after activation: resolving earlier would snapshot an empty plugin command set.
        await host.StartAsync();

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("greet", out var greet), Describe(host, registry));
        Assert.Equal($"plugin:{GreetingId}", greet.Origin);
        Assert.False(registry.TryGet(NpsCliCommandOwners.For(GreetingId), out _));
    }

    [Fact]
    public async Task Sink_and_registry_are_the_same_instance()
    {
        // The runtime withdraws through IPluginCliCommandSink; a second registry instance would make
        // the withdrawal miss the catalog the runner uses.
        await using var host = BuildHost((GreetingId, "greet", new TeardownPlugin("greet")));
        await host.StartAsync();

        Assert.Same(
            host.Services.GetRequiredService<CliCommandRegistry>(),
            host.Services.GetRequiredService<IPluginCliCommandSink>());
    }

    [Fact]
    public async Task Embedded_plugins_cannot_prove_alc_collection()
    {
        // Observation only, deliberately not an assertion: embedded plugins live in the test
        // assembly's own load context, so a surviving WeakReference says nothing about the
        // collectible ALC. See the class remarks for the audited reference chains.
        var plugin = new TeardownPlugin("observe");
        var host = BuildHost(("natter.test.observe", "observe", plugin));
        await host.StartAsync();

        var weak = new WeakReference(plugin);
        await host.StopAsync();
        await host.DisposeAsync();
        host = null;
        plugin = null;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(weak.IsAlive || !weak.IsAlive, "observation only — never asserted as a pass");
    }

    // ---------------------------------------------------------------- helpers
    private static string Describe(NatterHost host, CliCommandRegistry registry)
    {
        var commands = string.Join(",", registry.Commands.Select(command => $"{command.Name}@{command.Origin}"));
        var plugins = string.Join(",", host.Services.GetRequiredService<IPluginCatalog>().Plugins
            .Select(plugin => $"{plugin.Id}:{plugin.State}:{plugin.Error}"));
        return $"commands=[{commands}] plugins=[{plugins}]";
    }

    private static NatterHost BuildHost(params (string Id, string Command, TeardownPlugin Plugin)[] plugins)
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureServices(services => services.AddSingleton(new NatterCliOptions { LoadPlugins = false }));

        foreach (var (id, command, plugin) in plugins)
            builder = builder.AddEmbeddedNpsPlugin(ManifestFor(id, command), plugin);

        return builder.Build();
    }

    private static PluginManifest ManifestFor(string id, string commandName) => new()
    {
        SchemaVersion = 1,
        Id = id,
        Name = id,
        Version = "0.1.0",
        Kind = PluginKind.Feature,
        HostApi = ">=0.1.0 <1.0.0",
        Entry = new PluginEntry
        {
            Assembly = "Natter.Framework.Tests.dll",
            Type = typeof(TeardownPlugin).AssemblyQualifiedName!,
        },
        Permissions = [PluginPermissions.CliCommands],
        Contributions =
        [
            new PluginContribution
            {
                Type = PluginContributionTypes.CliCommand,
                Id = commandName.Replace(' ', '.'),
            },
        ],
    };

    /// <summary>Plugin that contributes exactly one CLI command and counts invocations.</summary>
    public sealed class TeardownPlugin(string commandName) : INatterPlugin
    {
        public string CommandName { get; } = commandName;

        public int InvocationCount { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddCliCommand(new CliCommandDescriptor
            {
                Name = CommandName,
                Description = "task-43 teardown probe.",
                Origin = "test",
                HandlerFactory = _ => new InvocationHandler(this),
            });

            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        private sealed class InvocationHandler(TeardownPlugin owner) : ICliCommandHandler
        {
            public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
            {
                owner.InvocationCount++;
                invocation.Console.Out.WriteLine($"teardown probe '{owner.CommandName}'");
                return ValueTask.FromResult(CliExitCodes.Success);
            }
        }
    }
}
