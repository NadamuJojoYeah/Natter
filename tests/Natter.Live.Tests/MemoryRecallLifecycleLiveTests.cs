using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Core;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// P3-051 / P3-052: after forgetting a memory the real reply must not mention it, and an
/// un-forgotten memory must still be recalled after a host restart (SQLite state directory).
/// </summary>
[Trait("Category", "Live")]
public sealed class MemoryRecallLifecycleLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [LiveE2EFact(DisplayName = "P3 memory: forget stops recall in the real DeepSeek reply")]
    public async Task Forget_stops_recall()
    {
        var root = Path.Combine(Path.GetTempPath(), "natter-live-memory-forget-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        LiveDeployment.StageInto(root, output);

        await using var host = await StartHostAsync(root, stateDir);
        using var rest = new DiscordRestClient(Settings.BotToken);
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId);
        var user = new User("900000000000000003", "live-forget-tester", UserKind.Human);

        var inputId = string.Empty;
        var replyId = string.Empty;
        var secondInputId = string.Empty;
        var secondReplyId = string.Empty;

        try
        {
            await WaitConnectedAsync(host);

            var memories = host.Services.GetRequiredService<IMemoryStore>();
            await memories.WriteAsync(new MemoryWriteRequest(
                source, conversation, user, MemoryScopeType.Conversation,
                "preference", "The user's favorite tea is oolong."));

            var first = await AskAsync(host, rest, source, conversation, user, "What tea do I like? Answer with one word.");
            inputId = first.InputId;
            replyId = first.ReplyId;
            output.WriteLine($"before forget: {first.Content}");
            Assert.Contains("oolong", first.Content, StringComparison.OrdinalIgnoreCase);

            var deleted = await memories.DeleteAsync(new MemoryDeleteRequest(
                source, conversation, user, MemoryId: null, Text: "The user's favorite tea is oolong."));
            Assert.True(deleted, "forget should delete the conversation-scope memory");
            output.WriteLine($"forget deleted={deleted}");

            var second = await AskAsync(host, rest, source, conversation, user, "What tea do I like? If you do not know, reply exactly: unknown");
            secondInputId = second.InputId;
            secondReplyId = second.ReplyId;
            output.WriteLine($"after forget: {second.Content}");
            Assert.DoesNotContain("oolong", second.Content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var id in new[] { replyId, secondReplyId, inputId, secondInputId })
                if (!string.IsNullOrEmpty(id))
                    await rest.TryDeleteMessageAsync(Settings.ChannelId, id);
            await host.StopAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [LiveE2EFact(DisplayName = "P3 memory: memory survives a host restart and is still recalled")]
    public async Task Memory_survives_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "natter-live-memory-restart-" + Guid.NewGuid().ToString("N"));
        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        LiveDeployment.StageInto(root, output);

        var source = Source.Create("discord", "discord", Settings.BotUserId);
        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId);
        var user = new User("900000000000000004", "live-restart-tester", UserKind.Human);
        using var rest = new DiscordRestClient(Settings.BotToken);
        var inputId = string.Empty;
        var replyId = string.Empty;

        try
        {
            await using (var host = await StartHostAsync(root, stateDir))
            {
                await WaitConnectedAsync(host);
                var memories = host.Services.GetRequiredService<IMemoryStore>();
                await memories.WriteAsync(new MemoryWriteRequest(
                    source, conversation, user, MemoryScopeType.Conversation,
                    "preference", "The user's favorite tea is oolong."));
                await host.StopAsync();
            }

            await using (var restarted = await StartHostAsync(root, stateDir))
            {
                await WaitConnectedAsync(restarted);
                var items = await restarted.Services.GetRequiredService<IMemoryStore>()
                    .SearchAsync(new MemoryQuery(source, conversation, user, 50));
                output.WriteLine($"memories after restart={items.Count}");
                Assert.NotEmpty(items);

                var reply = await AskAsync(restarted, rest, source, conversation, user, "What tea do I like? Answer with one word.");
                inputId = reply.InputId;
                replyId = reply.ReplyId;
                output.WriteLine($"after restart: {reply.Content}");
                Assert.Contains("oolong", reply.Content, StringComparison.OrdinalIgnoreCase);
                await restarted.StopAsync();
            }
        }
        finally
        {
            if (replyId.Length > 0) await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);
            if (inputId.Length > 0) await rest.TryDeleteMessageAsync(Settings.ChannelId, inputId);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<NatterHost> StartHostAsync(string root, string stateDir)
    {
        var secrets = new InMemorySecretProvider(new Dictionary<string, string>
        {
            ["DISCORD_BOT_TOKEN"] = Settings.BotToken,
            ["DEEPSEEK_API_KEY"] = Settings.ApiKey,
        });

        var host = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options => options.StateDirectory = stateDir)
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddPluginDirectory(root)
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task WaitConnectedAsync(NatterHost host)
    {
        var registry = host.Services.GetRequiredService<IChatUpstreamRegistry>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline && !registry.Upstreams.Any(upstream => upstream.IsConnected))
            await Task.Delay(250);
        Assert.Contains(registry.Upstreams, upstream => upstream.IsConnected);
    }

    private static async Task<(string InputId, string ReplyId, string Content)> AskAsync(
        NatterHost host,
        DiscordRestClient rest,
        Source source,
        Conversation conversation,
        User user,
        string question)
    {
        var input = await LiveDiscordOracle.CreateInputAsync(rest, Settings.ChannelId, "natter-live-memory-cycle");
        var inputId = input.GetProperty("id").GetString()!;
        var message = new Message(
            inputId,
            conversation,
            user,
            new Content(
                question,
                ContentFormat.Markdown,
                Mentions: [new Mention(MentionKind.User, Settings.BotUserId, "Natter")]),
            DateTimeOffset.UtcNow);
        message.EnsureValid();

        host.Services.GetRequiredService<IChatEventPublisher>().Publish(new MessageCreatedEvent
        {
            Id = $"live:memory-cycle:{inputId}:created",
            Source = source,
            Sequence = 1,
            OccurredAt = message.CreatedAt,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new MessagePayload(message),
        });

        var reply = await LiveDiscordOracle.WaitForSingleReplyAsync(
            rest, Settings.ChannelId, inputId, TimeSpan.FromSeconds(75));
        return (inputId, reply.GetProperty("id").GetString()!, reply.GetProperty("content").GetString()!);
    }
}
