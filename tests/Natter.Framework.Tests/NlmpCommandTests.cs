using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NlmpCommandTests
{
    [Fact]
    public async Task Long_text_is_chunked_and_returns_multiple_message_ids()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = Capabilities.TextOnly with { MaxMessageLength = 10 };

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();

        var ack = await dispatcher.ExecuteAsync(factory.Send(
            source,
            ChatTestData.Conversation(),
            new Content(new string('a', 95))));

        Assert.True(ack.Ok);
        Assert.Equal(10, ack.Result!.MessageIds!.Count);
        Assert.Equal(10, upstream.Messages.Count);
        Assert.All(upstream.Messages, message => Assert.True(message.Content.Text.Length <= 10));
    }

    [Fact]
    public async Task Markdown_is_downgraded_when_the_capability_is_missing()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = Capabilities.TextOnly with { Markdown = false };

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();

        var ack = await dispatcher.ExecuteAsync(factory.Send(
            source,
            ChatTestData.Conversation(),
            new Content("**bold** and [link](https://example.com)", ContentFormat.Markdown)));

        Assert.True(ack.Ok);
        var sent = Assert.Single(upstream.Messages);
        Assert.Equal(ContentFormat.Plain, sent.Content.Format);
        Assert.Equal("bold and link (https://example.com)", sent.Content.Text);
    }

    [Fact]
    public async Task Attachment_urls_become_text_when_attachments_are_unsupported()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = Capabilities.TextOnly with { Attachments = false, Reply = false };

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();

        var ack = await dispatcher.ExecuteAsync(factory.Send(
            source,
            ChatTestData.Conversation(),
            new Content("image:", Attachments: [new Attachment("https://cdn.example/a.png")]),
            replyTo: "older"));

        Assert.True(ack.Ok);
        var sent = Assert.Single(upstream.Messages);
        Assert.Null(sent.Content.Attachments);
        Assert.Contains("https://cdn.example/a.png", sent.Content.Text);
        Assert.Null(sent.ReplyTo);
    }

    [Fact]
    public async Task Unsupported_edit_and_delete_are_rejected_instead_of_guessed()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = Capabilities.TextOnly with { Edit = false, StreamEdit = false, Delete = false };

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();
        var conversation = ChatTestData.Conversation();

        var send = await dispatcher.ExecuteAsync(factory.Send(source, conversation, new Content("hi")));
        var messageId = Assert.Single(send.Result!.MessageIds!);

        var update = await dispatcher.ExecuteAsync(factory.Update(source, conversation, messageId, new Content("edited")));
        Assert.False(update.Ok);
        Assert.Equal(ChatErrorCodes.Unsupported, update.Error!.Code);

        var delete = await dispatcher.ExecuteAsync(factory.Delete(source, conversation, messageId));
        Assert.False(delete.Ok);
        Assert.Equal(ChatErrorCodes.Unsupported, delete.Error!.Code);
        Assert.Single(upstream.Messages);
    }

    [Fact]
    public async Task Typing_is_skipped_quietly_when_the_capability_is_missing()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source, Capabilities.TextOnly with { Typing = false });
        registry.Register(stub);

        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);

        var ack = await dispatcher.ExecuteAsync(new ChatCommandFactory(clock).Typing(source, ChatTestData.Conversation()));

        Assert.True(ack.Ok);
        Assert.Empty(stub.Commands);
    }

    [Fact]
    public async Task Rate_limit_ack_exposes_retry_after_and_retries_reuse_the_idempotency_key()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source);
        registry.Register(stub);

        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);

        var send = new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi"),
            idempotencyKey: "run-42");

        stub.NextAck = ChatAck.Failure(
            send,
            ChatError.Create(ChatErrorCodes.RateLimited, "Discord rate limit exceeded.", retryAfterMs: 1200));

        var first = await dispatcher.ExecuteAsync(send);
        Assert.False(first.Ok);
        Assert.Equal(ChatErrorCodes.RateLimited, first.Error!.Code);
        Assert.True(first.Error.Retryable);
        Assert.Equal(1200, first.Error.RetryAfterMs);

        stub.NextAck = null;
        var retry = await dispatcher.ExecuteAsync(send);
        Assert.True(retry.Ok);

        var cached = await dispatcher.ExecuteAsync(send);
        Assert.True(cached.Ok);
        Assert.Equal(2, stub.Commands.Count);
    }

    [Fact]
    public async Task The_same_idempotency_key_does_not_send_twice()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();

        var send = factory.Send(source, ChatTestData.Conversation(), new Content("once"), idempotencyKey: "run-7");

        var first = await dispatcher.ExecuteAsync(send);
        var second = await dispatcher.ExecuteAsync(send);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(first.Result!.MessageIds, second.Result!.MessageIds);
        Assert.Single(upstream.Messages);
    }

    [Fact]
    public async Task Failed_chunk_ack_reports_already_delivered_message_ids()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source, Capabilities.TextOnly with { MaxMessageLength = 5 });
        registry.Register(stub);

        stub.AckFactory = (command, count) => count == 1
            ? ChatAck.Success(command, ["m-1"], clock.UtcNow)
            : ChatAck.Failure(command, ChatError.Create(ChatErrorCodes.Unavailable, "upstream down"));

        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);

        var ack = await dispatcher.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content(new string('b', 12))));

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.Unavailable, ack.Error!.Code);
        Assert.NotNull(ack.Error.Details);
        Assert.True(ack.Error.Details!.ContainsKey("deliveredMessageIds"));
    }

    [Fact]
    public async Task Everyone_mentions_are_blocked_by_the_default_outbound_policy()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source, Capabilities.TextOnly with { Mentions = true });
        registry.Register(stub);

        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);

        var ack = await dispatcher.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hello", Mentions: [new Mention(MentionKind.Everyone)])));

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.InvalidRequest, ack.Error!.Code);
        Assert.Contains("everyone", ack.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stub.Commands);
    }


    [Fact]
    public async Task A_single_final_edit_is_allowed_when_streaming_is_disabled()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var capabilities = Capabilities.TextOnly with { Edit = true, StreamEdit = false };

        await using var host = await TestHost.CreateAsync();
        var upstream = new InMemoryChatUpstream(source, capabilities, clock);
        host.Get<IChatUpstreamRegistry>().Register(upstream);
        await upstream.ConnectAsync();

        var factory = host.Get<ChatCommandFactory>();
        var dispatcher = host.Get<IChatCommandDispatcher>();
        var conversation = ChatTestData.Conversation();

        var send = await dispatcher.ExecuteAsync(factory.Send(source, conversation, new Content("draft")));
        var messageId = Assert.Single(send.Result!.MessageIds!);

        var update = await dispatcher.ExecuteAsync(factory.Update(source, conversation, messageId, new Content("final")));

        Assert.True(update.Ok);
        Assert.False(capabilities.SupportsStreaming);
        Assert.Equal("final", Assert.Single(upstream.Messages).Content.Text);
    }

    [Fact]
    public async Task Send_without_idempotency_key_is_rejected()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source);
        registry.Register(stub);

        var dispatcher = new ChatCommandDispatcher(
            registry,
            new ChatIdempotencyCache(clock: () => clock.UtcNow),
            clock);

        var command = new MessageSendCommand
        {
            Id = "cmd:no-key",
            Source = source,
            IssuedAt = clock.UtcNow,
            Payload = new MessageSendPayload(ChatTestData.Conversation(), new Content("hi")),
        };

        var ack = await dispatcher.ExecuteAsync(command);

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.InvalidRequest, ack.Error!.Code);
    }
}
