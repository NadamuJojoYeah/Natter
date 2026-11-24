using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat.Discord;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Natter.Cli.Core;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.Discord;
using Natter.Cli.Abstractions;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DiscordNpsTests
{
    [Fact]
    public async Task Sample_discord_plugin_loads_with_config_and_secret_then_connects_via_fake_transport()
    {
        using var temp = new TempDirectory();
        var pluginDirectory = temp.Combine("plugin");
        var configDirectory = temp.Combine("config");
        Directory.CreateDirectory(pluginDirectory);
        Directory.CreateDirectory(configDirectory);

        File.WriteAllText(
            Path.Combine(pluginDirectory, "config.schema.json"),
            ReadEmbedded("Natter.Sample.Plugin.Discord.config.schema.json"));
        File.WriteAllText(
            Path.Combine(configDirectory, "natter.transport.messaging.discord.json"),
            """
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "123",
              "requireMention": false,
              "maxMessageLength": 1900
            }
            """);

        using var transport = new DiscordFakeTransport("123");
        var secrets = new TestSecretProvider(new Dictionary<string, string>
        {
            ["DISCORD_BOT_TOKEN"] = "test-token",
        });

        var manifest = DiscordPlugin.Manifest;
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = configDirectory)
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddEmbeddedNpsPlugin(manifest, new DiscordPlugin(transport), pluginDirectory);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
        Assert.True(
            descriptor.State == PluginState.Activated,
            $"state={descriptor.State} error={descriptor.Error} phase={descriptor.ErrorDetail?.Phase} code={descriptor.ErrorDetail?.Code}");
        var upstream = Assert.Single(host.Services.GetRequiredService<IChatUpstreamRegistry>().Upstreams);
        Assert.Equal("discord:discord:123", upstream.Source.Key);
        Assert.True(upstream.IsConnected);
        Assert.Equal(1, transport.ConnectCount);

        var commands = host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(commands.TryGet("discord status", out var statusCommand));
        var console = new StringConsole();
        var exitCode = await statusCommand.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
            "discord status",
            new Dictionary<string, string?> { ["json"] = null },
            [],
            host.Services,
            console));
        Assert.Equal(0, exitCode);
        Assert.Contains("\"connected\":true", console.Output, StringComparison.Ordinal);

        await host.StopAsync();
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Nps_connects_the_discord_upstream_after_activation_and_disconnects_on_stop()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", RequireMention = false };
        using var transport = new DiscordFakeTransport();

        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "natter.transport.messaging.discord.test",
            Name = "Discord test",
            Version = "0.1.0",
            Kind = PluginKind.Transport,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = typeof(DiscordTestPlugin).AssemblyQualifiedName!,
            },
            Permissions = ["chat.read", "chat.send"],
            Contributions =
            [
                new PluginContribution { Type = "chat.upstream", Id = "discord" },
            ],
        };

        var plugin = new DiscordTestPlugin(source, options, transport);
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddEmbeddedNpsPlugin(manifest, plugin);

        await using (var host = builder.Build())
        {
            await host.StartAsync();

            var upstream = Assert.Single(host.Services.GetRequiredService<IChatUpstreamRegistry>().Upstreams);
            Assert.Equal(source, upstream.Source);
            Assert.True(upstream.IsConnected);
            Assert.Equal(1, transport.ConnectCount);

            await host.StopAsync();
            Assert.Equal(1, transport.DisconnectCount);
            Assert.Empty(host.Services.GetRequiredService<IChatUpstreamRegistry>().Upstreams);
        }
    }

    private sealed class TestSecretProvider(IReadOnlyDictionary<string, string> values) : ISecretProvider
    {
        public bool TryGet(string name, out string? value)
        {
            var found = values.TryGetValue(name, out var result);
            value = result;
            return found;
        }
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = typeof(DiscordPlugin).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "natter-discord-nps-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_root);

        public string Combine(params string[] parts) => Path.Combine([_root, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class DiscordTestPlugin(
        Source source,
        DiscordOptions options,
        IDiscordTransport transport) : INatterPlugin
    {
        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            var services = context.Services.Collection;
            services.AddSingleton(source);
            services.AddSingleton(options);
            services.AddSingleton(transport);
            services.AddSingleton<DiscordChatUpstream>();

            context.Contributions.AddChatUpstream<DiscordChatUpstream>("discord");
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
