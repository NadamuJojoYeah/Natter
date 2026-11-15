using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai.DeepSeek;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DeepSeekResponderTests
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

    private static (DeepSeekResponder Responder, FakeChatClient Chat, Source Source, TestClock Clock) CreateResponder(
        int maxHistoryMessages = 20)
    {
        var chat = new FakeChatClient();
        var source = ChatTestData.Source();
        var clock = new TestClock();
        return (Build(chat, maxHistoryMessages), chat, source, clock);
    }

    private static DeepSeekResponder Build(FakeChatClient chat, int maxHistoryMessages)
    {
        var options = new DeepSeekOptions
        {
            SystemPrompt = "system",
            HistoryMaxMessages = maxHistoryMessages,
            HistoryMaxCharacters = 100000,
            FallbackText = "AI is temporarily unavailable.",
        };
        var clock = new TestClock();
        return new DeepSeekResponder(
            chat,
            options,
            new DeepSeekConversationStore(options),
            new DeepSeekConversationGate(options),
            new ChatCommandFactory(clock),
            new FakePluginLog(),
            new DeepSeekMetrics());
    }
}
