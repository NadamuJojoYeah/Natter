using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 3b: the same real turn as the orchestration test, but the plugins are loaded from a
/// file-system plugin root by NPS (not embedded). This is the closest automated approximation of
/// production: the deployable package, the collectible load context, private dependencies such as
/// Discord.Net and Microsoft.Extensions.AI, secret references and the real APIs together.
/// </summary>
[Trait("Category", "Live")]
public sealed class FilePluginE2ELiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [LiveE2EFact(DisplayName = "E2E (file plugins): NPS loads the staged packages and completes one real turn")]
    public async Task File_loaded_plugins_complete_a_real_turn()
    {
        var root = Path.Combine(Path.GetTempPath(), "natter-live-file-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LiveDeployment.StageInto(root, output);

        var secrets = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["DISCORD_BOT_TOKEN"] = Settings.BotToken,
            ["DEEPSEEK_API_KEY"] = Settings.ApiKey,
        });

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddPluginDirectory(root);

        await using var host = builder.Build();
        await host.StartAsync();

        using var rest = new DiscordRestClient(Settings.BotToken);
        var inputId = string.Empty;
        var replyId = string.Empty;

        try
        {
            var catalog = host.Services.GetRequiredService<IPluginCatalog>();
            foreach (var descriptor in catalog.Plugins)
            {
                output.WriteLine(
                    $"plugin {descriptor.Id} state={descriptor.State} trust={descriptor.Trust} "
                    + $"origin={descriptor.Directory} error={descriptor.Error}");
            }

            Assert.Equal(2, catalog.Plugins.Count);
            Assert.All(catalog.Plugins, descriptor => Assert.Equal(PluginState.Activated, descriptor.State));
            Assert.All(catalog.Plugins, descriptor => Assert.Equal("local", descriptor.Trust));

            var registry = host.Services.GetRequiredService<IChatUpstreamRegistry>();
            var connectDeadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < connectDeadline && !registry.Upstreams.Any(item => item.IsConnected))
                await Task.Delay(TimeSpan.FromMilliseconds(250));

            var upstream = Assert.Single(registry.Upstreams);
            Assert.True(upstream.IsConnected);
            Assert.Equal($"discord:discord:{Settings.BotUserId}", upstream.Source.Key);

            var input = await LiveDiscordOracle.CreateInputAsync(rest, Settings.ChannelId, "natter-live-file-e2e");
            inputId = input.GetProperty("id").GetString()!;

            var question = "Answer with one short sentence: what is 6 × 7?";
            var message = new Message(
                inputId,
                new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId, Title: "live"),
                new User("900000000000000002", "live-file-tester", UserKind.Human, "Live File Tester"),
                new Content(
                    question,
                    ContentFormat.Markdown,
                    Mentions: [new Mention(MentionKind.User, Settings.BotUserId, "Natter")]),
                DateTimeOffset.UtcNow,
                EditedAt: null);
            message.EnsureValid();

            host.Services.GetRequiredService<IChatEventPublisher>().Publish(new MessageCreatedEvent
            {
                Id = $"live:file-message:{inputId}:created",
                Source = Source.Create("discord", "discord", Settings.BotUserId),
                Sequence = 1,
                OccurredAt = message.CreatedAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                Payload = new MessagePayload(message),
            });

            var reply = await LiveDiscordOracle.WaitForSingleReplyAsync(
                rest,
                Settings.ChannelId,
                inputId,
                TimeSpan.FromSeconds(60));
            replyId = reply.GetProperty("id").GetString()!;
            var content = reply.GetProperty("content").GetString()!;

            output.WriteLine($"file-plugin reply id={replyId} chars={content.Length} text={content}");

            Assert.False(string.IsNullOrWhiteSpace(content));
            Assert.Equal(Settings.BotUserId, reply.GetProperty("author").GetProperty("id").GetString());
            Assert.DoesNotContain(Settings.ApiKey, content, StringComparison.Ordinal);

            var commands = host.Services.GetRequiredService<CliCommandRegistry>();
            Assert.True(commands.TryGet("discord status", out var statusCommand));
            var console = new StringConsole();
            await statusCommand.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
                "discord status",
                new Dictionary<string, string?> { ["json"] = null },
                [],
                host.Services,
                console));

            using var status = JsonDocument.Parse(console.Output);
            Assert.True(status.RootElement.GetProperty("connected").GetBoolean());
            Assert.Equal(Settings.BotUserId, status.RootElement.GetProperty("source").GetProperty("selfId").GetString());
            Assert.DoesNotContain(Settings.BotToken, console.Output, StringComparison.Ordinal);
            output.WriteLine($"discord status={console.Output.Trim()}");
        }
        finally
        {
            if (replyId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);
            if (inputId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, inputId);

            await host.StopAsync();

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class StringConsole : IConsole
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _error = new();

        public TextWriter Out => _out;

        public TextWriter Error => _error;

        public bool IsInteractive => false;

        public string? ReadLine() => null;

        public string Output => _out.ToString();
    }
}
