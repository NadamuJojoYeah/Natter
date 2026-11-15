using System.Collections.Concurrent;
using Natter.Chat.Discord;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Probe tests that record real Discord behaviour the end-to-end design depends on:
/// gateway readiness, whether a bot observes its own messages, and whether a reply reference
/// to an existing message is accepted. They are cheap and run before the full scenarios.
/// </summary>
[Trait("Category", "Live")]
public sealed class DiscordProbeLiveTests(ITestOutputHelper output)
{
    private readonly LiveSettings _settings = LiveSettings.Current;

    [DiscordLiveFact(DisplayName = "REST: bot identity matches the configured selfId")]
    public async Task Rest_identity_matches_configured_bot_id()
    {
        using var rest = new DiscordRestClient(_settings.BotToken);
        var me = await rest.GetCurrentUserAsync();

        var id = me.GetProperty("id").GetString();
        output.WriteLine($"identity id={id} username={me.GetProperty("username").GetString()} bot={me.GetProperty("bot").GetBoolean()}");

        Assert.Equal(_settings.BotUserId, id);
        Assert.True(me.GetProperty("bot").GetBoolean());
    }

    [DiscordLiveFact(DisplayName = "Gateway: connects with configured intents and reports ready metrics")]
    public async Task Gateway_connects_and_reports_ready_metrics()
    {
        var options = NewOptions(ignoreSelf: true);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(_settings.BotToken));
        var events = Observe(transport);

        await transport.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        var ready = Assert.Single(events.OfKind(DiscordTransportEventKind.Ready));
        output.WriteLine($"ready self={ready.SelfName}({ready.SelfId}) guilds={ready.GuildCount} channels={ready.ChannelCount} latency={ready.LatencyMs}ms");

        Assert.Equal(_settings.BotUserId, ready.SelfId);
        Assert.True(ready.GuildCount is > 0);
        Assert.True(ready.LatencyMs is >= 0);

        await transport.DisconnectAsync();
    }

    [DiscordLiveFact(DisplayName = "Probe: a bot does or does not observe its own MESSAGE_CREATE")]
    public async Task Probe_bot_self_message_visibility()
    {
        var options = NewOptions(ignoreSelf: false, ignoreBots: false);
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(_settings.BotToken));
        var events = Observe(transport);

        await transport.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        var marker = $"natter-live-probe-{Guid.NewGuid():N}";
        var sent = await transport.SendMessageAsync(new DiscordSendMessage(_settings.ChannelId, marker, null, []));
        Assert.True(sent.Ok, sent.ErrorMessage);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var observed = events.OfKind(DiscordTransportEventKind.MessageCreated)
                .Any(item => item.Message?.Id == sent.MessageId);

            output.WriteLine($"selfMessageVisible={observed} messageId={sent.MessageId}");
            Assert.True(
                observed,
                "The gateway did not deliver the bot's own MESSAGE_CREATE. Automated end-to-end input must come from a different identity or from a manual gate.");
        }
        finally
        {
            using var rest = new DiscordRestClient(_settings.BotToken);
            await rest.TryDeleteMessageAsync(_settings.ChannelId, sent.MessageId!);
            await transport.DisconnectAsync();
        }
    }

    [DiscordLiveFact(DisplayName = "REST: a reply reference to an existing message is accepted")]
    public async Task Reply_reference_to_existing_message_is_accepted()
    {
        using var rest = new DiscordRestClient(_settings.BotToken);
        var first = await rest.CreateMessageAsync(_settings.ChannelId, $"natter-live-ref-{Guid.NewGuid():N}");
        var firstId = first.GetProperty("id").GetString()!;

        try
        {
            var reply = await rest.CreateMessageAsync(_settings.ChannelId, "natter-live-reply", firstId);
            var replyId = reply.GetProperty("id").GetString()!;

            try
            {
                var reference = reply.GetProperty("referenced_message");
                Assert.Equal(firstId, reference.GetProperty("id").GetString());
            }
            finally
            {
                await rest.TryDeleteMessageAsync(_settings.ChannelId, replyId);
            }
        }
        finally
        {
            await rest.TryDeleteMessageAsync(_settings.ChannelId, firstId);
        }
    }

    [DiscordLiveFact(DisplayName = "Gateway: typing indicator is accepted by the real API")]
    public async Task Typing_indicator_is_accepted()
    {
        var options = NewOptions();
        using var transport = new DiscordSocketTransport(options, DiscordBotToken.From(_settings.BotToken));
        await transport.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

        try
        {
            await transport.TriggerTypingAsync(new DiscordConversationRef(_settings.ChannelId, _settings.GuildId));
        }
        finally
        {
            await transport.DisconnectAsync();
        }
    }

    private DiscordOptions NewOptions(bool ignoreSelf = true, bool ignoreBots = true)
        => new()
        {
            Token = "${secret:DISCORD_BOT_TOKEN}",
            SelfId = _settings.BotUserId,
            Intents = ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
            Guilds = [_settings.GuildId],
            Channels = [_settings.ChannelId],
            RequireMention = false,
            MessageCommands = true,
            SlashCommands = false,
            IgnoreBots = ignoreBots,
            IgnoreSelf = ignoreSelf,
            Typing = false,
            ReadyTimeoutSeconds = 60,
        };

    private static EventLog Observe(IDiscordTransport transport)
    {
        var log = new EventLog();
        log.Subscription = transport.Events.Subscribe(log.Add);
        return log;
    }

    private sealed class EventLog : IDisposable
    {
        private readonly ConcurrentQueue<DiscordTransportEvent> _events = new();

        public IDisposable? Subscription { get; set; }

        public void Add(DiscordTransportEvent value) => _events.Enqueue(value);

        public DiscordTransportEvent[] OfKind(DiscordTransportEventKind kind)
            => _events.Where(item => item.Kind == kind).ToArray();

        public DiscordTransportEvent[] Snapshot() => _events.ToArray();

        public void Dispose() => Subscription?.Dispose();
    }
}
