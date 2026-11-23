using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Feature.Ai.DeepSeek;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 3: the full Natter composition against real services. Both NPS plugins are loaded,
/// the Discord upstream connects to the real gateway, the DeepSeek responder calls the real
/// API, and the reply is verified through Discord REST. Inbound NLMP events are injected at the
/// NLMP boundary so the test controls exactly one turn (no reply loops, no self-chatter).
/// </summary>
[Trait("Category", "Live")]
public sealed class OrchestrationLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [LiveE2EFact(DisplayName = "E2E: injected message → real DeepSeek → real Discord reply → idempotent replay")]
    public async Task Injected_message_produces_one_real_ai_reply()
    {
        using var temp = new TempDirectory();
        var discordPluginDir = temp.Combine("discord");
        var deepSeekPluginDir = temp.Combine("deepseek");
        var configDir = temp.Combine("config");
        Directory.CreateDirectory(discordPluginDir);
        Directory.CreateDirectory(deepSeekPluginDir);
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(discordPluginDir, "config.schema.json"), ReadEmbedded(
            typeof(DiscordPlugin).Assembly, "Natter.Sample.Plugin.Discord.config.schema.json"));
        File.WriteAllText(Path.Combine(deepSeekPluginDir, "config.schema.json"), ReadEmbedded(
            typeof(DeepSeekPlugin).Assembly, "Natter.Feature.Ai.DeepSeek.config.schema.json"));

        File.WriteAllText(Path.Combine(configDir, "natter.transport.messaging.discord.json"), $$"""
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "{{Settings.BotUserId}}",
              "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
              "guilds": ["{{Settings.GuildId}}"],
              "channels": ["{{Settings.ChannelId}}"],
              "requireMention": true,
              "ignoreBots": true,
              "ignoreSelf": true,
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
            IgnoreBots = true,
            IgnoreSelf = true,
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
            .AddEmbeddedNpsPlugin(DiscordPlugin.Manifest, new DiscordPlugin(transport), discordPluginDir)
            .AddEmbeddedNpsPlugin(DeepSeekPlugin.Manifest, new DeepSeekPlugin(), deepSeekPluginDir);

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
                    + $"error={descriptor.Error} phase={descriptor.ErrorDetail?.Phase} code={descriptor.ErrorDetail?.Code}");
            }

            Assert.All(catalog.Plugins, descriptor => Assert.Equal(PluginState.Activated, descriptor.State));

            var registry = host.Services.GetRequiredService<IChatUpstreamRegistry>();
            await WaitUntilAsync(() => registry.Upstreams.Any(upstream => upstream.IsConnected), TimeSpan.FromSeconds(60));
            var upstream = Assert.Single(registry.Upstreams);
            output.WriteLine($"upstream {upstream.Source.Key} connected={upstream.IsConnected}");

            var input = await rest.CreateMessageAsync(
                Settings.ChannelId,
                $"natter-live-e2e {Guid.NewGuid():N} — please answer this question.");
            inputId = input.GetProperty("id").GetString()!;

            var source = Source.Create("discord", "discord", Settings.BotUserId);
            var conversation = new Conversation(
                Settings.ChannelId,
                ConversationKind.Guild,
                GuildId: Settings.GuildId,
                Title: "live");
            var question = "Answer with one short sentence: what is 17 + 25?";
            var message = new Message(
                inputId,
                conversation,
                new User("900000000000000001", "live-tester", UserKind.Human, "Live Tester"),
                new Content(
                    question,
                    ContentFormat.Markdown,
                    Mentions: [new Mention(MentionKind.User, Settings.BotUserId, "Natter")]),
                DateTimeOffset.UtcNow,
                EditedAt: null);

            message.EnsureValid();

            var publisher = host.Services.GetRequiredService<IChatEventPublisher>();
            var eventId = $"live:message:{inputId}:created";
            publisher.Publish(new MessageCreatedEvent
            {
                Id = eventId,
                Source = source,
                Sequence = 1,
                OccurredAt = message.CreatedAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                Payload = new MessagePayload(message),
            });

            var replies = await WaitForRepliesAsync(rest, inputId, expected: 1, TimeSpan.FromSeconds(60));
            var reply = Assert.Single(replies);
            replyId = reply.GetProperty("id").GetString()!;
            var replyContent = reply.GetProperty("content").GetString()!;
            var replyAuthor = reply.GetProperty("author").GetProperty("id").GetString();

            output.WriteLine($"reply id={replyId} author={replyAuthor} chars={replyContent.Length} text={Truncate(replyContent, 300)}");

            Assert.Equal(Settings.BotUserId, replyAuthor);
            Assert.False(string.IsNullOrWhiteSpace(replyContent));
            Assert.True(replyContent.Length is > 0 and <= 1900);
            Assert.DoesNotContain("reasoning_content", replyContent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Settings.ApiKey, replyContent, StringComparison.Ordinal);

            // Replaying the same NLMP event id must not create a second Discord message.
            publisher.Publish(new MessageCreatedEvent
            {
                Id = eventId,
                Source = source,
                Sequence = 2,
                OccurredAt = message.CreatedAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                Payload = new MessagePayload(message),
            });

            await Task.Delay(TimeSpan.FromSeconds(3));
            var afterReplay = await FindRepliesAsync(rest, inputId);
            output.WriteLine($"repliesAfterReplay={afterReplay.Count}");
            Assert.Single(afterReplay);

            var commands = host.Services.GetRequiredService<CliCommandRegistry>();
            Assert.True(commands.TryGet("ai status", out var statusCommand));
            var console = new StringConsole();
            var exitCode = await statusCommand.HandlerFactory(host.Services).ExecuteAsync(new CliInvocation(
                "ai status",
                new Dictionary<string, string?> { ["json"] = null },
                [],
                host.Services,
                console));
            output.WriteLine($"ai status exit={exitCode} output={console.Output.Trim()}");

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(Settings.ApiKey, console.Output, StringComparison.Ordinal);

            using var status = JsonDocument.Parse(console.Output);
            Assert.Equal("deepseek", status.RootElement.GetProperty("provider").GetString());
            Assert.Equal("deepseek-flash", status.RootElement.GetProperty("model").GetString());
            Assert.True(status.RootElement.GetProperty("metrics").GetProperty("requests").GetInt32() >= 1);
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

    private static async Task<List<JsonElement>> WaitForRepliesAsync(
        DiscordRestClient rest,
        string parentId,
        int expected,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var replies = await FindRepliesAsync(rest, parentId);
            if (replies.Count >= expected)
                return replies;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return await FindRepliesAsync(rest, parentId);
    }

    private static async Task<List<JsonElement>> FindRepliesAsync(DiscordRestClient rest, string parentId)
    {
        var messages = await rest.GetChannelMessagesAsync(LiveSettings.Current.ChannelId, limit: 50);
        var replies = new List<JsonElement>();

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("referenced_message", out var reference)
                || reference.ValueKind != JsonValueKind.Object
                || !reference.TryGetProperty("id", out var referenceId)
                || referenceId.GetString() != parentId)
            {
                continue;
            }

            if (message.TryGetProperty("author", out var author)
                && author.TryGetProperty("bot", out var isBot)
                && isBot.ValueKind == JsonValueKind.True)
            {
                replies.Add(message.Clone());
            }
        }

        return replies;
    }

    private static async Task WaitUntilAsync(Func<bool> probe, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private static string ReadEmbedded(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "natter-live-e2e-" + Guid.NewGuid().ToString("N"));

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

/// <summary>A live fact that needs both providers at once.</summary>
public sealed class LiveE2EFactAttribute : FactAttribute
{
    public LiveE2EFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !settings.HasDiscord || !settings.HasDeepSeek
                ? "Both DeepSeek and Discord credentials are required for the end-to-end test."
                : null;
    }
}
