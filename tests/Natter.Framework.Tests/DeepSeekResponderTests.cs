using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai;
using Natter.Feature.Ai.DeepSeek;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class AiChatResponderTests
{
    [Fact]
    public async Task Message_event_becomes_a_reply_with_history_and_reply_to()
    {
        var (responder, chat, source, clock) = CreateResponder();
        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);
        var message = ChatTestData.Message(source, id: "m1", conversation: conversation, text: "hello");
        var chatEvent = ChatTestData.CreatedEvent(source, message, "evt:m1");

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(chatEvent));

        Assert.Equal("answer", command.Payload.Content.Text);
        Assert.Equal("m1", command.Payload.ReplyTo);
        Assert.Equal($"ai:message:{source.Key}:m1", command.IdempotencyKey);

        var sent = Assert.Single(chat.Calls);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("hello", sent[^1].Text);

        var second = ChatTestData.Message(source, id: "m2", conversation: conversation, text: "again");
        await responder.RespondAsync(ChatTestData.CreatedEvent(source, second, "evt:m2"));

        var secondCall = chat.Calls[^1];
        Assert.Equal("hello", secondCall[1].Text);
        Assert.Equal("answer", secondCall[2].Text);
        Assert.Equal("again", secondCall[^1].Text);
    }

    [Fact]
    public async Task Slash_prompt_becomes_an_interaction_response()
    {
        var (responder, chat, source, clock) = CreateResponder();
        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);
        var interaction = new Interaction(
            "interaction-1",
            InteractionKind.Slash,
            ChatTestData.Human(),
            conversation,
            clock.UtcNow,
            Command: "ask",
            Options: new Dictionary<string, JsonElement>
            {
                ["prompt"] = JsonSerializer.SerializeToElement("hello from slash"),
            },
            ReplyToken: "token");

        var command = Assert.IsType<InteractionRespondCommand>(await responder.RespondAsync(new InteractionCreatedEvent
        {
            Id = "evt:interaction",
            Source = source,
            OccurredAt = clock.UtcNow,
            ReceivedAt = clock.UtcNow,
            Payload = new InteractionPayload(interaction),
        }));

        Assert.Equal("answer", command.Payload.Content.Text);
        Assert.Equal("interaction-1", command.Payload.InteractionId);
        Assert.False(command.Payload.Ephemeral);
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task Missing_slash_prompt_returns_an_ephemeral_usage_hint_without_calling_the_model()
    {
        var (responder, chat, source, clock) = CreateResponder();
        var interaction = new Interaction(
            "interaction-2",
            InteractionKind.Slash,
            ChatTestData.Human(),
            ChatTestData.Conversation("dm", ConversationKind.Direct),
            clock.UtcNow,
            Command: "ask",
            ReplyToken: "token");

        var command = Assert.IsType<InteractionRespondCommand>(await responder.RespondAsync(new InteractionCreatedEvent
        {
            Id = "evt:interaction-2",
            Source = source,
            OccurredAt = clock.UtcNow,
            ReceivedAt = clock.UtcNow,
            Payload = new InteractionPayload(interaction),
        }));

        Assert.Contains("prompt", command.Payload.Content.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(command.Payload.Ephemeral);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Duplicate_event_ids_are_idempotent()
    {
        var (responder, chat, source, _) = CreateResponder();
        var message = ChatTestData.Message(source, id: "m1", text: "hello");
        var chatEvent = ChatTestData.CreatedEvent(source, message, "evt:same");

        var first = await responder.RespondAsync(chatEvent);
        var second = await responder.RespondAsync(chatEvent);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task Same_conversation_is_serialized_by_the_gate()
    {
        var (responder, chat, source, clock) = CreateResponder();
        chat.AsyncResponseFactory = async _ =>
        {
            await Task.Delay(50);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
        };

        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);
        var first = ChatTestData.Message(source, id: "m1", conversation: conversation, text: "one");
        var second = ChatTestData.Message(source, id: "m2", conversation: conversation, text: "two");

        await Task.WhenAll(
            responder.RespondAsync(ChatTestData.CreatedEvent(source, first, "evt:1")).AsTask(),
            responder.RespondAsync(ChatTestData.CreatedEvent(source, second, "evt:2")).AsTask());

        Assert.Equal(1, chat.MaxActive);
    }

    [Fact]
    public async Task History_is_trimmed_to_the_configured_message_limit()
    {
        var (responder, chat, source, _) = CreateResponder(maxHistoryMessages: 2);
        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);

        await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m1", conversation: conversation, text: "one"),
            "evt:1"));
        await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m2", conversation: conversation, text: "two"),
            "evt:2"));
        await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m3", conversation: conversation, text: "three"),
            "evt:3"));

        var lastCall = chat.Calls[^1];
        Assert.Equal(4, lastCall.Count);
        Assert.Equal("two", lastCall[1].Text);
        Assert.Equal("answer", lastCall[2].Text);
        Assert.Equal("three", lastCall[3].Text);
    }

    [Fact]
    public async Task Timeout_produces_a_fallback_and_auth_error_does_not_reply()
    {
        var (responder, _, source, _) = CreateResponder();
        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);

        var fallbackResponder = responder;
        var fake = new FakeChatClient
        {
            ResponseFactory = _ => throw new DeepSeekException(DeepSeekErrorKind.Timeout, "timed out"),
        };
        var timeoutResponder = Build(fake, maxHistoryMessages: 20);
        var timeoutCommand = Assert.IsType<MessageSendCommand>(await timeoutResponder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m1", conversation: conversation, text: "one"),
            "evt:timeout")));
        Assert.Contains("temporarily", timeoutCommand.Payload.Content.Text, StringComparison.OrdinalIgnoreCase);

        var authFake = new FakeChatClient
        {
            ResponseFactory = _ => throw new DeepSeekException(DeepSeekErrorKind.Authentication, "denied"),
        };
        var authResponder = Build(authFake, maxHistoryMessages: 20);
        var authCommand = await authResponder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m2", conversation: conversation, text: "two"),
            "evt:auth"));
        Assert.Null(authCommand);
    }

    [Fact]
    public async Task Clipped_provider_output_is_retried_and_never_reaches_history()
    {
        var calls = 0;
        var chat = new FakeChatClient
        {
            AsyncResponseFactory = _ => Task.FromResult(++calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response clipped>"))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "complete answer"))),
        };
        var responder = Build(chat, 20);
        var source = ChatTestData.Source();
        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "m-clip"), "evt:clip")));
        Assert.Equal("complete answer", command.Payload.Content.Text);
        Assert.Equal(2, calls);

        var alwaysClipped = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response clipped>")),
        };
        var fallback = await Build(alwaysClipped, 20).RespondAsync(
            ChatTestData.CreatedEvent(source, ChatTestData.Message(source, id: "m-clip2"), "evt:clip2"));
        Assert.Equal("AI is temporarily unavailable.", Assert.IsType<MessageSendCommand>(fallback).Payload.Content.Text);
    }

    [Theory]
    [InlineData("<response>hello</response>", "hello")]
    [InlineData("  <response>\n hello \n</response>  ", "hello")]
    [InlineData("<RESPONSE>Hello</Response>", "Hello")]
    [InlineData("<response><response>hello</response></response>", "hello")]
    [InlineData("<response>我可以帮你什么？ </response>", "我可以帮你什么？")]
    [InlineData("<response>hello", "hello")]
    [InlineData("hello</response>", "hello")]
    [InlineData("plain answer", "plain answer")]
    [InlineData("before <response>inside</response> after", "before <response>inside</response> after")]
    public async Task Provider_response_wrapper_is_stripped_from_the_reply_boundaries(
        string providerText,
        string expected)
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, providerText)),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(
                source,
                id: "m-wrap",
                conversation: ChatTestData.Conversation("dm", ConversationKind.Direct),
                text: "hi"),
            "evt:wrap")));

        Assert.Equal(expected, command.Payload.Content.Text);
    }

    [Fact]
    public async Task Markdown_and_code_blocks_keep_response_tags_in_the_body()
    {
        const string body = "Use it like this:\n\n```html\n<response>example</response>\n```\n\nDone.";
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, body)),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m-code", text: "format it"),
            "evt:code")));

        Assert.Equal(body, command.Payload.Content.Text);
    }

    [Fact]
    public async Task Code_fence_at_the_start_is_not_treated_as_a_wrapper()
    {
        const string body = "```html\n<response>x</response>\n```\nplain text";
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, body)),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m-fence", text: "show sample"),
            "evt:fence")));

        Assert.Equal(body, command.Payload.Content.Text);
    }

    [Fact]
    public async Task Tag_only_output_falls_back_to_the_empty_response_text()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response>   </response>")),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(ChatTestData.CreatedEvent(
            source,
            ChatTestData.Message(source, id: "m-empty", text: "hi"),
            "evt:empty")));

        Assert.Equal("I could not produce a response.", command.Payload.Content.Text);
    }

    [Fact]
    public async Task Cleaned_reply_is_what_gets_stored_in_history()
    {
        var chat = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response>clean</response>")),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();
        var conversation = ChatTestData.Conversation("dm", ConversationKind.Direct);

        await responder.RespondAsync(ChatTestData.CreatedEvent(
            source, ChatTestData.Message(source, id: "m-h1", conversation: conversation, text: "one"), "evt:h1"));
        await responder.RespondAsync(ChatTestData.CreatedEvent(
            source, ChatTestData.Message(source, id: "m-h2", conversation: conversation, text: "two"), "evt:h2"));

        var secondCall = chat.Calls[^1];
        Assert.Equal("clean", secondCall[2].Text);
        Assert.Equal("two", secondCall[^1].Text);
    }

    [Fact]
    public async Task Clipped_detection_still_works_when_the_output_is_wrapped()
    {
        var calls = 0;
        var chat = new FakeChatClient
        {
            AsyncResponseFactory = _ => Task.FromResult(++calls == 1
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response><response clipped></response>"))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "<response>complete answer</response>"))),
        };
        var responder = Build(chat, maxHistoryMessages: 20);
        var source = ChatTestData.Source();

        var command = Assert.IsType<MessageSendCommand>(await responder.RespondAsync(ChatTestData.CreatedEvent(
            source, ChatTestData.Message(source, id: "m-clip-wrap", text: "hi"), "evt:clip-wrap")));

        Assert.Equal("complete answer", command.Payload.Content.Text);
        Assert.Equal(2, calls);
    }

    private static (AiChatResponder Responder, FakeChatClient Chat, Source Source, TestClock Clock) CreateResponder(
        int maxHistoryMessages = 20)
    {
        var chat = new FakeChatClient();
        var source = ChatTestData.Source();
        var clock = new TestClock();
        return (Build(chat, maxHistoryMessages), chat, source, clock);
    }

    private static AiChatResponder Build(FakeChatClient chat, int maxHistoryMessages)
    {
        var options = new AiResponderOptions
        {
            SystemPrompt = "system",
            HistoryMaxMessages = maxHistoryMessages,
            HistoryMaxCharacters = 100000,
            FallbackText = "AI is temporarily unavailable.",
        };
        var clock = new TestClock();
        return new AiChatResponder(
            chat,
            options,
            new AiConversationStore(options),
            new AiConversationGate(options),
            new ChatCommandFactory(clock),
            new FakePluginLog());
    }
}
