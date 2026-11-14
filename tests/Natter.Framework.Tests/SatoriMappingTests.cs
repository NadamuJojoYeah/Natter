using Natter.Chat;
using Natter.Chat.Satori;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class SatoriMappingTests
{
    private const string Elements =
        "<at id=\"bot-1\" name=\"Natter\"/> hi "
        + "<sharp id=\"555\" name=\"general\"/> "
        + "<img src=\"https://cdn.example/a.png\" title=\"a.png\" width=\"8\" height=\"6\"/> "
        + "<quote id=\"42\"/> "
        + "<a href=\"https://example.com\">link</a><br/>tail "
        + "<b>bold</b> <unknown>inner</unknown> <emoji name=\"wave\"/> "
        + "<img src=\"file:///etc/passwd\"/> <img src=\"http://127.0.0.1/x.png\"/>";

    [Fact]
    public void Message_elements_map_to_text_mentions_attachments_and_quote()
    {
        var mapped = SatoriContentMapper.Map(Elements);

        Assert.Contains("@Natter", mapped.Content.Text);
        Assert.Contains("#general", mapped.Content.Text);
        Assert.Contains("link (https://example.com)", mapped.Content.Text);
        Assert.Contains("\ntail", mapped.Content.Text);
        Assert.Contains("bold", mapped.Content.Text);
        Assert.Contains("inner", mapped.Content.Text);
        Assert.Contains(":wave:", mapped.Content.Text);
        Assert.Equal("42", mapped.ReplyTo);

        var attachment = Assert.Single(mapped.Content.Attachments!);
        Assert.Equal("https://cdn.example/a.png", attachment.Url);
        Assert.Equal(AttachmentKind.Image, attachment.Kind);
        Assert.Equal(8, attachment.Width);
        Assert.Equal(6, attachment.Height);

        Assert.Contains(mapped.Content.Mentions!, mention => mention.Kind == MentionKind.User && mention.Id == "bot-1");
        Assert.Contains(mapped.Content.Mentions!, mention => mention.Kind == MentionKind.Channel && mention.Id == "555");
    }

    [Fact]
    public void At_all_and_here_become_high_risk_mentions()
    {
        var mapped = SatoriContentMapper.Map("<at type=\"all\"/> <at type=\"here\"/>");

        Assert.Contains(mapped.Content.Mentions!, mention => mention.Kind == MentionKind.Everyone);
        Assert.Contains(mapped.Content.Mentions!, mention => mention.Kind == MentionKind.Here);
        Assert.Contains("@everyone", mapped.Content.Text);
        Assert.Contains("@here", mapped.Content.Text);
    }

    [Fact]
    public void Malformed_fragments_fall_back_to_text_instead_of_failing()
    {
        var mapped = SatoriContentMapper.Map("2 < 3 & <at id=\"1\">broken");

        Assert.False(string.IsNullOrWhiteSpace(mapped.Content.Text));
    }

    [Fact]
    public void Login_features_map_without_guessing_missing_capabilities()
    {
        var capabilities = SatoriCapabilityMapper.Map(
            ["message.update", "message.delete", "message.attachments", "message.buttons", "guild.plain", "future.feature"]);

        Assert.True(capabilities.Edit);
        Assert.True(capabilities.Delete);
        Assert.True(capabilities.Attachments);
        Assert.True(capabilities.Buttons);
        Assert.False(capabilities.Markdown);
        Assert.False(capabilities.StreamEdit);
        Assert.False(capabilities.SlashCommands);
        Assert.False(capabilities.Reply);
        Assert.Contains(ConversationKind.Guild, capabilities.ConversationKinds);

        var empty = SatoriCapabilityMapper.Map(null);
        Assert.False(empty.Edit);
        Assert.False(empty.Delete);
        Assert.False(empty.Markdown);
        Assert.Empty(empty.ConversationKinds);

        var streaming = SatoriCapabilityMapper.Map([], new SatoriCapabilityOptions
        {
            StreamEdit = true,
            SlashCommands = true,
            Reply = true,
        });
        Assert.True(streaming.Edit);
        Assert.True(streaming.SupportsEdit);
        Assert.True(streaming.SlashCommands);
        Assert.True(streaming.Reply);
    }

    [Fact]
    public void Satori_event_maps_source_conversation_sender_and_time()
    {
        var clock = new TestClock();
        var mapper = new SatoriEventMapper(clock);
        var satoriEvent = CreateMessageEvent();

        var chatEvent = Assert.IsType<MessageCreatedEvent>(mapper.Map(satoriEvent));
        var message = chatEvent.Payload.Message;

        Assert.Equal("satori", chatEvent.Source.Protocol);
        Assert.Equal("discord", chatEvent.Source.Platform);
        Assert.Equal("bot-1", chatEvent.Source.SelfId);
        Assert.Equal(42, chatEvent.Sequence);
        Assert.Equal(ConversationKind.Guild, message.Conversation.Kind);
        Assert.Equal("444", message.Conversation.GuildId);
        Assert.Equal("general", message.Conversation.Title);
        Assert.Equal("333", message.Sender.Id);
        Assert.Equal(UserKind.Human, message.Sender.Kind);
        Assert.Contains("@Natter", message.Content.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1763128800).ToUniversalTime(), chatEvent.OccurredAt);
        Assert.Equal(TimeSpan.Zero, chatEvent.ReceivedAt.Offset);
        Assert.True(chatEvent.Extensions!.ContainsKey("satori"));
    }

    [Fact]
    public void Replayed_satori_events_are_rejected_by_the_deduplicator()
    {
        var clock = new TestClock();
        var mapper = new SatoriEventMapper(clock);
        var deduplicator = new ChatEventDeduplicator(clock: () => clock.UtcNow);

        var first = mapper.Map(CreateMessageEvent())!;
        var replay = mapper.Map(CreateMessageEvent())!;

        Assert.Equal(first.Id, replay.Id);
        Assert.True(deduplicator.TryAccept(first));
        Assert.False(deduplicator.TryAccept(replay));
    }

    [Fact]
    public void Partial_updates_are_merged_from_the_snapshot_cache_or_skipped()
    {
        var clock = new TestClock();
        var mapper = new SatoriEventMapper(clock);
        var update = CreateUpdateEvent();

        // No cached snapshot: a partial update must not be emitted as a message.
        Assert.Null(mapper.Map(update));

        mapper.Map(CreateMessageEvent());
        var updated = Assert.IsType<MessageUpdatedEvent>(mapper.Map(update));

        Assert.Equal("987", updated.Payload.Message.Id);
        Assert.Contains("@Natter", updated.Payload.Message.Content.Text);
        Assert.NotNull(updated.Payload.Message.EditedAt);
    }

    [Fact]
    public void Satori_snake_case_wire_fields_deserialize()
    {
        const string json = """
            {
              "sn": 42,
              "type": "message-created",
              "timestamp": 1763128800,
              "login": {
                "platform": "discord",
                "user": { "id": "bot-1", "name": "Natter", "is_bot": true },
                "features": ["message.update"]
              },
              "message": {
                "id": "987",
                "content": "hi",
                "channel": { "id": "555", "type": 0, "name": "general" },
                "guild": { "id": "444" },
                "user": { "id": "333", "name": "yg", "is_bot": false },
                "created_at": 1763128800
              }
            }
            """;

        var satoriEvent = Assert.IsType<SatoriEvent>(SatoriJson.DeserializeEvent(json));
        var chatEvent = Assert.IsType<MessageCreatedEvent>(new SatoriEventMapper(new TestClock()).Map(satoriEvent));

        Assert.Equal(42, chatEvent.Sequence);
        Assert.Equal("bot-1", chatEvent.Source.SelfId);
        Assert.Equal("333", chatEvent.Payload.Message.Sender.Id);
        Assert.Equal("hi", chatEvent.Payload.Message.Content.Text);
    }

    [Fact]
    public void Command_mapping_derives_headers_from_the_structured_source()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var send = new MessageSendCommand
        {
            Id = "cmd:1",
            Source = source,
            IssuedAt = clock.UtcNow,
            IdempotencyKey = "run-1",
            Payload = new MessageSendPayload(
                ChatTestData.Conversation(),
                new Content("hi", Attachments: [new Attachment("https://cdn.example/a.png", Kind: AttachmentKind.Image)]),
                ReplyTo: "older"),
        };

        var mapper = new SatoriCommandMapper();
        Assert.True(mapper.TryMap(send, out var request));

        Assert.Equal("POST", request!.Method);
        Assert.Equal("/v1/message.create", request.Route);
        Assert.Equal("discord", request.Headers["Satori-Platform"]);
        Assert.Equal("bot-1", request.Headers["Satori-User-ID"]);
        Assert.Equal("555", request.Body.GetProperty("channel_id").GetString());
        Assert.Contains("<img src=\"https://cdn.example/a.png\"/>", request.Body.GetProperty("content").GetString());

        var delete = new MessageDeleteCommand
        {
            Id = "cmd:2",
            Source = source,
            IssuedAt = clock.UtcNow,
            Payload = new MessageDeletePayload(ChatTestData.Conversation(), "987"),
        };

        Assert.True(mapper.TryMap(delete, out var deleteRequest));
        Assert.Equal("/v1/message.delete", deleteRequest!.Route);

        var typing = new TypingStartCommand
        {
            Id = "cmd:3",
            Source = source,
            IssuedAt = clock.UtcNow,
            Payload = new TypingStartPayload(ChatTestData.Conversation()),
        };

        Assert.False(mapper.TryMap(typing, out _));
    }

    [Fact]
    public void Sn_tracker_rejects_replays_and_builds_identify_payload()
    {
        var tracker = new SatoriSnTracker();

        Assert.True(tracker.TryAdvance(42));
        Assert.False(tracker.TryAdvance(42));
        Assert.False(tracker.TryAdvance(41));
        Assert.True(tracker.TryAdvance(43));
        Assert.Equal(43, tracker.LastSn);

        var payload = tracker.BuildIdentifyPayload("secret-token");
        Assert.Contains("\"sn\":43", payload);
        Assert.Contains("\"token\":\"secret-token\"", payload);
    }

    [Fact]
    public void Satori_attachment_policy_rejects_private_and_oversized_attachments()
    {
        var mapping = SatoriContentMapper.Map(
            "<img src=\"https://cdn.example/big.png\" size=\"2048\"/><img src=\"https://cdn.example/ok.png\" size=\"16\"/>",
            new Natter.Chat.Safety.ChatAttachmentPolicy { MaxBytes = 1024 });

        var attachment = Assert.Single(mapping.Content.Attachments!);
        Assert.EndsWith("ok.png", attachment.Url, StringComparison.Ordinal);
    }


    [Fact]
    public void Interaction_tokens_do_not_leak_into_ids_extensions_or_display()
    {
        var clock = new TestClock();
        var mapper = new SatoriEventMapper(clock);
        var satoriEvent = new SatoriEvent
        {
            Sn = 77,
            Type = "interaction/command",
            Timestamp = 1763128800,
            Login = CreateLogin(),
            Guild = new SatoriGuild { Id = "444" },
            Interaction = new SatoriInteraction
            {
                Id = "int-1",
                Type = "command",
                Command = "/Ask",
                User = new SatoriUser { Id = "333", Name = "yg", IsBot = false },
                Channel = new SatoriChannel { Id = "555", Type = 0, Name = "general" },
                ReplyToken = "opaque-secret-token",
            },
        };

        var chatEvent = Assert.IsType<InteractionCreatedEvent>(mapper.Map(satoriEvent));
        var interaction = chatEvent.Payload.Interaction;

        Assert.Equal(InteractionKind.Slash, interaction.Kind);
        Assert.Equal("ask", interaction.Command);
        Assert.Equal("opaque-secret-token", interaction.ReplyToken);
        Assert.DoesNotContain("opaque-secret-token", chatEvent.Id);
        Assert.DoesNotContain("opaque-secret-token", chatEvent.Extensions?.ToString() ?? string.Empty);
        Assert.DoesNotContain("opaque-secret-token", interaction.ToString());
    }

    private static SatoriEvent CreateMessageEvent()
        => new()
        {
            Sn = 42,
            Type = "message-created",
            Timestamp = 1763128800,
            Login = CreateLogin(),
            Message = new SatoriMessage
            {
                Id = "987",
                Content = "<at id=\"bot-1\" name=\"Natter\"/> hello",
                Channel = new SatoriChannel { Id = "555", Type = 0, Name = "general" },
                Guild = new SatoriGuild { Id = "444" },
                User = new SatoriUser { Id = "333", Name = "yg", IsBot = false },
                CreatedAt = 1763128800,
            },
        };

    private static SatoriEvent CreateUpdateEvent()
        => new()
        {
            Sn = 43,
            Type = "message-updated",
            Timestamp = 1763128860,
            Login = CreateLogin(),
            Channel = new SatoriChannel { Id = "555" },
            Message = new SatoriMessage
            {
                Id = "987",
                UpdatedAt = 1763128860,
            },
        };

    private static SatoriLogin CreateLogin()
        => new()
        {
            Platform = "discord",
            User = new SatoriUser { Id = "bot-1", Name = "Natter", IsBot = true },
            Features = ["message.update"],
        };
}
