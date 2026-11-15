using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
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
/// Layer 5: the only scenario that needs a human. A person writes a message in the configured
/// test channel and the complete stack (gateway inbound → orchestrator → DeepSeek → Discord)
/// answers without any injection. Opt in with <c>NATTER_LIVE_MANUAL=1</c>.
/// </summary>
[Trait("Category", "LiveManual")]
public sealed class ManualGateLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [ManualLiveFact(DisplayName = "Manual: a human message in the channel is answered by the real stack")]
    public async Task Human_message_gets_an_ai_reply()
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
              "ignoreBots": true,
              "ignoreSelf": true,
              "typing": true,
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
              "historyMaxMessages": 10,
              "maxOutputTokens": 300,
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
            IgnoreBots = true,
            IgnoreSelf = true,
            Typing = true,
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
            .AddEmbeddedNpsPlugin(DeepSeekPlugin.Manifest, new DeepSeekPlugin(), deepSeekDir);

        await using var host = builder.Build();
        await host.StartAsync();

        using var rest = new DiscordRestClient(Settings.BotToken);
        var inputId = string.Empty;
        var replyId = string.Empty;

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

            var humanMessages = new ConcurrentQueue<Message>();
            using var subscription = upstream.Events.Subscribe(item =>
            {
                if (item is MessageCreatedEvent created && created.Payload.Message.Sender.Kind == UserKind.Human)
                    humanMessages.Enqueue(created.Payload.Message);
            });

            output.WriteLine(
                $"MANUAL STEP: open Discord channel {Settings.ChannelId} and send a message that mentions the bot "
                + $"(<@{Settings.BotUserId}>). Waiting up to 180 seconds...");

            Message? human = null;
            var waitDeadline = DateTimeOffset.UtcNow.AddSeconds(180);
            while (DateTimeOffset.UtcNow < waitDeadline)
            {
                if (humanMessages.TryDequeue(out human))
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            Assert.NotNull(human);
            inputId = human!.Id;
            output.WriteLine($"human message id={inputId} sender={human.Sender.Id} text={human.Content.Text}");

            var replyDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
            JsonElement? reply = null;
            while (DateTimeOffset.UtcNow < replyDeadline && reply is null)
            {
                var messages = await rest.GetChannelMessagesAsync(Settings.ChannelId, limit: 50);
                reply = messages.EnumerateArray()
                    .Where(item => item.TryGetProperty("referenced_message", out var reference)
                                   && reference.ValueKind == JsonValueKind.Object
                                   && reference.TryGetProperty("id", out var referenceId)
                                   && referenceId.GetString() == inputId)
                    .Select(item => (JsonElement?)item.Clone())
                    .FirstOrDefault();

                if (reply is null)
                    await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Assert.NotNull(reply);
            replyId = reply!.Value.GetProperty("id").GetString()!;
            var content = reply.Value.GetProperty("content").GetString()!;
            output.WriteLine($"ai reply id={replyId} chars={content.Length} text={content}");

            Assert.False(string.IsNullOrWhiteSpace(content));
            Assert.Equal(Settings.BotUserId, reply.Value.GetProperty("author").GetProperty("id").GetString());
        }
        finally
        {
            if (replyId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);

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

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "natter-live-manual-" + Guid.NewGuid().ToString("N"));

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

/// <summary>A fact that additionally requires an operator to act inside Discord.</summary>
public sealed class ManualLiveFactAttribute : FactAttribute
{
    public ManualLiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        var manual = string.Equals(
            Environment.GetEnvironmentVariable("NATTER_LIVE_MANUAL"),
            "1",
            StringComparison.Ordinal);

        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !manual
                ? "Set NATTER_LIVE_MANUAL=1 to run the manual gate (a human must send a message in the test channel)."
                : !settings.HasDiscord || !settings.HasDeepSeek
                    ? "Both DeepSeek and Discord credentials are required."
                    : null;
    }
}
