using Natter.Chat;
using Natter.Chat.Discord;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P2 regression tests: Direct/Group DMs bypass guild/channel allowlists, while the
/// user allowlist and guild-channel/thread filtering remain enforced.
/// </summary>
public sealed class DiscordDmAllowlistTests
{
    [Fact]
    public void Dm_maps_when_a_guild_allowlist_is_configured()
    {
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions
        {
            SelfId = "bot",
            RequireMention = false,
            Guilds = ["g1"],
            Channels = ["c1"],
        };
        var mapper = new DiscordEventMapper(source, options);

        var dm = new DiscordMessage(
            "m-dm",
            new DiscordChannel("dm-channel-1", IsDirect: true, Name: "tester"),
            Guild: null,
            new DiscordUser("human-1", "tester", "tester", IsBot: false),
            "hello dm",
            DateTimeOffset.UtcNow);

        var mapped = mapper.MapMessage(dm, isEdit: false);
        Assert.NotNull(mapped);
        Assert.Equal(ConversationKind.Direct, mapped!.Conversation.Kind);
    }

    [Fact]
    public void Dm_user_allowlist_still_applies()
    {
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions
        {
            SelfId = "bot",
            RequireMention = false,
            Guilds = ["g1"],
            Users = ["someone-else"],
        };
        var mapper = new DiscordEventMapper(source, options);
        var dm = new DiscordMessage(
            "m-dm",
            new DiscordChannel("dm-channel-1", IsDirect: true, Name: "tester"),
            Guild: null,
            new DiscordUser("human-1", "tester", "tester", IsBot: false),
            "hello dm",
            DateTimeOffset.UtcNow);

        Assert.Null(mapper.MapMessage(dm, isEdit: false));
    }

    [Fact]
    public void Guild_channel_allowlist_still_applies()
    {
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions
        {
            SelfId = "bot",
            RequireMention = false,
            Guilds = ["g1"],
            Channels = ["allowed-channel"],
        };
        var mapper = new DiscordEventMapper(source, options);
        var guildMessage = new DiscordMessage(
            "m-guild",
            new DiscordChannel("other-channel", GuildId: "g1", Name: "other"),
            new DiscordGuild("g1", "Guild"),
            new DiscordUser("human-1", "tester", "tester", IsBot: false),
            "hello guild",
            DateTimeOffset.UtcNow);

        Assert.Null(mapper.MapMessage(guildMessage, isEdit: false));
    }

    [Fact]
    public void Dm_maps_when_no_allowlist_is_configured()
    {
        var source = Source.Create("discord", "discord", "bot");
        var options = new DiscordOptions { SelfId = "bot", RequireMention = false };
        var mapper = new DiscordEventMapper(source, options);

        var dm = new DiscordMessage(
            "m-dm",
            new DiscordChannel("dm-channel-1", IsDirect: true, Name: "tester"),
            Guild: null,
            new DiscordUser("human-1", "tester", "tester", IsBot: false),
            "hello dm",
            DateTimeOffset.UtcNow);

        var mapped = mapper.MapMessage(dm, isEdit: false);
        Assert.NotNull(mapped);
        Assert.Equal(ConversationKind.Direct, mapped!.Conversation.Kind);
    }
}
