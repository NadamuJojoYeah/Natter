using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Composition;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P2 regression tests: an explicit --profile override must win over match routing,
/// survive reload, and fail validation when its provider is unavailable.
/// </summary>
public sealed class ProfileRoutingTests
{
    [Fact]
    public void Override_profile_forces_even_when_a_match_exists()
    {
        var store = new RuntimeProfileStore(configDirectory: null, profileOverride: "tier2-dingtalk-spark");
        var resolver = new ProfileResolver(store);

        var discord = Source.Create("discord", "discord", "bot");
        var guild = new Conversation("c1", ConversationKind.Guild, GuildId: "g1");
        var human = new User("u1", "tester", UserKind.Human);

        var resolved = resolver.Resolve(discord, guild, human);

        Assert.Equal("tier2-dingtalk-spark", resolved?.Id);
    }

    [Fact]
    public void Override_profile_is_used_when_nothing_matches()
    {
        var store = new RuntimeProfileStore(configDirectory: null, profileOverride: "tier2-dingtalk-spark");
        var resolver = new ProfileResolver(store);

        var satori = Source.Create("satori", "satori", "bot");
        var direct = new Conversation("c2", ConversationKind.Direct);
        var human = new User("u2", "tester", UserKind.Human);

        var resolved = resolver.Resolve(satori, direct, human);

        Assert.Equal("tier2-dingtalk-spark", resolved?.Id);
    }

    [Fact]
    public async Task Override_survives_reload_and_missing_override_provider_fails_validation()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "t1",
              "profiles": [
                { "id": "t1", "supportTier": 1, "match": { "protocols": ["discord"] }, "ai": { "provider": "p.deepseek#deepseek" } },
                { "id": "t2", "supportTier": 2, "match": {}, "ai": { "provider": "p.spark#spark" } }
              ]
            }
            """);
        var store = new RuntimeProfileStore(temp.Path, "t2");
        var resolver = new ProfileResolver(store);
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c", ConversationKind.Direct);
        var human = new User("u", "U");

        Assert.Equal("t2", resolver.Resolve(source, conversation, human)?.Id);
        await store.ReloadAsync();
        Assert.Equal("t2", resolver.Resolve(source, conversation, human)?.Id);

        var registry = new ChatResponderRegistry();
        Assert.Throws<InvalidOperationException>(() => RuntimeProfileValidator.Validate(store, registry));
    }

    [Fact]
    public void Default_set_routes_discord_to_deepseek_and_dingtalk_to_spark()
    {
        var store = new RuntimeProfileStore(configDirectory: null);
        var resolver = new ProfileResolver(store);
        var human = new User("u3", "tester", UserKind.Human);
        var guild = new Conversation("c3", ConversationKind.Guild, GuildId: "g3");

        var discord = resolver.Resolve(Source.Create("discord", "discord", "bot"), guild, human);
        var dingtalk = resolver.Resolve(Source.Create("dingtalk", "dingtalk", "bot"), guild, human);

        Assert.Equal("natter.feature.ai.deepseek#deepseek", discord?.Ai.Provider);
        Assert.Equal("natter.feature.ai.spark#spark", dingtalk?.Ai.Provider);
    }
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

}
