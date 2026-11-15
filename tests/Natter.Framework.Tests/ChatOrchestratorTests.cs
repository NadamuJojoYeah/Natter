using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class ChatOrchestratorTests
{
    [Fact]
    public async Task Direct_human_messages_trigger_a_reply_and_bots_are_ignored()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var responder = new ReplyResponder(factory, source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions(),
            logger: null);

        var directConversation = ChatTestData.Conversation("dm", ConversationKind.Direct);
        var directMessage = ChatTestData.Message(source, id: "m1", conversation: directConversation);
        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, directMessage, "evt:m1"));

        Assert.Single(upstream.Messages);

        var botMessage = new Message(
            "m2",
            directConversation,
            new User("bot-2", "Other", UserKind.Bot),
            new Content("I am a bot"),
            clock.UtcNow);

        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, botMessage, "evt:m2"));
        Assert.Single(upstream.Messages);
        Assert.Single(responder.Seen);
    }

    [Fact]
    public async Task Guild_messages_require_a_mention_unless_configured_otherwise()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var responder = new ReplyResponder(factory, source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions(),
            logger: null);

        var guild = ChatTestData.Conversation();
        var unmentioned = ChatTestData.Message(source, id: "m3", conversation: guild);
        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, unmentioned, "evt:m3"));
        Assert.Empty(upstream.Messages);

        var mentioned = new Message(
            "m4",
            guild,
            ChatTestData.Human(),
            new Content("hey", Mentions: [new Mention(MentionKind.User, source.SelfId, "Natter")]),
            clock.UtcNow);

        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, mentioned, "evt:m4"));
        Assert.Single(upstream.Messages);
        Assert.Single(responder.Seen);
    }

    [Fact]
    public async Task Interaction_events_are_routed_to_the_responder_and_dispatcher()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(
            source,
            Capabilities.TextOnly with { SlashCommands = true },
            clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var responder = new InteractionResponder(factory, source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions(),
            logger: null);

        var interaction = new Interaction(
            "interaction-1",
            InteractionKind.Slash,
            ChatTestData.Human(),
            ChatTestData.Conversation("555", ConversationKind.Guild),
            clock.UtcNow,
            Command: "ask",
            ReplyToken: "reply-token");

        var chatEvent = new InteractionCreatedEvent
        {
            Id = "evt:interaction",
            Source = source,
            Sequence = 1,
            OccurredAt = clock.UtcNow,
            ReceivedAt = clock.UtcNow,
            Payload = new InteractionPayload(interaction),
        };

        await orchestrator.HandleAsync(chatEvent);

        Assert.Single(responder.Seen);
        Assert.IsType<InteractionCreatedEvent>(responder.Seen[0]);
    }

    [Fact]
    public async Task Orchestrator_ignores_messages_from_the_bot_identity_by_default()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var responder = new ReplyResponder(host.Get<ChatCommandFactory>(), source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions(),
            logger: null);

        var selfMessage = new Message(
            "self-1",
            ChatTestData.Conversation("dm", ConversationKind.Direct),
            new User(source.SelfId, "Natter", UserKind.Human),
            new Content("echo"),
            clock.UtcNow);

        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, selfMessage, "evt:self"));

        Assert.Empty(responder.Seen);
        Assert.Empty(upstream.Messages);
    }

    [Fact]
    public async Task Process_bot_messages_can_be_enabled_explicitly()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var responder = new ReplyResponder(factory, source);
        var orchestrator = new ChatOrchestrator(
            host.Get<IChatEventSource>(),
            host.Get<IChatCommandDispatcher>(),
            responder,
            new ChatOrchestrationOptions { ProcessBotMessages = true },
            logger: null);

        var direct = ChatTestData.Conversation("dm", ConversationKind.Direct);
        var botMessage = new Message(
            "m5",
            direct,
            new User("bot-2", "Other", UserKind.Bot),
            new Content("I am a bot"),
            clock.UtcNow);

        await orchestrator.HandleAsync(ChatTestData.CreatedEvent(source, botMessage, "evt:m5"));

        Assert.Single(upstream.Messages);
    }

    private sealed class InteractionResponder(ChatCommandFactory factory, Source source) : IChatResponder
    {
        public List<ChatEvent> Seen { get; } = [];

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Seen.Add(chatEvent);

            if (chatEvent is not InteractionCreatedEvent interaction)
                return ValueTask.FromResult<ChatCommand?>(null);

            return ValueTask.FromResult<ChatCommand?>(
                factory.Respond(source, interaction.Payload.Interaction, new Content("interaction reply")));
        }
    }

    private sealed class ReplyResponder(ChatCommandFactory factory, Source source) : IChatResponder
    {
        public List<ChatEvent> Seen { get; } = [];

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Seen.Add(chatEvent);
            var message = ((MessageCreatedEvent)chatEvent).Payload.Message;

            return ValueTask.FromResult<ChatCommand?>(factory.Send(
                source,
                message.Conversation,
                new Content("echo"),
                replyTo: message.Id,
                idempotencyKey: $"reply:{chatEvent.Id}"));
        }
    }
}
