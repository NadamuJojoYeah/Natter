using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Cli.Core;
using Natter.Feature.Ai.DeepSeek;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 3c: a <b>real gateway inbound</b> mention end to end. The earlier injected E2E could not
/// see BUG-003 because it bypassed the mapper. Here the input message is created through Discord
/// REST, delivered by the gateway, mapped by DiscordEventMapper, and processed by the real
/// orchestrator with the real DeepSeek responder.
///
/// The only identity available is the bot itself, so the test enables ProcessBotMessages and
/// disables IgnoreSelfMessages for this run. The reply contains no mention, so the mapper's
/// require-mention gate drops it and the scenario cannot loop.
/// </summary>
[Trait("Category", "Live")]
public sealed class RealInboundMentionLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [LiveE2EFact(DisplayName = "E2E (real inbound): gateway mention → orchestrator → DeepSeek → Discord reply")]
    public async Task Real_gateway_mention_triggers_ai_reply()
    {
        using var temp = new TempDirectory();
        var discordDir = temp.Combine("discord");
        var deepSeekDir = temp.Combine("deepseek");
        var configDir = temp.Combine("config");
        Directory.CreateDirectory(discordDir);
        Directory.CreateDirectory(deepSeekDir);
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(discordDir, "config.schema.json"), ReadEmbedded(
            typeof(DiscordPlugin).Assembly, "Natter.Sample.Plugin.Discord.config.schema.json"));
        File.WriteAllText(Path.Combine(deepSeekDir, "config.schema.json"), ReadEmbedded(
            typeof(DeepSeekPlugin).Assembly, "Natter.Feature.Ai.DeepSeek.config.schema.json"));

        File.WriteAllText(Path.Combine(configDir, "natter.transport.discord.json"), $$"""
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "{{Settings.BotUserId}}",
              "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
              "guilds": ["{{Settings.GuildId}}"],
              "channels": ["{{Settings.ChannelId}}"],
              "requireMention": true,
              "ignoreBots": false,
              "ignoreSelf": false,
              "typing": false,
              "messageCommands": true,
              "slashCommands": false,
              "maxMessageLength": 1900,
              "readyTimeoutSeconds": 60
            }
            """);

        File.WriteAllText(Path.Combine(configDir, "natter.feature.ai.deepseek.json"), """
            {
              "apiKey": "${secret:DEEPSEEK_API_KEY}",
              "model": "deepseek-flash",
              "thinking": "disabled",
              "reasoningEffort": "none",
              "systemPrompt": "You are Natter, a concise Discord assistant. Reply in one short sentence.",
              "historyMaxMessages": 6,
              "maxOutputTokens": 200,
              "timeoutSeconds": 60,
              "tools": []
            }
            """);

        var transportOptions = new DiscordOptions
        {
            Token = "${secret:DISCORD_BOT_TOKEN}",
            SelfId = Settings.BotUserId,
            Intents = ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
            Guilds = [Settings.GuildId],
            Channels = [Settings.ChannelId],
            RequireMention = true,
            MessageCommands = true,
            IgnoreBots = false,
            IgnoreSelf = false,
            Typing = false,
            ReadyTimeoutSeconds = 60,
        };

        using var transport = new DiscordSocketTransport(transportOptions, DiscordBotToken.From(Settings.BotToken));
        var secrets = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["DISCORD_BOT_TOKEN"] = Settings.BotToken,
            ["DEEPSEEK_API_KEY"] = Settings.ApiKey,
        });

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = configDir)
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddEmbeddedNpsPlugin(DiscordPlugin.Manifest, new DiscordPlugin(transport), discordDir)
            .AddEmbeddedNpsPlugin(DeepSeekPlugin.Manifest, new DeepSeekPlugin(), deepSeekDir)
            // Bot-friendliness for the test harness only: the inbound identity is the bot itself.
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(
                new ChatOrchestrationOptions
                {
                    ProcessBotMessages = true,
                    IgnoreSelfMessages = false,
                    RequireMentionOrDirect = true,
                })));

        await using var host = builder.Build();
        await host.StartAsync();

        using var rest = new DiscordRestClient(Settings.BotToken);
        var inputId = string.Empty;
        var replyId = string.Empty;
        var observed = new System.Collections.Concurrent.ConcurrentQueue<ChatEvent>();

        try
        {
            Assert.All(
                host.Services.GetRequiredService<IPluginCatalog>().Plugins,
                descriptor => Assert.Equal(PluginState.Activated, descriptor.State));

            var registry = host.Services.GetRequiredService<IChatUpstreamRegistry>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline && !registry.Upstreams.Any(item => item.IsConnected))
                await Task.Delay(TimeSpan.FromMilliseconds(250));

            var upstream = Assert.Single(registry.Upstreams);
            Assert.True(upstream.IsConnected);
            using var subscription = upstream.Events.Subscribe(observed.Enqueue);

            var effective = host.Services.GetRequiredService<ChatOrchestrationOptions>();
            output.WriteLine(
                $"options processBot={effective.ProcessBotMessages} ignoreSelf={effective.IgnoreSelfMessages} requireMention={effective.RequireMentionOrDirect}");

            var marker = $"natter-live-real-inbound-{Guid.NewGuid():N}";
            // allowed_mentions must include the bot, otherwise Discord keeps the <@id> text but
            // resolves MentionedUsers to empty and the mapper's require-mention gate drops it.
            var input = await rest.CreateMessageAsync(
                Settings.ChannelId,
                $"<@{Settings.BotUserId}> {marker} — answer with one short sentence: what is 9 + 4?",
                mentionUserIds: [Settings.BotUserId]);
            inputId = input.GetProperty("id").GetString()!;
            output.WriteLine($"input id={inputId} marker={marker}");

            await Task.Delay(TimeSpan.FromSeconds(12));
            output.WriteLine($"observed events={observed.Count}");
            foreach (var item in observed)
            {
                if (item is MessageCreatedEvent created)
                {
                    var m = created.Payload.Message;
                    output.WriteLine(
                        $"event message={m.Id} sender={m.Sender.Id}/{m.Sender.Kind} conversation={m.Conversation.Id}/{m.Conversation.Kind} "
                        + $"mentions={(m.Content.Mentions is null ? "null" : string.Join(",", m.Content.Mentions.Select(x => $"{x.Kind}:{x.Id}")))} text={m.Content.Text}");
                }
            }

            var commands = host.Services.GetRequiredService<Natter.Cli.Core.CliCommandRegistry>();
            if (commands.TryGet("ai status", out var statusCommand))
            {
                var console = new StringConsole();
                await statusCommand.HandlerFactory(host.Services).ExecuteAsync(new Natter.Cli.Abstractions.CliInvocation(
                    "ai status",
                    new Dictionary<string, string?> { ["json"] = null },
                    [],
                    host.Services,
                    console));
                output.WriteLine("ai status=" + console.Output.Trim());
            }

            var reply = await LiveDiscordOracle.WaitForSingleReplyAsync(
                rest,
                Settings.ChannelId,
                inputId,
                TimeSpan.FromSeconds(75));
            replyId = reply.GetProperty("id").GetString()!;
            var content = reply.GetProperty("content").GetString()!;

            output.WriteLine($"reply id={replyId} chars={content.Length} text={content}");

            Assert.False(string.IsNullOrWhiteSpace(content));
            Assert.Equal(Settings.BotUserId, reply.GetProperty("author").GetProperty("id").GetString());
            Assert.DoesNotContain(Settings.ApiKey, content, StringComparison.Ordinal);
            Assert.DoesNotContain("reasoning_content", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (replyId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);
            if (inputId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, inputId);

            await host.StopAsync();
        }
    }

    private static string ReadEmbedded(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class StringConsole : Natter.Cli.Abstractions.IConsole
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _error = new();

        public TextWriter Out => _out;

        public TextWriter Error => _error;

        public bool IsInteractive => false;

        public string? ReadLine() => null;

        public string Output => _out.ToString();
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "natter-live-real-inbound-" + Guid.NewGuid().ToString("N"));

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
}
