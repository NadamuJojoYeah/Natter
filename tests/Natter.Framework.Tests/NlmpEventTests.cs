using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NlmpEventTests
{
    [Fact]
    public async Task Message_lifecycle_produces_created_updated_and_deleted_events()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = new Capabilities
        {
            ConversationKinds = [ConversationKind.Guild],
            Reply = true,
            Edit = true,
            Delete = true,
            Attachments = true,
            Mentions = true,
            MaxMessageLength = 100,
        };

        await using var host = await TestHost.CreateAsync();
        var registry = host.Get<IChatUpstreamRegistry>();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        registry.Register(upstream);

        var captured = new List<ChatEvent>();
        using var subscription = host.Get<IChatEventSource>().Events.Subscribe(new CaptureObserver<ChatEvent>(captured));

        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();
        var conversation = ChatTestData.Conversation();

        var send = factory.Send(
            source,
            conversation,
            new Content(
                "hello",
                Attachments: [new Attachment("https://cdn.example/a.png", Kind: AttachmentKind.Image)],
                Mentions: [new Mention(MentionKind.User, source.SelfId, "Natter")]),
            replyTo: "older-message");

        var sendAck = await dispatcher.ExecuteAsync(send);
        Assert.True(sendAck.Ok, sendAck.Error?.Message);
        var messageId = Assert.Single(sendAck.Result!.MessageIds!);

        Assert.True((await dispatcher.ExecuteAsync(factory.Update(source, conversation, messageId, new Content("hello edited")))).Ok);
        Assert.True((await dispatcher.ExecuteAsync(factory.Delete(source, conversation, messageId))).Ok);

        Assert.Empty(upstream.Messages);
        Assert.Collection(
            captured,
            item => Assert.IsType<UpstreamStatusEvent>(item),
            item =>
            {
                var created = Assert.IsType<MessageCreatedEvent>(item);
                Assert.Equal(messageId, created.Payload.Message.Id);
                Assert.Equal("older-message", created.Payload.Message.ReplyTo);
                Assert.Single(created.Payload.Message.Content.Attachments!);
                Assert.Single(created.Payload.Message.Content.Mentions!);
            },
            item =>
            {
                var updated = Assert.IsType<MessageUpdatedEvent>(item);
                Assert.Equal("hello edited", updated.Payload.Message.Content.Text);
                Assert.NotNull(updated.Payload.Message.EditedAt);
            },
            item =>
            {
                var deleted = Assert.IsType<MessageDeletedEvent>(item);
                Assert.Equal(messageId, deleted.Payload.MessageId);
                Assert.Equal(conversation.Id, deleted.Payload.Conversation.Id);
            });
    }

    [Fact]
    public async Task Two_bots_with_the_same_conversation_id_do_not_cross_talk()
    {
        var clock = new TestClock();
        var firstSource = ChatTestData.Source(selfId: "bot-a");
        var secondSource = ChatTestData.Source(selfId: "bot-b");
        var capabilities = Capabilities.TextOnly;

        await using var host = await TestHost.CreateAsync();
        var registry = host.Get<IChatUpstreamRegistry>();
        var first = new InMemoryChatUpstream(firstSource, capabilities, clock);
        var second = new InMemoryChatUpstream(secondSource, capabilities, clock);
        registry.Register(first);
        registry.Register(second);
        await first.ConnectAsync();
        await second.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();
        var conversation = ChatTestData.Conversation("same-channel-id");

        Assert.True((await dispatcher.ExecuteAsync(factory.Send(firstSource, conversation, new Content("to first")))).Ok);
        Assert.True((await dispatcher.ExecuteAsync(factory.Send(secondSource, conversation, new Content("to second")))).Ok);

        Assert.Single(first.Messages);
        Assert.Single(second.Messages);
        Assert.Equal("to first", first.Messages[0].Content.Text);
        Assert.Equal("to second", second.Messages[0].Content.Text);
        Assert.Equal(firstSource.SelfId, first.Messages[0].Sender.Id);
        Assert.Equal(secondSource.SelfId, second.Messages[0].Sender.Id);
    }

    [Fact]
    public async Task Ingress_deduplicates_by_event_id_and_message_creation_key()
    {
        await using var host = await TestHost.CreateAsync();
        var ingress = host.Get<IChatEventIngress>();

        var source = ChatTestData.Source();
        var message = ChatTestData.Message(source);
        var created = ChatTestData.CreatedEvent(source, message);

        Assert.True(ingress.TryPublish(created));
        Assert.False(ingress.TryPublish(created));

        // A replay that derives a different envelope id must still be rejected by the
        // recommended (source, conversation, message, "created") key.
        Assert.False(ingress.TryPublish(created with { Id = "evt:replayed-with-other-id" }));

        var otherConversation = ChatTestData.Message(source, id: "988", conversation: ChatTestData.Conversation("other"));
        Assert.True(ingress.TryPublish(ChatTestData.CreatedEvent(source, otherConversation, "evt:3")));
    }

    [Fact]
    public async Task Deduplicator_expires_after_the_configured_window()
    {
        var clock = new TestClock();
        var deduplicator = new ChatEventDeduplicator(TimeSpan.FromMinutes(10), clock: () => clock.UtcNow);
        var source = ChatTestData.Source();
        var created = ChatTestData.CreatedEvent(source, ChatTestData.Message(source));

        Assert.True(deduplicator.TryAccept(created));
        Assert.False(deduplicator.TryAccept(created));

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.True(deduplicator.TryAccept(created));
    }
}
