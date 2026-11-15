using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Application.Chat;
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
    public async Task P3_context_contribution_is_registered_and_rolled_back()
    {
        var plugin = new PromptContributionPlugin();
        var manifest = Manifest(
            "natter.test.prompt",
            permissions: [],
            contributions: [new PluginContribution { Type = PluginContributionTypes.PromptProvider, Id = "static" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IContextComponentCatalog>();
        Assert.True(catalog.TryGetPrompt("natter.test.prompt#static", out var prompt));
        Assert.NotNull(prompt);
        var bundle = await prompt!.GetAsync(new PromptRequest(
            new RuntimeProfile { Id = "p" },
            Source.Create("discord", "discord", "bot"),
            new Conversation("c", ConversationKind.Direct),
            new User("u", "U"),
            null,
            new Dictionary<string, string>()));
        Assert.Equal("plugin prompt", bundle.SystemPrompt);

        await host.StopAsync();
        Assert.False(catalog.TryGetPrompt("natter.test.prompt#static", out _));
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
    public async Task Chat_responder_and_event_subscriber_contributions_are_active()
    {
        var plugin = new ChatContributionPlugin();
        var manifest = Manifest(
            "natter.test.chat",
            permissions: [PluginPermissions.ChatRead, PluginPermissions.ChatSend],
            contributions:
            [
                new PluginContribution { Type = PluginContributionTypes.ChatResponder, Id = "test" },
                new PluginContribution { Type = PluginContributionTypes.ChatEventSubscriber, Id = "audit" },
            ]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var responders = host.Services.GetRequiredService<IChatResponderRegistry>();
        Assert.Contains(responders.Registrations, registration => registration.Id == "test" && registration.Owner == "natter.test.chat");

        var source = ChatTestData.Source();
        var chatEvent = ChatTestData.CreatedEvent(source, ChatTestData.Message(source));
        host.Services.GetRequiredService<IChatEventPublisher>().Publish(chatEvent);

        Assert.Equal(1, TestSubscriber.Handled);

        var composite = host.Services.GetRequiredService<IChatResponder>();
        var command = await composite.RespondAsync(chatEvent);
        Assert.NotNull(command);

        await host.StopAsync();
        Assert.DoesNotContain(responders.Registrations, registration => registration.Owner == "natter.test.chat");

        var handledAfterStop = TestSubscriber.Handled;
        host.Services.GetRequiredService<IChatEventPublisher>().Publish(chatEvent);
        Assert.Equal(handledAfterStop, TestSubscriber.Handled);
    }

    [Fact]
    public async Task Duplicate_contribution_ids_fail_the_later_plugin()
    {
        var first = new HelloPlugin();
        var second = new HelloPlugin();

        var firstManifest = Manifest(
            "natter.test.dup.a",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }]);
        var secondManifest = Manifest(
            "natter.test.dup.b",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(firstManifest, first)
            .AddEmbeddedNpsPlugin(secondManifest, second);

        await using var host = builder.Build();
        await host.StartAsync();

        var catalog = host.Services.GetRequiredService<IPluginCatalog>();
        Assert.Equal(PluginState.Activated, catalog.Plugins.Single(plugin => plugin.Id == "natter.test.dup.a").State);
        var failed = catalog.Plugins.Single(plugin => plugin.Id == "natter.test.dup.b");
        Assert.Equal(PluginState.Failed, failed.State);
        Assert.Equal("duplicate-contribution", failed.ErrorDetail!.Code);
    }

    [Fact]
    public async Task Declared_contributions_must_be_registered_during_configure()
    {
        var plugin = new EmptyPlugin();
        var manifest = Manifest(
            "natter.test.missing",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "missing.command" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Failed, descriptor.State);
        Assert.Equal("missing-contribution", descriptor.ErrorDetail!.Code);
    }

    [Fact]
    public async Task Plugin_dispatcher_enforces_chat_permissions()
    {
        var plugin = new DispatcherPlugin();
        var manifest = Manifest("natter.test.dispatcher", permissions: [], contributions: []);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        Assert.NotNull(plugin.Ack);
        Assert.False(plugin.Ack!.Ok);
        Assert.Equal(ChatErrorCodes.Forbidden, plugin.Ack.Error!.Code);
    }

    [Fact]
    public async Task Required_secret_is_resolved_only_with_permission()
    {
        var plugin = new SecretPlugin();
        var manifest = Manifest(
            "natter.test.secret.ok",
            permissions: [PluginPermissions.Secret("NPS_TEST_TOKEN")],
            contributions: [],
            secrets: [new PluginSecret { Name = "NPS_TEST_TOKEN", Required = true }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .ConfigurePlugins(options => options.SecretProvider = new InMemorySecretProvider(
                [new KeyValuePair<string, string>("NPS_TEST_TOKEN", "supersecret")]))
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Activated, descriptor.State);
        Assert.Equal("supersecret", plugin.Token);
        Assert.DoesNotContain("supersecret", descriptor.Error ?? string.Empty);
    }

    [Fact]
    public async Task Plugin_package_with_secret_files_is_rejected()
    {
        var directory = CreateTempPluginDirectoryFromSample("""
            {
              "schemaVersion": 1,
              "id": "natter.test.secretfile",
              "name": "Secret File",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": { "assembly": "Natter.Sample.Plugin.Greetings.dll", "type": "Natter.Sample.Plugin.Greetings.GreetingsNpsPlugin" },
              "permissions": [],
              "contributions": []
            }
            """);
        File.WriteAllText(Path.Combine(directory, "token"), "should-not-ship");

        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("sensitive-file", descriptor.ErrorDetail!.Code);
            Assert.Contains("token", descriptor.Error!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_entry_type_is_reported_as_a_load_error()
    {
        var directory = CreateTempPluginDirectoryFromSample("""
            {
              "schemaVersion": 1,
              "id": "natter.test.missingtype",
              "name": "Missing Type",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": { "assembly": "Natter.Sample.Plugin.Greetings.dll", "type": "Does.Not.Exist" },
              "permissions": [],
              "contributions": []
            }
            """);

        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("entry-type-missing", descriptor.ErrorDetail!.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_plugin_is_reported_and_not_activated()
    {
        var plugin = new HelloPlugin();
        var manifest = Manifest(
            "natter.test.disabled",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .ConfigurePlugins(options => options.DisabledPlugins.Add("natter.test.disabled"))
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.Equal(PluginState.Disabled, descriptor.State);
        Assert.False(plugin.Activated);
    }

    [Fact]
    public async Task Duplicate_plugin_ids_fail_both_descriptors()
    {
        var first = new HelloPlugin();
        var second = new HelloPlugin();
        var manifest = Manifest("natter.test.duplicate", permissions: [], contributions: []);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, first)
            .AddEmbeddedNpsPlugin(manifest, second);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptors = host.Services.GetRequiredService<IPluginCatalog>().Plugins
            .Where(plugin => plugin.Id == "natter.test.duplicate")
            .ToList();

        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(PluginState.Failed, descriptor.State);
            Assert.Equal("duplicate-plugin-id", descriptor.ErrorDetail!.Code);
        });
    }

    [Fact]
    public async Task Stop_order_is_the_reverse_of_activation_order()
    {
        OrderedPlugin.Events.Clear();
        var first = new OrderedPlugin("first");
        var second = new OrderedPlugin("second");

        var firstManifest = Manifest("natter.test.order.first", permissions: [], contributions: []);
        var secondManifest = Manifest(
            "natter.test.order.second",
            permissions: [],
            contributions: [],
            loadAfter: ["natter.test.order.first"]);

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(firstManifest, first)
            .AddEmbeddedNpsPlugin(secondManifest, second);

        await using var host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(
            ["activate:first", "activate:second", "deactivate:second", "deactivate:first"],
            OrderedPlugin.Events);
    }

    [Fact]
    public async Task Configure_failure_blocks_required_dependents_before_activation()
    {
        var directory = CreateTempPluginDirectory(
            """{ "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }""",
            """{ "name": 42 }""");

        try
        {
            var failing = new ConfigPlugin();
            var dependent = new HelloPlugin();

            var failingManifest = Manifest(
                "natter.test.cfg.first",
                permissions: [],
                contributions: [],
                configSchema: "config.schema.json");
            var dependentManifest = Manifest(
                "natter.test.cfg.second",
                permissions: [PluginPermissions.CliCommands],
                contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "nps.hello" }],
                dependsOn: [new PluginDependency { Id = "natter.test.cfg.first", Version = ">=0.1.0 <1.0.0" }]);

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddEmbeddedNpsPlugin(failingManifest, failing, directory)
                .AddEmbeddedNpsPlugin(dependentManifest, dependent);

            await using var host = builder.Build();
            await host.StartAsync();

            var catalog = host.Services.GetRequiredService<IPluginCatalog>();
            Assert.Equal(PluginState.Failed, catalog.Plugins.Single(plugin => plugin.Id == "natter.test.cfg.first").State);
            Assert.Equal(PluginState.Blocked, catalog.Plugins.Single(plugin => plugin.Id == "natter.test.cfg.second").State);
            Assert.False(dependent.Activated);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public async Task External_config_directory_is_used_before_plugin_directory_config()
    {
        var pluginDirectory = Path.Combine(Path.GetTempPath(), "natter-nps-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(Path.GetTempPath(), "natter-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pluginDirectory);
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(pluginDirectory, "config.schema.json"),
            """{ "type": "object", "properties": { "name": { "type": "string" }, "count": { "type": "integer" } } }""");
        File.WriteAllText(
            Path.Combine(configDirectory, "natter.test.external.json"),
            """{ "name": "external", "count": 7 }""");

        try
        {
            var plugin = new ConfigPlugin();
            var manifest = Manifest(
                "natter.test.external",
                permissions: [],
                contributions: [],
                configSchema: "config.schema.json");

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .ConfigureOptions(options => options.PluginConfigDirectory = configDirectory)
                .AddEmbeddedNpsPlugin(manifest, plugin, pluginDirectory);

            await using var host = builder.Build();
            await host.StartAsync();

            Assert.Equal("external", plugin.Config?.Name);
            Assert.Equal(7, plugin.Config?.Count);
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
            Directory.Delete(configDirectory, recursive: true);
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

    [Fact]
    public async Task Legacy_manifest_still_loads_with_the_legacy_loader()
    {
        var directory = CreateTempPluginDirectoryFromSample(
            """{ "id": "legacy.sample", "name": "Legacy Sample", "version": "0.1.0", "entryAssembly": "Natter.Sample.Plugin.Greetings.dll", "enabled": true, "capabilities": ["port:IGreetingPort"] }""");

        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Loaded, descriptor.State);
            Assert.Equal("legacy.sample", descriptor.Id);

            var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("legacy");
            Assert.Contains("greetings plugin adapter", greeting);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Nps_manifest_can_point_at_a_legacy_plugin_module()
    {
        var directory = CreateTempPluginDirectoryFromSample("""
            {
              "schemaVersion": 1,
              "id": "natter.test.legacyadapter",
              "name": "Legacy Adapter",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": {
                "assembly": "Natter.Sample.Plugin.Greetings.dll",
                "type": "Natter.Sample.Plugin.Greetings.GreetingsPlugin"
              },
              "permissions": [],
              "contributions": []
            }
            """);

        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Loaded, descriptor.State);
            Assert.Equal("legacy", descriptor.Kind);

            var greeting = await host.Services.GetRequiredService<IGreetingPort>().GreetAsync("legacy");
            Assert.Contains("greetings plugin adapter", greeting);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempPluginDirectoryFromSample(string manifest)
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-nps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var assembly = Path.Combine(AppContext.BaseDirectory, "plugins", "greetings", "Natter.Sample.Plugin.Greetings.dll");
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)));
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest);
        return directory;
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
        string? configSchema = null,
        string[]? loadAfter = null)
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
            LoadAfter = loadAfter?.ToList(),
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

    public sealed class DispatcherPlugin : INatterPlugin
    {
        public ChatAck? Ack { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<DispatcherProbe>();
            return ValueTask.CompletedTask;
        }

        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            var provider = context.Services.Provider
                           ?? throw new InvalidOperationException("plugin provider missing");
            Ack = await provider.GetRequiredService<DispatcherProbe>().SendAsync(cancellationToken);
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class DispatcherProbe(ChatCommandFactory factory, IChatCommandDispatcher dispatcher)
    {
        public ValueTask<ChatAck> SendAsync(CancellationToken cancellationToken)
        {
            var source = Source.Create("test", "test", "bot");
            return dispatcher.ExecuteAsync(
                factory.Send(source, new Conversation("c", ConversationKind.Direct), new Content("hi")),
                cancellationToken);
        }
    }

    public sealed class EmptyPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class SecretPlugin : INatterPlugin
    {
        public string? Token { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Token = context.Secrets.GetRequired("NPS_TEST_TOKEN");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class OrderedPlugin(string name) : INatterPlugin
    {
        public static List<string> Events { get; } = [];

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Events.Add("activate:" + name);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Events.Add("deactivate:" + name);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class ChatContributionPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Services.Collection.AddSingleton<TestResponder>();
            context.Services.Collection.AddSingleton<TestSubscriber>();
            context.Contributions.AddChatResponder<TestResponder>("test");
            context.Contributions.AddChatEventSubscriber<TestSubscriber>("audit");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class TestResponder(ChatCommandFactory factory) : IChatResponder
    {
        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            var message = ((MessageCreatedEvent)chatEvent).Payload.Message;
            return ValueTask.FromResult<ChatCommand?>(factory.Send(
                chatEvent.Source,
                message.Conversation,
                new Content("responder reply"),
                idempotencyKey: "responder:" + chatEvent.Id));
        }
    }

    public sealed class TestSubscriber : IChatEventSubscriber
    {
        public static int Handled { get; set; }

        public ValueTask HandleAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Handled++;
            return ValueTask.CompletedTask;
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
    public sealed class PromptContributionPlugin : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddPromptProvider<PluginPromptProvider>("static");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    public sealed class PluginPromptProvider : IPromptProvider
    {
        public ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new PromptBundle("plugin prompt", null, "plugin-v1"));
    }

}
