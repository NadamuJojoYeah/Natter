using System.Collections.Concurrent;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Layer 2b: real inbound direction. A message is created through Discord REST, delivered by
/// the gateway, and observed as an NLMP event produced by DiscordEventMapper. This validates
/// the path the injected end-to-end test deliberately skips, without attaching a responder
/// (so a self-echo can never start a reply loop).
/// </summary>
[Trait("Category", "Live")]
public sealed class GatewayIngressLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [DiscordLiveFact(DisplayName = "Inbound: gateway message becomes an NLMP MessageCreatedEvent with the mention stripped")]
    public async Task Gateway_message_maps_to_nlmp_event()
    {
        var options = NewOptions(ignoreSelf: false);
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());

        var events = new ConcurrentQueue<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(item => events.Enqueue(item));

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        using var rest = new DiscordRestClient(Settings.BotToken);
        var sentId = string.Empty;

        try
        {
            var marker = $"natter-live-ingress-{Guid.NewGuid():N}";
            var sent = await rest.CreateMessageAsync(Settings.ChannelId, $"<@!{Settings.BotUserId}> {marker}");
            sentId = sent.GetProperty("id").GetString()!;

            var mapped = await WaitForAsync(
                () => events.OfType<MessageCreatedEvent>()
                    .FirstOrDefault(item => item.Payload.Message.Id == sentId),
                TimeSpan.FromSeconds(30));

            Assert.NotNull(mapped);
            var message = mapped!.Payload.Message;

            output.WriteLine(
                $"mapped id={message.Id} conversation={message.Conversation.Id} kind={message.Conversation.Kind} "
                + $"guild={message.Conversation.GuildId} sender={message.Sender.Id} text={message.Content.Text}");

            Assert.Equal(Settings.ChannelId, message.Conversation.Id);
            Assert.Equal(ConversationKind.Guild, message.Conversation.Kind);
            Assert.Equal(Settings.GuildId, message.Conversation.GuildId);
            Assert.Equal(Settings.BotUserId, message.Sender.Id);
            Assert.Equal(UserKind.Bot, message.Sender.Kind);
            Assert.Equal(marker, message.Content.Text);
            Assert.DoesNotContain(Settings.BotUserId, message.Content.Text!, StringComparison.Ordinal);
            Assert.Equal(ChatEventTypes.MessageCreated, mapped.Type);
        }
        finally
        {
            if (sentId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, sentId);

            await upstream.DisconnectAsync();
        }
    }

    [DiscordLiveFact(DisplayName = "Inbound: ignoreSelf filters the bot's own message before NLMP")]
    public async Task Ignore_self_filters_the_echo()
    {
        var options = NewOptions(ignoreSelf: true);
        var source = Source.Create("discord", "discord", Settings.BotUserId);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(Settings.BotToken));
        using var upstream = new DiscordChatUpstream(source, transport, options, new LiveClock());

        var events = new ConcurrentQueue<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(item => events.Enqueue(item));

        await upstream.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        using var rest = new DiscordRestClient(Settings.BotToken);
        var sentId = string.Empty;

        try
        {
            var sent = await rest.CreateMessageAsync(Settings.ChannelId, $"natter-live-filtered-{Guid.NewGuid():N}");
            sentId = sent.GetProperty("id").GetString()!;

            await Task.Delay(TimeSpan.FromSeconds(4));

            var leaked = events.OfType<MessageCreatedEvent>()
                .Where(item => item.Payload.Message.Id == sentId)
                .ToList();

            output.WriteLine($"selfEchoEvents={leaked.Count}");
            Assert.Empty(leaked);
        }
        finally
        {
            if (sentId.Length > 0)
                await rest.TryDeleteMessageAsync(Settings.ChannelId, sentId);

            await upstream.DisconnectAsync();
        }
    }

    private static DiscordOptions NewOptions(bool ignoreSelf)
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
            IgnoreBots = false,
            IgnoreSelf = ignoreSelf,
            Typing = false,
            ReadyTimeoutSeconds = 60,
        };

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } value)
                return value;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return null;
    }

    private sealed class LiveClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
