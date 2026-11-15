using ConversationKey = Natter.Application.Chat.ConversationKey;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class ContextPipelineTests
{
    [Fact]
    public void Messaging_contract_is_visible_across_plugin_load_contexts()
    {
        Assert.Contains("Microsoft.Extensions.AI", Natter.Infrastructure.Plugins.PluginContractAssemblies.All);
        Assert.Contains("Microsoft.Extensions.AI.Abstractions", Natter.Infrastructure.Plugins.PluginContractAssemblies.All);
    }

    [Fact]
    public async Task Pipeline_includes_prompt_summary_memory_and_recent_messages()
    {
        var conversations = new InMemoryConversationStore();
        var memories = new InMemoryMemoryStore();
        var summaries = new InMemorySummaryStore();
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");

        var store = new HostContextPipeline(
            conversations,
            memories,
            summaries,
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler());

        await conversations.AppendTurnAsync(
            new ConversationKey(source.Key, conversation.Id, null, null),
            new StoredMessage("user", "old question", null, DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "old answer", null, DateTimeOffset.UtcNow));
        await memories.WriteAsync(new MemoryWriteRequest(
            source, conversation, user, MemoryScopeType.User, "preference", "likes tea"));
        await summaries.SaveAsync(new ConversationSummary(
            "sum1",
            new ConversationKey(source.Key, conversation.Id, null, null),
            0, 1, "summary text", 1, "default", DateTimeOffset.UtcNow));

        var profile = new RuntimeProfile
        {
            Id = "p1",
            Prompt = new ProfilePrompt { Provider = "host#static", SystemPrompt = "system prompt" },
            Context = new ProfileContextSettings { MaxInputCharacters = 4000, MaxMemoryItems = 8 },
            History = new ProfileHistory { Scope = "conversation" },
        };

        ContextBundle bundle;
        using (ProfileContext.Push(profile))
        {
            bundle = await store.AssembleAsync(source, conversation, user, "current question");
        }

        Assert.Contains(bundle.Messages, message => message.Role == ChatRole.System
            && message.Text!.Contains("system prompt", StringComparison.Ordinal)
            && message.Text.Contains("likes tea", StringComparison.Ordinal)
            && message.Text.Contains("summary text", StringComparison.Ordinal));
        Assert.Contains(bundle.Messages, message => message.Text == "old answer");
        Assert.Equal("current question", bundle.Messages[^1].Text);
        Assert.Contains("mem_", bundle.UsedMemoryIds.Single());
        Assert.True(bundle.EstimatedCharacters <= 4000);
    }

    [Fact]
    public async Task Pipeline_creates_deterministic_summary_for_old_messages()
    {
        var conversations = new InMemoryConversationStore();
        var summaries = new InMemorySummaryStore();
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        for (var index = 0; index < 5; index++)
        {
            await conversations.AppendTurnAsync(
                new ConversationKey(source.Key, conversation.Id, null, null),
                new StoredMessage("user", $"q{index}", null, DateTimeOffset.UtcNow),
                new StoredMessage("assistant", $"a{index}", null, DateTimeOffset.UtcNow));
        }

        var pipeline = new HostContextPipeline(
            conversations,
            new InMemoryMemoryStore(),
            summaries,
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler());
        var profile = new RuntimeProfile
        {
            Id = "p",
            Context = new ProfileContextSettings { MaxRecentMessages = 2, MaxInputCharacters = 4000 },
            History = new ProfileHistory { Scope = "conversation" },
        };

        ContextBundle bundle;
        using (ProfileContext.Push(profile))
            bundle = await pipeline.AssembleAsync(source, conversation, user, "now");

        var summary = await summaries.GetLatestAsync(new ConversationKey(source.Key, conversation.Id, null, null));
        Assert.NotNull(summary);
        Assert.Contains("q0", summary!.Text);
        Assert.Contains(bundle.Messages, message => message.Role == ChatRole.System
            && message.Text!.Contains("[Summary]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Context_budget_is_never_exceeded()
    {
        var conversations = new InMemoryConversationStore();
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        await conversations.AppendTurnAsync(
            new ConversationKey(source.Key, conversation.Id, null, null),
            new StoredMessage("user", new string('q', 500), null, DateTimeOffset.UtcNow),
            new StoredMessage("assistant", new string('a', 500), null, DateTimeOffset.UtcNow));

        var pipeline = new HostContextPipeline(
            conversations,
            new InMemoryMemoryStore(),
            new InMemorySummaryStore(),
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler());
        var profile = new RuntimeProfile
        {
            Id = "p",
            Prompt = new ProfilePrompt { SystemPrompt = "system" },
            Context = new ProfileContextSettings { MaxInputCharacters = 80, MaxRecentMessages = 20 },
        };

        ContextBundle bundle;
        using (ProfileContext.Push(profile))
            bundle = await pipeline.AssembleAsync(source, conversation, user, new string('x', 500));

        Assert.True(bundle.EstimatedCharacters <= 80, $"estimated={bundle.EstimatedCharacters}");
    }

    [Fact]
    public async Task Memory_scopes_isolate_users_conversations_and_sources()
    {
        var source = Source.Create("discord", "discord", "bot");
        var otherSource = Source.Create("dingtalk", "dingtalk", "bot");
        var conversation = new Conversation("c1", ConversationKind.Group, GuildId: "g1");
        var otherConversation = new Conversation("c2", ConversationKind.Group, GuildId: "g1");
        var userA = new User("a", "A");
        var userB = new User("b", "B");
        var store = new InMemoryMemoryStore();

        await store.WriteAsync(new MemoryWriteRequest(source, conversation, userA, MemoryScopeType.User, "preference", "A likes tea"));
        await store.WriteAsync(new MemoryWriteRequest(source, conversation, userA, MemoryScopeType.Conversation, "fact", "conversation fact"));
        await store.WriteAsync(new MemoryWriteRequest(source, conversation, userA, MemoryScopeType.Guild, "decision", "guild decision"));

        var userBItems = await store.SearchAsync(new MemoryQuery(source, conversation, userB));
        Assert.DoesNotContain(userBItems, item => item.Text == "A likes tea");

        var otherConversationItems = await store.SearchAsync(new MemoryQuery(source, otherConversation, userA));
        Assert.DoesNotContain(otherConversationItems, item => item.Text == "conversation fact");
        Assert.Contains(otherConversationItems, item => item.Text == "guild decision");

        var otherSourceItems = await store.SearchAsync(new MemoryQuery(otherSource, conversation, userA));
        Assert.Empty(otherSourceItems);
    }

    [Fact]
    public async Task Private_memory_is_not_injected_into_guild_context()
    {
        var source = Source.Create("discord", "discord", "bot");
        var dm = new Conversation("dm", ConversationKind.Direct);
        var guild = new Conversation("guild-channel", ConversationKind.Guild, GuildId: "g1");
        var user = new User("u1", "User");
        var memories = new InMemoryMemoryStore();
        await memories.WriteAsync(new MemoryWriteRequest(
            source, dm, user, MemoryScopeType.User, "preference", "normal memory", Sensitivity: "normal"));
        await memories.WriteAsync(new MemoryWriteRequest(
            source, dm, user, MemoryScopeType.User, "preference", "private memory", Sensitivity: "private"));
        var pipeline = new HostContextPipeline(
            new InMemoryConversationStore(),
            memories,
            new InMemorySummaryStore(),
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler());

        var dmBundle = await pipeline.AssembleAsync(source, dm, user, "dm");
        var guildBundle = await pipeline.AssembleAsync(source, guild, user, "guild");

        Assert.Contains(dmBundle.Messages, message => message.Text!.Contains("private memory", StringComparison.Ordinal));
        Assert.DoesNotContain(guildBundle.Messages, message => message.Text!.Contains("private memory", StringComparison.Ordinal));
        Assert.Contains(guildBundle.Messages, message => message.Text!.Contains("normal memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pipeline_degrades_when_memory_store_throws()
    {
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u", "U");
        var pipeline = new HostContextPipeline(
            new InMemoryConversationStore(),
            new ThrowingMemoryStore(),
            new InMemorySummaryStore(),
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler());

        var bundle = await pipeline.AssembleAsync(source, conversation, user, "hello");
        Assert.Equal("hello", bundle.Messages[^1].Text);
        Assert.Single(bundle.Messages, message => message.Role == ChatRole.System);
    }

    private sealed class ThrowingMemoryStore : IMemoryStore
    {
        public ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no memory");

        public ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no memory");

        public ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no memory");

        public ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no memory");

        public ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no memory");
    }
}
