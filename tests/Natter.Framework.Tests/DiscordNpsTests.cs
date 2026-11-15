using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DiscordNpsTests
{
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
            Id = "natter.transport.discord.test",
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
