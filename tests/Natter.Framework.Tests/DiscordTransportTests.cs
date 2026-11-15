using System.Text.Json;
using Natter.Abstractions.Plugins;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.Discord;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DiscordTransportTests
{
    [Fact]
    public async Task Maps_dm_guild_and_thread_messages()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions
        {
            SelfId = "bot",
            RequireMention = true,
            SlashCommands = true,
        };

        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);

        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);
        await upstream.ConnectAsync();

        var dm = Message("dm-1", new DiscordChannel("dm", IsDirect: true), "hello");
        transport.EmitMessage(dm);

        var guildChannel = new DiscordChannel("guild-channel", "guild", "general");
        var guild = new DiscordGuild("guild", "Guild");
        var mentioned = Message(
            "guild-1",
            guildChannel,
            "<@bot> hello",
            guild: guild,
            mentions: [User("bot", "bot"), User("human-2", "Other")]);
        transport.EmitMessage(mentioned);

        var thread = Message(
            "thread-1",
            new DiscordChannel("thread", "guild", "topic", ParentId: "guild-channel", IsThread: true),
            "<@!bot> thread hello",
            guild: guild,
            mentions: [User("bot", "bot")]);
        transport.EmitMessage(thread);

        var mapped = events.OfType<MessageCreatedEvent>().Select(item => item.Payload.Message).ToArray();
        Assert.Equal(3, mapped.Length);

        Assert.Equal(ConversationKind.Direct, mapped[0].Conversation.Kind);
        Assert.Equal("hello", mapped[0].Content.Text);

        Assert.Equal(ConversationKind.Guild, mapped[1].Conversation.Kind);
        Assert.Equal("guild", mapped[1].Conversation.GuildId);
        Assert.Equal("hello", mapped[1].Content.Text);
        Assert.Contains(mapped[1].Content.Mentions!, mention => mention.Id == "human-2");

        Assert.Equal(ConversationKind.Thread, mapped[2].Conversation.Kind);
        Assert.Equal("guild-channel", mapped[2].Conversation.ParentId);
        Assert.Equal("thread hello", mapped[2].Content.Text);
    }

    [Fact]
    public async Task Group_dms_do_not_require_a_mention_and_edits_deletes_are_mapped()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", RequireMention = true };
        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);
        await upstream.ConnectAsync();

        var group = Message("group-1", new DiscordChannel("group", IsGroup: true), "group hello");
        transport.EmitMessage(group);

        var guild = new DiscordGuild("guild", "Guild");
        var channel = new DiscordChannel("channel", "guild", "general");
        var edited = new DiscordMessage(
            "edit-1",
            channel,
            guild,
            User("human-1", "Human"),
            "edited",
            clock.UtcNow,
            EditedAt: clock.UtcNow.AddSeconds(1),
            Attachments: [],
            MentionedUsers: [User("bot", "bot")]);
        transport.EmitMessage(edited, edit: true);
        transport.EmitDeleted("edit-1", channel);

        var created = Assert.Single(events.OfType<MessageCreatedEvent>());
        Assert.Equal(ConversationKind.Group, created.Payload.Message.Conversation.Kind);

        var updated = Assert.Single(events.OfType<MessageUpdatedEvent>());
        Assert.Equal("edited", updated.Payload.Message.Content.Text);
        Assert.Equal(clock.UtcNow.AddSeconds(1), updated.Payload.Message.EditedAt);

        var deleted = Assert.Single(events.OfType<MessageDeletedEvent>());
        Assert.Equal("edit-1", deleted.Payload.MessageId);
        Assert.Equal(ConversationKind.Guild, deleted.Payload.Conversation.Kind);
    }

    [Fact]
    public async Task Filters_bots_self_allowlists_empty_messages_and_accepts_attachment_only()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions
        {
            SelfId = "bot",
            RequireMention = false,
            Guilds = ["allowed-guild"],
            Channels = ["allowed-channel"],
        };

        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);
        await upstream.ConnectAsync();

        var allowed = new DiscordChannel("allowed-channel", "allowed-guild", "general");
        var allowedGuild = new DiscordGuild("allowed-guild", "Guild");

        transport.EmitMessage(Message("bot-msg", allowed, "bot", author: User("other-bot", "Bot", isBot: true), guild: allowedGuild));
        transport.EmitMessage(Message("self-msg", allowed, "self", author: User("bot", "bot"), guild: allowedGuild));
        transport.EmitMessage(Message("wrong-guild", new DiscordChannel("allowed-channel", "other", "general"), "wrong", guild: new DiscordGuild("other")));
        transport.EmitMessage(Message("wrong-channel", new DiscordChannel("other", "allowed-guild"), "wrong", guild: allowedGuild));
        transport.EmitMessage(Message("empty", allowed, "", guild: allowedGuild));
        transport.EmitMessage(Message(
            "attachment",
            allowed,
            "",
            guild: allowedGuild,
            attachments: [new DiscordAttachment("a1", "https://cdn.discordapp.com/a.png", "a.png", "image/png", 12, 1, 1)]));

        var mapped = events.OfType<MessageCreatedEvent>().Select(item => item.Payload.Message).ToArray();
        var message = Assert.Single(mapped);
        Assert.Equal("attachment", message.Id);
        Assert.Equal(AttachmentKind.Image, Assert.Single(message.Content.Attachments!).Kind);
    }

    [Fact]
    public async Task Sends_chunked_messages_with_explicit_mentions_and_idempotency()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", MaxMessageLength = 5 };
        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var factory = new ChatCommandFactory(clock);
        var conversation = new Conversation("channel", ConversationKind.Guild, GuildId: "guild");
        var command = factory.Send(
            source,
            conversation,
            new Content("abcdefghijkl", Mentions: [new Mention(MentionKind.User, "human-1")]),
            replyTo: "reply-id",
            idempotencyKey: "send-1");

        var ack = await upstream.ExecuteAsync(command);

        Assert.True(ack.Ok);
        Assert.Equal(3, transport.Sent.Count);
        Assert.Equal("abcde", transport.Sent[0].Content);
        Assert.Equal("fghij", transport.Sent[1].Content);
        Assert.Equal("kl", transport.Sent[2].Content);
        Assert.Equal("reply-id", transport.Sent[0].ReplyToMessageId);
        Assert.All(transport.Sent, sent => Assert.Contains("human-1", sent.MentionUserIds!));
        Assert.Equal(3, ack.Result!.MessageIds!.Count);

        var duplicate = await upstream.ExecuteAsync(command);
        Assert.True(duplicate.Ok);
        Assert.Equal(3, transport.Sent.Count);
    }

    [Fact]
    public async Task Maps_transport_errors_to_nlmp_errors_and_supports_typing()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot" };
        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var factory = new ChatCommandFactory(clock);
        var conversation = new Conversation("channel", ConversationKind.Guild, GuildId: "guild");

        transport.NextResult = DiscordSendResult.Failure("rate_limited", "slow down", 1200);
        var rateLimited = await upstream.ExecuteAsync(factory.Send(source, conversation, new Content("x")));
        Assert.Equal(ChatErrorCodes.RateLimited, rateLimited.Error!.Code);
        Assert.Equal(1200, rateLimited.Error.RetryAfterMs);

        transport.NextResult = DiscordSendResult.Failure("forbidden", "denied");
        var forbidden = await upstream.ExecuteAsync(factory.Send(source, conversation, new Content("x")));
        Assert.Equal(ChatErrorCodes.Forbidden, forbidden.Error!.Code);

        transport.NextResult = null;
        var typing = await upstream.ExecuteAsync(factory.Typing(source, conversation));
        Assert.True(typing.Ok);
        Assert.Single(transport.Typing);
    }

    [Fact]
    public async Task Maps_slash_interactions_and_responds_through_the_transport()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", SlashCommands = true };
        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);
        await upstream.ConnectAsync();

        var interaction = new DiscordInteraction(
            "interaction-1",
            "slash",
            User("human-1", "Human"),
            new DiscordChannel("channel", "guild", "general"),
            new DiscordGuild("guild"),
            "ask",
            new Dictionary<string, JsonElement> { ["prompt"] = JsonSerializer.SerializeToElement("hello") },
            null,
            "reply-token",
            clock.UtcNow);

        transport.EmitInteraction(interaction);

        var mapped = Assert.Single(events.OfType<InteractionCreatedEvent>());
        Assert.Equal("ask", mapped.Payload.Interaction.Command);
        Assert.Equal("***", mapped.Payload.Interaction.ToString().Split("ReplyToken = ")[1].TrimEnd(' ', '}'));

        var factory = new ChatCommandFactory(clock);
        var command = factory.Respond(source, mapped.Payload.Interaction, new Content("answer"), ephemeral: true);
        var ack = await upstream.ExecuteAsync(command);

        Assert.True(ack.Ok);
        var response = Assert.Single(transport.Interactions);
        Assert.Equal("interaction-1", response.InteractionId);
        Assert.Equal("answer", response.Content);
        Assert.True(response.Ephemeral);
    }

    [Fact]
    public async Task Connect_rejects_a_discord_login_with_a_different_self_id()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot" };
        using var transport = new DiscordFakeTransport(selfId: "other-bot");
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => upstream.ConnectAsync().AsTask());
        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.False(upstream.IsConnected);
        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task Status_and_status_command_never_expose_the_token()
    {
        var clock = new TestClock();
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", Guilds = ["guild"], Channels = ["channel"] };
        using var transport = new DiscordFakeTransport();
        using var upstream = new DiscordChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var status = upstream.GetStatus();
        Assert.True(status.Connected);
        Assert.Equal("discord:discord:bot", status.Source.Key);
        Assert.Equal(1, status.GuildCount);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(status), StringComparison.OrdinalIgnoreCase);

        var console = new StringConsole();
        var handler = new DiscordStatusCommandHandler(upstream);
        var exitCode = await handler.ExecuteAsync(new Cli.Abstractions.CliInvocation(
            "discord status",
            new Dictionary<string, string?> { ["json"] = null },
            [],
            new EmptyServiceProvider(),
            console));

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.True(document.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal("discord", document.RootElement.GetProperty("source").GetProperty("protocol").GetString());
        Assert.Equal("bot", document.RootElement.GetProperty("source").GetProperty("selfId").GetString());
    }

    [Fact]
    public void Sample_manifest_declares_the_transport_contract()
    {
        var manifest = DiscordPlugin.Manifest;
        Assert.Equal("natter.transport.discord", manifest.Id);
        Assert.Equal(PluginKind.Transport, manifest.Kind);
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.read");
        Assert.Contains(manifest.Permissions!, permission => permission == "chat.send");
        Assert.Contains(manifest.Permissions!, permission => permission == "secrets:DISCORD_BOT_TOKEN");
        Assert.Contains(manifest.Contributions!, contribution => contribution.Type == "chat.upstream" && contribution.Id == "discord");
        Assert.Contains(manifest.Contributions!, contribution => contribution.Type == "cli.command" && contribution.Id == "discord.status");
    }

    private static DiscordMessage Message(
        string id,
        DiscordChannel channel,
        string content,
        DiscordGuild? guild = null,
        DiscordUser? author = null,
        IReadOnlyList<DiscordUser>? mentions = null,
        IReadOnlyList<DiscordAttachment>? attachments = null,
        bool mentionEveryone = false)
        => new(
            id,
            channel,
            guild,
            author ?? User("human-1", "Human"),
            content,
            new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero),
            ReplyToMessageId: null,
            Attachments: attachments,
            MentionedUsers: mentions,
            MentionEveryone: mentionEveryone);

    private static DiscordUser User(string id, string name, bool isBot = false)
        => new(id, name, name, isBot);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
