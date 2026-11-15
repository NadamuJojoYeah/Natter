using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class RuntimeProfileTests
{
    [Fact]
    public void Defaults_are_used_when_profiles_file_is_absent()
    {
        var store = new RuntimeProfileStore(null);
        Assert.False(store.Current.IsExplicit);
        Assert.Equal("tier1-default", store.Current.DefaultProfile);
        Assert.Equal(2, store.Current.Profiles.Count);
    }

    [Fact]
    public void Resolver_prefers_conversation_and_guild_over_protocol()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "fallback",
              "profiles": [
                {
                  "id": "protocol",
                  "supportTier": 1,
                  "match": { "protocols": ["discord"] },
                  "ai": { "provider": "p#protocol" }
                },
                {
                  "id": "guild",
                  "supportTier": 1,
                  "match": { "protocols": ["discord"], "guilds": ["g1"] },
                  "ai": { "provider": "p#guild" }
                },
                {
                  "id": "conversation",
                  "supportTier": 1,
                  "match": { "protocols": ["discord"], "guilds": ["g1"], "conversations": ["c1"] },
                  "ai": { "provider": "p#conversation" }
                },
                {
                  "id": "fallback",
                  "supportTier": 1,
                  "match": {},
                  "ai": { "provider": "p#fallback" }
                }
              ]
            }
            """);

        var resolver = new ProfileResolver(new RuntimeProfileStore(temp.Path));
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Guild, GuildId: "g1");

        Assert.Equal("conversation", resolver.Resolve(source, conversation, new User("u", "U"))!.Id);
        Assert.Equal("guild", resolver.Resolve(source, new Conversation("c2", ConversationKind.Guild, GuildId: "g1"), new User("x", "X"))!.Id);
        Assert.Equal("protocol", resolver.Resolve(source, new Conversation("other", ConversationKind.Guild, GuildId: "other"), new User("u", "U"))!.Id);
        Assert.Equal("fallback", resolver.Resolve(Source.Create("satori", "satori", "b"), new Conversation("x", ConversationKind.Direct), new User("u", "U"))!.Id);
    }

    [Fact]
    public void Invalid_or_unknown_profiles_are_rejected()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """{ "schemaVersion": 2, "defaultProfile": "x", "profiles": [] }""");
        Assert.Throws<InvalidOperationException>(() => new RuntimeProfileStore(temp.Path));

        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """{ "schemaVersion": 1, "defaultProfile": "missing", "profiles": [] }""");
        Assert.Throws<InvalidOperationException>(() => new RuntimeProfileStore(temp.Path));
    }

    [Fact]
    public void Tier1_missing_provider_fails_but_tier2_is_disabled()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "t1",
              "profiles": [
                { "id": "t1", "supportTier": 1, "match": {}, "ai": { "provider": "missing#deepseek" } },
                { "id": "t2", "supportTier": 2, "match": {}, "ai": { "provider": "missing#spark" } }
              ]
            }
            """);
        var registry = new ChatResponderRegistry();
        var store = new RuntimeProfileStore(temp.Path);

        Assert.Throws<InvalidOperationException>(() => RuntimeProfileValidator.Validate(store, registry));

        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "t2",
              "profiles": [
                { "id": "t2", "supportTier": 2, "match": {}, "ai": { "provider": "missing#spark" } }
              ]
            }
            """);
        store = new RuntimeProfileStore(temp.Path);
        var disabled = RuntimeProfileValidator.Validate(store, registry);
        Assert.Contains("t2", disabled);
    }

    [Fact]
    public async Task Profile_aware_responder_routes_by_provider_ref()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "default",
              "profiles": [
                {
                  "id": "discord",
                  "supportTier": 1,
                  "match": { "protocols": ["discord"] },
                  "ai": { "provider": "p.deepseek#deepseek" }
                },
                {
                  "id": "dingtalk",
                  "supportTier": 2,
                  "match": { "protocols": ["dingtalk"] },
                  "ai": { "provider": "p.spark#spark" }
                },
                {
                  "id": "default",
                  "supportTier": 1,
                  "match": {},
                  "ai": { "provider": "p.deepseek#deepseek" }
                }
              ]
            }
            """);

        var registry = new ChatResponderRegistry();
        var deepseek = new RecordingResponder("deepseek");
        var spark = new RecordingResponder("spark");
        registry.Register(new ChatResponderRegistration("deepseek", 10, "p.deepseek", () => deepseek));
        registry.Register(new ChatResponderRegistration("spark", 20, "p.spark", () => spark));

        var store = new RuntimeProfileStore(temp.Path);
        var router = new ProfileAwareChatResponder(
            registry,
            store,
            new ProfileResolver(store),
            new CompositeChatResponder(registry));

        var clock = new TestClock();
        var discordSource = Source.Create("discord", "discord", "bot");
        var discordMessage = ChatTestData.Message(discordSource, id: "d1", conversation: ChatTestData.Conversation("c", ConversationKind.Direct));
        await router.RespondAsync(ChatTestData.CreatedEvent(discordSource, discordMessage, "evt:d"));
        Assert.Equal(1, deepseek.Calls);
        Assert.Equal(0, spark.Calls);

        var dingSource = Source.Create("dingtalk", "dingtalk", "bot");
        var dingMessage = ChatTestData.Message(dingSource, id: "t1", conversation: ChatTestData.Conversation("c", ConversationKind.Group));
        await router.RespondAsync(ChatTestData.CreatedEvent(dingSource, dingMessage, "evt:t"));
        Assert.Equal(1, deepseek.Calls);
        Assert.Equal(1, spark.Calls);
    }

    [Fact]
    public void Profile_history_scope_controls_adapter_actor_key()
    {
        var options = new Natter.Feature.Ai.AiResponderOptions { HistoryScope = "user" };
        var adapter = new Natter.Feature.Ai.HostConversationStoreAdapter(
            new Natter.Application.Chat.InMemoryConversationStore(),
            options);
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c", ConversationKind.Direct, GuildId: "g");
        var profile = new RuntimeProfile
        {
            Id = "conversation",
            History = new ProfileHistory { Scope = "conversation" },
            Ai = new ProfileAi { Provider = "p#r" },
        };

        var userKey = adapter.GetKey(source, conversation, "user");
        using (Natter.Application.Chat.ProfileContext.Push(profile))
        {
            var conversationKey = adapter.GetKey(source, conversation, "user");
            Assert.NotEqual(userKey, conversationKey);
            Assert.DoesNotContain("user", conversationKey.Split('\u001f')[3]);
        }
    }

    private sealed class RecordingResponder(string name) : IChatResponder
    {
        public int Calls { get; private set; }

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<ChatCommand?>(new MessageSendCommand
            {
                Id = "cmd:" + name,
                Source = chatEvent.Source,
                IssuedAt = DateTimeOffset.UtcNow,
                Payload = new MessageSendPayload(
                    new Conversation("c", ConversationKind.Direct),
                    new Content(name)),
            });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
