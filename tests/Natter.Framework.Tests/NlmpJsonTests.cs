using System.Text.Json;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NlmpJsonTests
{
    private const string MessageCreatedJson = """
        {
          "version": "0.1",
          "id": "evt:1",
          "type": "message.created",
          "source": { "protocol": "satori", "platform": "discord", "selfId": "bot-1" },
          "sequence": 1,
          "occurredAt": "2026-11-14T14:00:00Z",
          "receivedAt": "2026-11-14T14:00:00.120Z",
          "payload": {
            "message": {
              "id": "987",
              "conversation": { "id": "555", "kind": "mystery" },
              "sender": { "id": "333", "name": "yg", "kind": "cyborg" },
              "content": { "text": "hi", "format": "html" },
              "createdAt": "2026-11-14T14:00:00Z"
            }
          },
          "futureField": { "x": 1 }
        }
        """;

    [Fact]
    public void Unknown_event_types_are_ignored()
    {
        const string json = """
            {
              "version": "0.1",
              "id": "evt:2",
              "type": "future.event",
              "source": { "protocol": "satori", "platform": "discord", "selfId": "bot-1" },
              "occurredAt": "2026-11-14T14:00:00Z",
              "receivedAt": "2026-11-14T14:00:00Z",
              "payload": { "x": 1 }
            }
            """;

        Assert.Null(NlmpJson.DeserializeEvent(json));
        Assert.Null(NlmpJson.DeserializeCommand(json));
    }

    [Fact]
    public void Unknown_fields_and_enum_values_do_not_break_binding()
    {
        var chatEvent = Assert.IsType<MessageCreatedEvent>(NlmpJson.DeserializeEvent(MessageCreatedJson));
        var message = chatEvent.Payload.Message;

        Assert.Equal("987", message.Id);
        Assert.Equal(ConversationKind.Unknown, message.Conversation.Kind);
        Assert.Equal(UserKind.Unknown, message.Sender.Kind);
        Assert.Equal(ContentFormat.Plain, message.Content.Format);
        Assert.Equal(TimeSpan.Zero, chatEvent.OccurredAt.Offset);
    }

    [Fact]
    public void Version_guards_reject_missing_and_major_mismatches_but_accept_minor_updates()
    {
        Assert.Throws<NlmpVersionException>(() => NlmpJson.DeserializeEvent("""{ "id": "evt:1" }"""));

        var major = MessageCreatedJson.Replace("\"version\": \"0.1\"", "\"version\": \"1.0\"", StringComparison.Ordinal);
        Assert.Throws<NlmpVersionException>(() => NlmpJson.DeserializeEvent(major));

        var minor = MessageCreatedJson.Replace("\"version\": \"0.1\"", "\"version\": \"0.9\"", StringComparison.Ordinal);
        Assert.NotNull(NlmpJson.DeserializeEvent(minor));
    }

    [Fact]
    public void Events_commands_and_acks_round_trip_through_the_json_projection()
    {
        var source = ChatTestData.Source();
        var conversation = ChatTestData.Conversation();
        var sentAt = new DateTimeOffset(2026, 11, 14, 22, 0, 0, TimeSpan.FromHours(8));

        var message = new Message(
            "987",
            conversation,
            ChatTestData.Human(),
            new Content(
                "hello",
                ContentFormat.Plain,
                [new Attachment("https://cdn.example/a.png", FileName: "a.png", Kind: AttachmentKind.Image)],
                [new Mention(MentionKind.User, "bot-1", "Natter")]),
            sentAt,
            ReplyTo: "986");

        var created = ChatTestData.CreatedEvent(source, message) with { OccurredAt = sentAt, ReceivedAt = sentAt };
        var eventJson = NlmpJson.SerializeEvent(created);

        Assert.Contains("\"occurredAt\":\"2026-11-14T14:00:00Z\"", eventJson);
        Assert.Contains("\"replyTo\":\"986\"", eventJson);

        var eventRoundTrip = Assert.IsType<MessageCreatedEvent>(NlmpJson.DeserializeEvent(eventJson));
        Assert.Equal("987", eventRoundTrip.Payload.Message.Id);
        Assert.Equal("986", eventRoundTrip.Payload.Message.ReplyTo);
        Assert.Single(eventRoundTrip.Payload.Message.Content.Attachments!);
        Assert.Single(eventRoundTrip.Payload.Message.Content.Mentions!);
        Assert.Equal(TimeSpan.Zero, eventRoundTrip.Payload.Message.CreatedAt.Offset);

        var send = new MessageSendCommand
        {
            Id = "cmd:1",
            Source = source,
            IssuedAt = sentAt,
            IdempotencyKey = "run-1",
            Payload = new MessageSendPayload(conversation, new Content("hi"), ReplyTo: "986"),
        };

        var commandJson = NlmpJson.SerializeCommand(send);
        var commandRoundTrip = Assert.IsType<MessageSendCommand>(NlmpJson.DeserializeCommand(commandJson));

        Assert.Equal("run-1", commandRoundTrip.IdempotencyKey);
        Assert.Equal("hi", commandRoundTrip.Payload.Content.Text);
        Assert.Equal("986", commandRoundTrip.Payload.ReplyTo);

        var ack = ChatAck.Failure(
            send,
            ChatError.Create(ChatErrorCodes.RateLimited, "slow down", retryAfterMs: 1200));
        var ackRoundTrip = NlmpJson.DeserializeAck(NlmpJson.SerializeAck(ack));

        Assert.False(ackRoundTrip.Ok);
        Assert.Equal(ChatErrorCodes.RateLimited, ackRoundTrip.Error!.Code);
        Assert.True(ackRoundTrip.Error.Retryable);
        Assert.Equal(1200, ackRoundTrip.Error.RetryAfterMs);
    }

    [Fact]
    public void Json_null_and_missing_optional_fields_are_equivalent()
    {
        var withoutAnswer = MessageCreatedJson.Replace(
            "\"createdAt\": \"2026-11-14T14:00:00Z\"",
            "\"createdAt\": \"2026-11-14T14:00:00Z\", \"replyTo\": null, \"editedAt\": null",
            StringComparison.Ordinal);

        var chatEvent = Assert.IsType<MessageCreatedEvent>(NlmpJson.DeserializeEvent(withoutAnswer));
        Assert.Null(chatEvent.Payload.Message.ReplyTo);
        Assert.Null(chatEvent.Payload.Message.EditedAt);
    }

    [Fact]
    public void Ack_json_uses_camel_case_and_drops_nulls()
    {
        var source = ChatTestData.Source();
        var send = new MessageSendCommand
        {
            Id = "cmd:2",
            Source = source,
            IssuedAt = new DateTimeOffset(2026, 11, 14, 14, 0, 0, TimeSpan.Zero),
            IdempotencyKey = "run-2",
            Payload = new MessageSendPayload(ChatTestData.Conversation(), new Content("hi")),
        };

        var json = NlmpJson.SerializeAck(ChatAck.Success(send, ["m-1"]));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("0.1", document.RootElement.GetProperty("version").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("m-1", document.RootElement.GetProperty("result").GetProperty("messageIds")[0].GetString());
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }
}
