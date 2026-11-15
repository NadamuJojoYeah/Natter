using Natter.Chat.DingTalk;
using Xunit;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Real DingTalk Stream handshake: gateway open-connection (HTTP) + ClientWebSocket attach, and
/// the authentication failure path. Inbound bot messages still need a human (@机器人), so this
/// suite deliberately stops at "connected + clean disconnect".
/// </summary>
[Trait("Category", "Live")]
public sealed class DingTalkStreamLiveTests(ITestOutputHelper output)
{
    private static readonly LiveSettings Settings = LiveSettings.Current;

    [DingTalkLiveFact(DisplayName = "DingTalk: Stream open-connection + WebSocket attach succeeds, disconnect is clean")]
    public async Task Stream_connects_to_the_real_gateway_and_disconnects_cleanly()
    {
        var options = new DingTalkOptions
        {
            ClientId = Settings.DingTalkClientId,
            ClientSecret = "${secret:DINGTALK_CLIENT_SECRET}",
            BotId = string.IsNullOrWhiteSpace(Settings.DingTalkBotId) ? Settings.DingTalkClientId : Settings.DingTalkBotId,
            RequireMention = true,
        };

        using var transport = new DingTalkStreamTransport(options, Settings.DingTalkClientSecret);

        await transport.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(45));
        output.WriteLine($"connected={transport.IsConnected}");

        Assert.True(transport.IsConnected);

        // Hold the socket briefly so an immediate gateway rejection would surface.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(transport.IsConnected);

        await transport.DisconnectAsync();
        output.WriteLine($"afterDisconnect={transport.IsConnected}");
        Assert.False(transport.IsConnected);
    }

    [DingTalkLiveFact(DisplayName = "DingTalk: an invalid client secret fails with 401 and never leaks the secret")]
    public async Task Invalid_secret_fails_without_leaking_it()
    {
        const string badSecret = "wrong-secret-natter-live-test-0000";
        var options = new DingTalkOptions
        {
            ClientId = Settings.DingTalkClientId,
            ClientSecret = "${secret:DINGTALK_CLIENT_SECRET}",
            BotId = string.IsNullOrWhiteSpace(Settings.DingTalkBotId) ? Settings.DingTalkClientId : Settings.DingTalkBotId,
        };

        using var transport = new DingTalkStreamTransport(options, badSecret);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.ConnectAsync().AsTask());

        output.WriteLine($"message={exception.Message}");

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(badSecret, exception.Message, StringComparison.Ordinal);
    }
}
