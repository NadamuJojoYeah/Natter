using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NpsRuntimeTests
{
    [Fact]
    public async Task Embedded_nps_plugin_activates_and_contributes_a_cli_command()
    {
        var plugin = new HelloPlugin();
        var manifest = Manifest(
            "natter.test.hello",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        var descriptor = Assert.Single(catalog.Plugins);
        Assert.Equal(PluginState.Activated, descriptor.State);
        Assert.True(plugin.Activated);
        Assert.False(plugin.Deactivated);

        var registry = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("nps hello", out var command));

        var console = new StringConsole();
        var handler = command.HandlerFactory(host.Services);
        var exitCode = await handler.ExecuteAsync(new CliInvocation(
            command.Name,
            new Dictionary<string, string?>(),
            [],
            host.Services,
            console));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("hello from nps", console.Output);

        await host.StopAsync();
        Assert.True(plugin.Deactivated);
        Assert.Equal(PluginState.Stopped, catalog.Plugins.Single().State);
    }

    [Fact]
    public async Task Activation_failure_is_reported_and_rolls_back()
    {
        var plugin = new FailingPlugin();
        var manifest = Manifest("natter.test.failing", permissions: [], contributions: []);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("activate-failed", descriptor.ErrorDetail!.Code);
    }

    [Fact]
    public async Task Port_adapter_contribution_overrides_and_rolls_back_to_the_host_adapter()
    {
        var plugin = new PortPlugin();
        var manifest = Manifest(
            "natter.test.port",
            permissions: [],
            contributions: [new PluginContribution { Type = PluginContributionTypes.PortAdapter, Id = "greeting" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var pluginGreeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("tester");
        Assert.Contains("nps port adapter", pluginGreeting);

        await host.StopAsync();
        Assert.Equal(PluginState.Stopped, host.Services.GetRequiredService<IPluginCatalog>().Plugins.Single().State);

        var fallbackGreeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("tester");
        Assert.Contains("core greeting adapter", fallbackGreeting);
    }

    [Fact]
    public async Task Chat_upstream_contribution_is_connected_by_the_host_and_disconnected_on_stop()
    {
        var plugin = new UpstreamPlugin();
        var manifest = Manifest(
            "natter.test.upstream",
            permissions: [PluginPermissions.ChatRead, PluginPermissions.ChatSend],
            contributions: [new PluginContribution { Type = PluginContributionTypes.ChatUpstream, Id = "fake" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var upstreams = host.Services.GetRequiredService<IChatUpstreamRegistry>();
        var upstream = Assert.Single(upstreams.Upstreams);
        var fake = Assert.IsType<FakeUpstream>(upstream);
        Assert.Equal(1, fake.ConnectCalls);

        await host.StopAsync();
        Assert.Equal(1, fake.DisconnectCalls);
        Assert.Empty(upstreams.Upstreams);
    }

    [Fact]
    public async Task Activation_failure_blocks_required_dependents()
    {
        var failing = new FailingPlugin();
        var dependent = new HelloPlugin();

        var failingManifest = Manifest("natter.test.first", permissions: [], contributions: []);
        var dependentManifest = Manifest(
            "natter.test.second",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }],
            dependsOn: [new PluginDependency { Id = "natter.test.first", Version = ">=0.1.0 <1.0.0" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(failingManifest, failing)
            .AddEmbeddedNpsPlugin(dependentManifest, dependent);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        Assert.Equal(PluginState.Failed, catalog.Plugins.Single(p => p.Id == "natter.test.first").State);
        Assert.Equal(PluginState.Blocked, catalog.Plugins.Single(p => p.Id == "natter.test.second").State);
    }

    [Fact]
    public async Task Denied_contribution_permission_fails_configure_without_loading_plugin()
    {
        var plugin = new HelloPlugin();
        var manifest = Manifest(
            "natter.test.permission",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .ConfigurePlugins(options =>
            {
                options.AutoGrantBundledPermissions = false;
                options.ForceDeniedPermissions.Add(PluginPermissions.CliCommands);
            })
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Contains("permission", descriptor.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_required_secret_fails_before_activation_and_never_prints_the_value()
    {
        var plugin = new HelloPlugin();
        var manifest = Manifest(
            "natter.test.secret",
            permissions: [PluginPermissions.Secret("NPS_TEST_TOKEN")],
            contributions: [],
            secrets: [new PluginSecret { Name = "NPS_TEST_TOKEN", Required = true }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("missing-secret", descriptor.ErrorDetail!.Code);
        Assert.DoesNotContain("supersecret", descriptor.Error!, StringComparison.Ordinal);
        Assert.False(plugin.Activated);
    }

    [Fact]
    public async Task Valid_config_is_validated_and_defaults_are_visible_to_the_plugin()
    {
        var directory = CreateTempPluginDirectory(
            """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "count": { "type": "integer", "default": 3 } } }""",
            """{ "name": "ok" }""");

        try
        {
            var plugin = new ConfigPlugin();
            var manifest = Manifest(
                "natter.test.config",
                permissions: [],
                contributions: [],
                configSchema: "config.schema.json");

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddEmbeddedNpsPlugin(manifest, plugin, directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);
            Assert.NotNull(plugin.Config);
            Assert.Equal("ok", plugin.Config!.Name);
            Assert.Equal(3, plugin.Config.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_config_fails_before_activation_and_is_visible_to_diagnostics()
    {
        var directory = CreateTempPluginDirectory(
            """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "count": { "type": "integer" } } }""",
            """{ "name": 42 }""");

        try
        {
            var plugin = new ConfigPlugin();
            var manifest = Manifest(
                "natter.test.badconfig",
                permissions: [],
                contributions: [],
                configSchema: "config.schema.json");

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddEmbeddedNpsPlugin(manifest, plugin, directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("invalid-config", descriptor.ErrorDetail!.Code);
            Assert.False(plugin.Activated);

            var diagnostics = host.Services.GetRequiredService<IPluginRuntimeDiagnostics>();
            var validation = diagnostics.ValidateConfig("natter.test.badconfig");
            Assert.False(validation.Valid);
            Assert.Contains(validation.Issues, issue => issue.Pointer == "/name");

            var doctor = diagnostics.GetDoctor("natter.test.badconfig");
            Assert.False(doctor.ConfigValid);
            Assert.Equal("invalid-config", doctor.Error!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempPluginDirectory(string schema, string config)
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-nps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "config.schema.json"), schema);
        File.WriteAllText(Path.Combine(directory, "config.json"), config);
        return directory;
    }

    private static PluginManifest Manifest(
        string id,
        string[]? permissions = null,
        PluginContribution[]? contributions = null,
        PluginDependency[]? dependsOn = null,
        PluginSecret[]? secrets = null,
        string? configSchema = null)
        => new()
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
                Type = typeof(HelloPlugin).AssemblyQualifiedName!,
            },
            Permissions = permissions?.ToList(),
            Contributions = contributions?.ToList(),
            DependsOn = dependsOn?.ToList(),
            Secrets = secrets?.ToList(),
            ConfigSchema = configSchema,
        };

    public sealed class HelloPlugin : INatterPlugin
    {
        public bool Activated { get; private set; }

        public bool Deactivated { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<HelloHandler>();
            context.Contributions.AddCliCommand(new CliCommandDescriptor
            {
                Name = "nps hello",
                Description = "NPS test command.",
                Origin = "test",
                HandlerFactory = services => services.GetRequiredService<HelloHandler>(),
            });

            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Activated = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Deactivated = true;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class HelloHandler : ICliCommandHandler
    {
        public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
        {
            invocation.Console.Out.WriteLine("hello from nps");
            return ValueTask.FromResult(CliExitCodes.Success);
        }
    }

    public sealed class ConfigPlugin : INatterPlugin
    {
        public TestConfig? Config { get; private set; }

        public bool Activated { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Config = context.Config.Get<TestConfig>();
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Activated = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class TestConfig
    {
        public string? Name { get; set; }

        public int Count { get; set; }
    }

    public sealed class FailingPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class PortPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<PortGreetingAdapter>();
            context.Contributions.AddPortAdapter<IGreetingPort, PortGreetingAdapter>("greeting");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class PortGreetingAdapter : IGreetingPort
    {
        public ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult($"hello {name} from nps port adapter");
    }

    public sealed class UpstreamPlugin : INatterPlugin
    {
        public static FakeUpstream? LastCreated { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<FakeUpstream>();
            context.Contributions.AddChatUpstream<FakeUpstream>("fake");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class FakeUpstream : IChatUpstream
    {
        public Source Source { get; } = Source.Create("fake", "test", "bot");

        public bool IsConnected { get; private set; }

        public int ConnectCalls { get; private set; }

        public int DisconnectCalls { get; private set; }

        public IObservable<ChatEvent> Events => EmptyObservable<ChatEvent>.Instance;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask<Capabilities> GetCapabilitiesAsync(
            Conversation? conversation = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Capabilities.TextOnly);

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatAck.Success(command, ["fake-message"]));
    }
}
