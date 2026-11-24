using System.Reactive.Subjects;
using Natter.Chat;
using Natter.Chat.Safety;
using Natter.Chat.Satori;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S1-03: outbound Satori content is built from elements, not string concatenation. Text is XML
/// escaped, structured mentions are serialized as real elements, and the mention policy is
/// validated against the mapped elements (not only <see cref="Content.Mentions"/>). Inbound
/// parsing is unchanged: platform-provided <c>&lt;at/&gt;</c> elements still become mentions.
/// </summary>
public sealed class SatoriEscapingTests
{
    [Fact]
    public void Plain_text_without_special_characters_is_byte_identical()
    {
        var content = new Content("hello world\nsecond line");

        Assert.Equal("hello world\nsecond line", SatoriOutboundContent.Format(content));
        Assert.Equal(content.Text, SatoriOutboundContent.Format(content));
    }

    [Fact]
    public void Injected_at_all_is_escaped_and_never_becomes_a_mention_element()
    {
        var content = new Content("<at type=\"all\"/>");
        var elements = SatoriOutboundContent.Build(content);

        var text = Assert.IsType<SatoriTextElement>(Assert.Single(elements));
        Assert.Equal("<at type=\"all\"/>", text.Text);
        Assert.DoesNotContain(elements, element => element is SatoriMentionElement);
        Assert.Null(SatoriOutboundContent.ValidateMentions(elements));

        var formatted = SatoriOutboundContent.Format(content);
        Assert.Equal("&lt;at type=\"all\"/&gt;", formatted);
        Assert.DoesNotContain("<at", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Injected_img_br_and_ampersand_are_all_escaped()
    {
        var content = new Content("<img src=\"http://attacker.example/x\"><br>&");

        var formatted = SatoriOutboundContent.Format(content);

        Assert.Equal(
            "&lt;img src=\"http://attacker.example/x\"&gt;&lt;br&gt;&amp;",
            formatted);
        Assert.DoesNotContain("<img", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("<br", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaped_text_round_trips_through_the_inbound_mapper_without_becoming_a_mention()
    {
        var formatted = SatoriOutboundContent.Format(new Content("<at type=\"all\"/>"));

        var mapping = SatoriContentMapper.Map(formatted);

        Assert.Null(mapping.Content.Mentions);
        Assert.Equal("<at type=\"all\"/>", mapping.Content.Text);
    }

    [Fact]
    public void Attachment_url_is_attribute_escaped()
    {
        const string url = "https://example.com/a\"b<c>&d";
        var content = new Content(
            "see ",
            Attachments: [new Attachment(url, Kind: AttachmentKind.Image)]);

        var elements = SatoriOutboundContent.Build(content);
        var attachment = Assert.IsType<SatoriAttachmentElement>(elements[^1]);
        Assert.Equal(url, attachment.Url);

        var formatted = SatoriOutboundContent.Format(content);
        Assert.Contains("src=\"https://example.com/a&quot;b&lt;c&gt;&amp;d\"", formatted);
        Assert.DoesNotContain("src=\"https://example.com/a\"b", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_mentions_are_serialized_as_elements()
    {
        var content = new Content(
            "hi ",
            Mentions:
            [
                new Mention(MentionKind.User, "u1", "Alice"),
                new Mention(MentionKind.Everyone),
                new Mention(MentionKind.Here),
                new Mention(MentionKind.Channel, "c1", "general"),
            ]);

        var formatted = SatoriOutboundContent.Format(content);

        Assert.StartsWith("hi ", formatted, StringComparison.Ordinal);
        Assert.Contains("<at id=\"u1\" name=\"Alice\"/>", formatted);
        Assert.Contains("<at type=\"all\"/>", formatted);
        Assert.Contains("<at type=\"here\"/>", formatted);
        Assert.Contains("<sharp id=\"c1\" name=\"general\"/>", formatted);
    }

    [Fact]
    public void Mention_policy_is_validated_on_mapped_elements()
    {
        var content = new Content(
            "text",
            Mentions: [new Mention(MentionKind.Everyone), new Mention(MentionKind.Here)]);
        var elements = SatoriOutboundContent.Build(content);

        var blocked = SatoriOutboundContent.ValidateMentions(elements);
        Assert.NotNull(blocked);
        Assert.Equal(ChatErrorCodes.InvalidRequest, blocked!.Code);

        var allowed = SatoriOutboundContent.ValidateMentions(
            elements,
            new ChatOutboundPolicy { AllowEveryoneMention = true, AllowHereMention = true });
        Assert.Null(allowed);
    }

    [Fact]
    public void Mapper_validates_mentions_on_the_mapped_elements()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var command = new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi", Mentions: [new Mention(MentionKind.Everyone)]));
        var mapper = new SatoriCommandMapper();

        var mapped = mapper.TryMap(
            command,
            ChatOutboundPolicy.Default,
            out var request,
            out var error);

        Assert.False(mapped);
        Assert.Null(request);
        Assert.Equal(ChatErrorCodes.InvalidRequest, error!.Code);
    }

    [Fact]
    public async Task Text_that_looks_like_an_element_cannot_bypass_the_outbound_policy()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var content = new Content("hello <at type=\"all\"/>");
        var ack = await upstream.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            content,
            idempotencyKey: "injected-text"));

        Assert.True(ack.Ok);
        var request = Assert.Single(transport.Requests);
        var body = request.Body.GetProperty("content").GetString()!;
        Assert.Equal("hello &lt;at type=\"all\"/&gt;", body);
        Assert.DoesNotContain("<at", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_caller_with_structured_everyone_is_blocked_by_the_adapter()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var ack = await upstream.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi", Mentions: [new Mention(MentionKind.Everyone)])));

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.InvalidRequest, ack.Error!.Code);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Adapter_policy_can_allow_structured_mentions_explicitly()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(
            source,
            transport,
            clock,
            outboundPolicy: new ChatOutboundPolicy { AllowEveryoneMention = true });
        await upstream.ConnectAsync();

        var ack = await upstream.ExecuteAsync(new ChatCommandFactory(clock).Send(
            source,
            ChatTestData.Conversation(),
            new Content("hi", Mentions: [new Mention(MentionKind.Everyone)])));

        Assert.True(ack.Ok);
        var request = Assert.Single(transport.Requests);
        Assert.Contains("<at type=\"all\"/>", request.Body.GetProperty("content").GetString());
    }

    [Fact]
    public void Inbound_at_elements_still_map_to_mentions()
    {
        var mapping = SatoriContentMapper.Map("<at type=\"all\"/>");

        var mention = Assert.Single(mapping.Content.Mentions!);
        Assert.Equal(MentionKind.Everyone, mention.Kind);
        Assert.Equal("@everyone", mapping.Content.Text);
    }

    [Fact]
    public async Task Update_commands_use_the_same_escaping()
    {
        var clock = new TestClock();
        var source = ChatTestData.Source();
        var transport = new FakeSatoriTransport();
        using var upstream = new SatoriChatUpstream(source, transport, clock);
        await upstream.ConnectAsync();

        var ack = await upstream.ExecuteAsync(new ChatCommandFactory(clock).Update(
            source,
            ChatTestData.Conversation(),
            "m1",
            new Content("<br>&")));

        Assert.True(ack.Ok);
        var request = Assert.Single(transport.Requests);
        Assert.Equal("/v1/message.update", request.Route);
        Assert.Equal("&lt;br&gt;&amp;", request.Body.GetProperty("content").GetString());
    }

    private sealed class FakeSatoriTransport : ISatoriTransport
    {
        private readonly Subject<SatoriEvent> _events = new();

        public IObservable<SatoriEvent> Events => _events;

        public List<SatoriApiRequest> Requests { get; } = [];

        public ValueTask ConnectAsync(long? lastSn, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SatoriSendResult> SendAsync(
            SatoriApiRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new SatoriSendResult(true, ["satori-message-1"]));
        }
    }
}
