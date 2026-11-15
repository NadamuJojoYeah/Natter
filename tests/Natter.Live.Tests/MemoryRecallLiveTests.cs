using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>P3 Tier 1 proof: host memory is injected into a real DeepSeek turn.</summary>
[Trait("Category", "Live")]
public sealed class MemoryRecallLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [LiveE2EFact(DisplayName = "P3 memory: explicit host memory is recalled by the real DeepSeek reply")]
    public async Task Explicit_memory_is_recalled_by_real_deepseek()
    {
        var root = Path.Combine(Path.GetTempPath(), "natter-live-memory-" + Guid.NewGuid().ToString("N"));
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
            var registry = host.Services.GetRequiredService<IChatUpstreamRegistry>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline && !registry.Upstreams.Any(upstream => upstream.IsConnected))
                await Task.Delay(250);

            var source = Source.Create("discord", "discord", Settings.BotUserId);
            var conversation = new Conversation(
                Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId);
            var user = new User("900000000000000002", "live-memory-tester", UserKind.Human);
            var memories = host.Services.GetRequiredService<IMemoryStore>();
            await memories.WriteAsync(new MemoryWriteRequest(
                source,
                conversation,
                user,
                MemoryScopeType.Conversation,
                "preference",
                "The user's favorite tea is oolong."));

            var input = await LiveDiscordOracle.CreateInputAsync(rest, Settings.ChannelId, "natter-live-memory");
            inputId = input.GetProperty("id").GetString()!;
            var message = new Message(
                inputId,
                conversation,
                user,
                new Content(
                    "What tea do I like? Answer with the tea name only.",
                    ContentFormat.Markdown,
                    Mentions: [new Mention(MentionKind.User, Settings.BotUserId, "Natter")]),
                DateTimeOffset.UtcNow);
            message.EnsureValid();

            host.Services.GetRequiredService<IChatEventPublisher>().Publish(new MessageCreatedEvent
            {
                Id = $"live:memory:{inputId}:created",
                Source = source,
                Sequence = 1,
                OccurredAt = message.CreatedAt,
                ReceivedAt = DateTimeOffset.UtcNow,
                Payload = new MessagePayload(message),
            });

            var reply = await LiveDiscordOracle.WaitForSingleReplyAsync(
                rest, Settings.ChannelId, inputId, TimeSpan.FromSeconds(60));
            replyId = reply.GetProperty("id").GetString()!;
            var content = reply.GetProperty("content").GetString()!;
            output.WriteLine($"memory reply: {content}");
            Assert.Contains("oolong", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (replyId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);
            if (inputId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, inputId);
            await host.StopAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
