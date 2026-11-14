using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NlmpModelTests
{
    [Fact]
    public void Source_and_composite_keys_are_structured_and_source_scoped()
    {
        var first = Source.Create("satori", "discord", "bot-a");
        var second = Source.Create("satori", "discord", "bot-b");
        var native = Source.Create("discord", "discord", "bot-a");

        var message = ChatTestData.Message(first);

        Assert.Equal("satori:discord:bot-a", first.Key);
        Assert.Equal(first, Source.Create("satori", "discord", "bot-a"));
        Assert.NotEqual(first, second);
        Assert.NotEqual(MessageKey.From(first, message), MessageKey.From(second, message));
        Assert.NotEqual(MessageKey.From(first, message), MessageKey.From(native, message));
        Assert.Equal(new ConversationKey(first, "555"), ConversationKey.From(first, message.Conversation));
        Assert.Throws<NlmpValidationException>(() => Source.Create(" ", "discord", "bot-a"));
        Assert.Throws<NlmpValidationException>(() => Source.Create("satori", string.Empty, "bot-a"));
    }

    [Fact]
    public void Capabilities_default_to_unsupported_and_stream_edit_implies_edit()
    {
        var defaults = new Capabilities();

        Assert.False(defaults.Markdown);
        Assert.False(defaults.Edit);
        Assert.False(defaults.Delete);
        Assert.False(defaults.Attachments);
        Assert.False(defaults.Reply);
        Assert.Empty(defaults.ConversationKinds);
        Assert.Null(defaults.MaxMessageLength);

        var streaming = defaults with { StreamEdit = true };
        Assert.True(streaming.SupportsEdit);
        Assert.True(streaming.SupportsStreaming);
    }

    [Fact]
    public void Conversation_scope_is_per_source_even_with_the_same_display_title()
    {
        var sourceA = ChatTestData.Source(selfId: "bot-a");
        var sourceB = ChatTestData.Source(selfId: "bot-b");
        var conversation = ChatTestData.Conversation("555");

        var keyA = new ConversationKey(sourceA, conversation.Id);
        var keyB = new ConversationKey(sourceB, conversation.Id);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(conversation.Title, ChatTestData.Conversation("555").Title);
    }

    [Fact]
    public void Content_requires_text_or_attachments_and_rejects_unsafe_urls()
    {
        Assert.NotEmpty(new Content(string.Empty).Validate());

        var valid = new Content(
            "hello",
            Attachments: [new Attachment("https://cdn.example/image.png", Size: 1024)]);
        Assert.Empty(valid.Validate());

        var script = new Content("x", Attachments: [new Attachment("javascript:alert(1)")]);
        Assert.Contains(script.Validate(), error => error.Contains("http", StringComparison.OrdinalIgnoreCase));

        var metadata = new Content("x", Attachments: [new Attachment("http://169.254.169.254/latest/meta-data/")]);
        Assert.Contains(metadata.Validate(), error => error.Contains("private", StringComparison.OrdinalIgnoreCase));

        var localhost = new Content("x", Attachments: [new Attachment("http://localhost:8080/a.png")]);
        Assert.Contains(localhost.Validate(), error => error.Contains("private", StringComparison.OrdinalIgnoreCase));

        var oversized = new Attachment("https://cdn.example/big.png", Size: 2048);
        Assert.NotNull(Natter.Chat.Safety.ChatAttachmentGuard.Validate(
            oversized,
            new Natter.Chat.Safety.ChatAttachmentPolicy { MaxBytes = 1024 }));
    }

    [Fact]
    public void Mention_validation_requires_ids_for_entity_mentions()
    {
        var content = new Content("hi", Mentions: [new Mention(MentionKind.User)]);

        Assert.Contains(content.Validate(), error => error.Contains("id is required", StringComparison.Ordinal));
        Assert.Empty(new Content("hi", Mentions: [new Mention(MentionKind.Everyone)]).Validate());
    }

    [Fact]
    public void Interaction_display_masks_reply_tokens()
    {
        var clock = new TestClock();
        var interaction = new Interaction(
            "int-1",
            InteractionKind.Slash,
            ChatTestData.Human(),
            ChatTestData.Conversation(),
            clock.UtcNow,
            Command: "ask",
            ReplyToken: "super-secret-token");

        Assert.DoesNotContain("super-secret-token", interaction.ToString());

        var payload = new InteractionRespondPayload("int-1", new Content("ok"), "super-secret-token");
        Assert.DoesNotContain("super-secret-token", payload.ToString());
    }
}
