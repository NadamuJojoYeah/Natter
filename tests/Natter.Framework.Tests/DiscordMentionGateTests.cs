using Natter.Application.Chat;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Regression test for BUG-003: a real guild message that mentions the bot must keep a
/// structured self-mention in NLMP so the orchestrator's mention gate can see it. Outbound
/// self-mention filtering belongs to the Discord upstream, not to the inbound mapper.
/// </summary>
public sealed class DiscordMentionGateTests
{
    [Fact]
    public async Task Guild_self_mention_survives_mapping_and_reaches_the_responder()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", RequireMention = true };
        var mapper = new DiscordEventMapper(source, options);

        var mapped = mapper.MapMessage(
            new DiscordMessage(
                "m1",
                new DiscordChannel("guild-channel", "guild", "general"),
                new DiscordGuild("guild", "Guild"),
                new DiscordUser("human", "YG", "YG", IsBot: false),
                "<@bot> hello",
                clock.UtcNow,
                EditedAt: null,
                ReplyToMessageId: null,
                Attachments: [],
                MentionedUsers: [new DiscordUser("bot", "bot", null, IsBot: true)],
                MentionEveryone: false),
            isEdit: false);

        Assert.NotNull(mapped);
        Assert.Equal("hello", mapped!.Content.Text);
        Assert.Contains(
            mapped.Content.Mentions!,
            mention => mention.Kind == MentionKind.User && mention.Id == "bot");

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var responder = new RecordingResponder(host.Get<ChatCommandFactory>(), source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions(),
            logger: null);

        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, mapped, "evt:guild-mention"));

        Assert.Single(responder.Seen);
        Assert.Single(upstream.Messages);
    }

    [Fact]
    public void Everyone_alone_is_not_a_bot_mention()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var mapper = new DiscordEventMapper(source, new DiscordOptions { SelfId = "bot", RequireMention = true });

        var mapped = mapper.MapMessage(
            new DiscordMessage(
                "m2",
                new DiscordChannel("guild-channel", "guild", "general"),
                new DiscordGuild("guild", "Guild"),
                new DiscordUser("human", "YG", "YG", IsBot: false),
                "@everyone hello",
                clock.UtcNow,
                MentionedUsers: [],
                MentionEveryone: true),
            isEdit: false);

        Assert.Null(mapped);
    }

    private sealed class RecordingResponder(ChatCommandFactory factory, Source source) : IChatResponder
    {
        public List<ChatEvent> Seen { get; } = [];

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Seen.Add(chatEvent);
            return ValueTask.FromResult<ChatCommand?>(factory.Send(
                source,
                ChatTestData.Conversation("guild-channel", ConversationKind.Guild),
                new Content("echo")));
        }
    }
}
