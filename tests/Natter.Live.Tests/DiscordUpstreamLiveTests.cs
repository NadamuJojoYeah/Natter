using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 2: the real Discord transport and the NLMP Discord upstream against the live API.
/// These exercise the product path (capability degradation, chunking, reply references) rather
/// than raw REST, and the REST oracle verifies what Discord actually stored.
/// </summary>
[Trait("Category", "Live")]
public sealed class DiscordUpstreamLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [DiscordLiveFact(DisplayName = "Discord upstream: send → update → delete round-trips through the real API")]
    public async Task Send_update_delete_round_trip()
    {
        var options = NewOptions(typing: true);
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());
        var commands = new ChatCommandFactory(new LiveClock());

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(upstream.IsConnected);

        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId, Title: "live");
        using var rest = new DiscordRestClient(Settings.BotToken);
        var sentId = string.Empty;

        try
        {
            var sendAck = await upstream.ExecuteAsync(commands.Send(source, conversation, new Content("natter-live-send")));
            Assert.True(sendAck.Ok, sendAck.Error?.Message);
            sentId = Assert.Single(sendAck.Result!.MessageIds!);
            output.WriteLine($"sent id={sentId}");

            var stored = await rest.GetMessageAsync(Settings.ChannelId, sentId);
            Assert.NotNull(stored);
            Assert.Equal("natter-live-send", stored!.Value.GetProperty("content").GetString());

            var updateAck = await upstream.ExecuteAsync(commands.Update(source, conversation, sentId, new Content("natter-live-updated")));
            Assert.True(updateAck.Ok, updateAck.Error?.Message);

            var updated = await rest.GetMessageAsync(Settings.ChannelId, sentId);
            Assert.Equal("natter-live-updated", updated!.Value.GetProperty("content").GetString());

            var typingAck = await upstream.ExecuteAsync(commands.Typing(source, conversation));
            Assert.True(typingAck.Ok, typingAck.Error?.Message);

            var deleteAck = await upstream.ExecuteAsync(commands.Delete(source, conversation, sentId));
            Assert.True(deleteAck.Ok, deleteAck.Error?.Message);
            Assert.Null(await rest.GetMessageAsync(Settings.ChannelId, sentId));
        }
        finally
        {
            if (sentId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, sentId);

            await upstream.DisconnectAsync();
        }
    }

    [DiscordLiveFact(DisplayName = "Discord upstream: a message above the capability limit is chunked into real messages")]
    public async Task Long_message_is_chunked_by_capability_limit()
    {
        var options = NewOptions();
        options.MaxMessageLength = 200;
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());
        var commands = new ChatCommandFactory(new LiveClock());

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId, Title: "live");
        using var rest = new DiscordRestClient(Settings.BotToken);

        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(index => $"sentence {index}: " + new string('y', 40)));
        var ids = new List<string>();

        try
        {
            var ack = await upstream.ExecuteAsync(commands.Send(source, conversation, new Content(text)));
            Assert.True(ack.Ok, ack.Error?.Message);
            ids.AddRange(ack.Result!.MessageIds!);

            output.WriteLine($"chunks={ids.Count} length={text.Length}");

            Assert.True(ids.Count >= 2);
            foreach (var id in ids)
            {
                var stored = await rest.GetMessageAsync(Settings.ChannelId, id);
                Assert.NotNull(stored);
                Assert.True(stored!.Value.GetProperty("content").GetString()!.Length <= 200);
            }
        }
        finally
        {
            foreach (var id in ids)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, id);

            await upstream.DisconnectAsync();
        }
    }

    [DiscordLiveFact(DisplayName = "Discord upstream: a reply reference is stored by Discord")]
    public async Task Reply_reference_is_stored()
    {
        var options = NewOptions();
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());
        var commands = new ChatCommandFactory(new LiveClock());

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId, Title: "live");
        using var rest = new DiscordRestClient(Settings.BotToken);

        var parentId = string.Empty;
        var replyId = string.Empty;

        try
        {
            var parent = await rest.CreateMessageAsync(Settings.ChannelId, "natter-live-parent");
            parentId = parent.GetProperty("id").GetString()!;

            var ack = await upstream.ExecuteAsync(
                commands.Send(source, conversation, new Content("natter-live-child"), replyTo: parentId));
            Assert.True(ack.Ok, ack.Error?.Message);
            replyId = Assert.Single(ack.Result!.MessageIds!);

            var stored = await rest.GetMessageAsync(Settings.ChannelId, replyId);
            Assert.NotNull(stored);
            Assert.Equal(parentId, stored!.Value.GetProperty("referenced_message").GetProperty("id").GetString());
        }
        finally
        {
            if (replyId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, replyId);
            if (parentId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, parentId);

            await upstream.DisconnectAsync();
        }
    }

    [DiscordLiveFact(DisplayName = "Discord upstream: a mismatching source is rejected before any network call")]
    public async Task Mismatching_source_is_rejected()
    {
        var options = NewOptions();
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        var other = Source.Create("discord", "discord", "123");
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());
        var commands = new ChatCommandFactory(new LiveClock());

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        var conversation = new Conversation(Settings.ChannelId, ConversationKind.Guild, GuildId: Settings.GuildId, Title: "live");

        try
        {
            var ack = await upstream.ExecuteAsync(commands.Send(other, conversation, new Content("must not be sent")));
            Assert.False(ack.Ok);
            Assert.Equal(ChatErrorCodes.InvalidRequest, ack.Error!.Code);
        }
        finally
        {
            await upstream.DisconnectAsync();
        }
    }

    private static DiscordOptions NewOptions(bool typing = false)
        => new()
        {
            Token = "${secret:DISCORD_BOT_TOKEN}",
            SelfId = Settings.BotUserId,
            Intents = ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
            Guilds = [Settings.GuildId],
            Channels = [Settings.ChannelId],
            RequireMention = false,
            MessageCommands = true,
            SlashCommands = false,
            IgnoreBots = true,
            IgnoreSelf = true,
            Typing = typing,
            ReadyTimeoutSeconds = 60,
        };

    private sealed class LiveClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
