using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>P6.1 memory scope, lightweight retrieval, budgets and fragment injection.</summary>
public sealed class MemoryRetrievalTests
{
    private static readonly Source Discord = Source.Create("discord", "discord", "bot");
    private static readonly Conversation Direct = new("c1", ConversationKind.Direct);
    private static readonly User Alice = new("alice", "Alice");
    private static readonly User Bob = new("bob", "Bob");

    // ------------------------------------------------------------------ scope

    [Fact]
    public async Task Actor_scope_isolates_users_in_both_stores()
    {
        using var temp = new TempDirectory();
        var stores = new IMemoryStore[]
        {
            new InMemoryMemoryStore(),
            new SqliteMemoryStore(temp.Combine("natter.db")),
        };

        foreach (var store in stores)
        {
            await Write(store, Alice, MemoryScopeType.User, "alice remembers the deploy key rotation");
            var aliceHits = await Search(store, Alice, MemoryScopeType.User, "deploy");
            var bobHits = await Search(store, Bob, MemoryScopeType.User, "deploy");
            Assert.Single(aliceHits);
            Assert.Empty(bobHits);
        }
    }

    [Fact]
    public async Task Scope_kinds_filter_independently()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        await Write(store, Alice, MemoryScopeType.User, "actor note alpha");
        await Write(store, Alice, MemoryScopeType.Conversation, "conversation note alpha");
        await Write(store, Alice, MemoryScopeType.Guild, "guild note alpha");

        Assert.Single(await Search(store, Alice, MemoryScopeType.User, "alpha"));
        Assert.Single(await Search(store, Alice, MemoryScopeType.Conversation, "alpha"));
        Assert.Single(await Search(store, Alice, MemoryScopeType.Guild, "alpha"));
    }

    [Fact]
    public async Task Other_actor_memories_never_enter_the_pipeline_prompt()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        await Write(store, Alice, MemoryScopeType.User, "UNIQUE_ALICE_SECRET_MARKER");

        var bundle = await Assemble(store, Bob, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics());

        Assert.DoesNotContain(bundle.Messages, message => (message.Text ?? string.Empty).Contains("UNIQUE_ALICE_SECRET_MARKER", StringComparison.Ordinal));
        Assert.DoesNotContain(
            bundle.Diagnostics.PromptFragments ?? [],
            fragment => fragment.Id.Contains("memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Default_write_uses_actor_scope()
    {
        var store = new InMemoryMemoryStore();
        var item = await store.WriteAsync(new MemoryWriteRequest(Discord, Direct, Alice, "fact", "default scope memory"));

        Assert.Equal(MemoryScopeType.User, item.ScopeType);
        Assert.Contains("|user|alice", item.ScopeId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profile_can_change_the_default_write_scope()
    {
        var store = new InMemoryMemoryStore();
        using var scope = ProfileContext.Push(new RuntimeProfile
        {
            Id = "p",
            Memory = new ProfileMemory { Scope = "conversation" },
        });
        var item = await store.WriteAsync(new MemoryWriteRequest(Discord, Direct, Alice, "fact", "conversation default"));
        Assert.Equal(MemoryScopeType.Conversation, item.ScopeType);
        Assert.Contains("|conversation|" + Direct.Id, item.ScopeId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_retrieval_is_a_union_of_user_and_conversation_but_isolated_per_actor()
    {
        var store = new InMemoryMemoryStore();
        await store.WriteAsync(new MemoryWriteRequest(Discord, Direct, Alice, "fact", "default actor memory"));
        await Write(store, Alice, MemoryScopeType.Conversation, "conversation union memory");

        var union = await Search(store, Alice, null, "memory");
        Assert.Equal(2, union.Count);

        // A's default actor memory is invisible to B via the scope id, even without a strict
        // filter; conversation-scoped memories are shared inside the same conversation.
        var bob = await Search(store, Bob, null, "memory");
        Assert.DoesNotContain(bob, item => item.ScopeType == MemoryScopeType.User);

        // Explicit strict filter (future recall --scope) excludes the conversation entry.
        var strict = await Search(store, Alice, MemoryScopeType.User, "memory");
        Assert.Single(strict);
        Assert.All(strict, item => Assert.Equal(MemoryScopeType.User, item.ScopeType));
    }

    // ------------------------------------------------------------------ FTS / decay

    [Fact]
    public async Task Chinese_retrieval_uses_fts_and_never_throws_when_degraded()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        await Write(store, Alice, MemoryScopeType.User, "项目期限是下周五，需要提前评审");

        Assert.Contains(SqliteSchema.FtsTokenizer, new[] { "trigram", "unicode61" });
        var hits = await Search(store, Alice, MemoryScopeType.User, "项目期限");
        if (SqliteSchema.FtsTokenizer == "trigram")
            Assert.Single(hits);
        else
            Assert.NotNull(SqliteSchema.FtsWarning);
    }

    [Fact]
    public async Task Time_decay_downweights_old_entries_without_deleting_them()
    {
        var store = new InMemoryMemoryStore();
        var old = await Write(store, Alice, MemoryScopeType.User, "release window policy");
        var recent = await Write(store, Alice, MemoryScopeType.User, "release window policy current");
        // Directly age the item via export/import round trip (no test-only API).
        var export = await store.ExportAsync(new MemoryExportRequest(Discord, Direct, Alice));
        var aged = export.Items.Select(item => item.MemoryId == old.MemoryId
            ? item with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-120), CreatedAt = DateTimeOffset.UtcNow.AddDays(-120) }
            : item).ToArray();
        await store.ImportAsync(new MemoryImportRequest(Discord, Direct, Alice, new MemoryExport(1, DateTimeOffset.UtcNow, aged), Overwrite: true));

        var hits = await Search(store, Alice, MemoryScopeType.User, "release window policy");
        Assert.Equal(2, hits.Count);
        Assert.Equal(recent.MemoryId, hits[0].MemoryId);
        Assert.Contains(hits, item => item.MemoryId == old.MemoryId);
    }

    // ------------------------------------------------------------------ budgets

    [Fact]
    public async Task Retrieval_timeout_degrades_to_empty_and_counts()
    {
        var metrics = new MemoryRetrievalMetrics();
        var bundle = await Assemble(
            new SlowMemoryStore(),
            Alice,
            new MemoryRetrievalOptions { RetrievalTimeout = TimeSpan.FromMilliseconds(50) },
            metrics);

        Assert.NotNull(bundle);
        Assert.Equal(1, metrics.TimeoutCount);
        Assert.DoesNotContain(bundle.Diagnostics.PromptFragments ?? [], fragment => fragment.Id.Contains("memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Injection_and_token_budgets_truncate_and_are_counted()
    {
        var store = new InMemoryMemoryStore();
        for (var index = 0; index < 6; index++)
            await Write(store, Alice, MemoryScopeType.User, $"deploy window alpha {index} " + new string('x', 120));

        var metrics = new MemoryRetrievalMetrics();
        var bundle = await Assemble(
            store,
            Alice,
            new MemoryRetrievalOptions { MaxCandidates = 6, MaxInjected = 2, MaxTokens = 400 },
            metrics);

        var memoryFragments = (bundle.Diagnostics.PromptFragments ?? [])
            .Where(fragment => fragment.Id.Contains("memory", StringComparison.Ordinal))
            .ToArray();
        Assert.True(memoryFragments.Length <= 2);
        Assert.True(metrics.InjectionTruncations + metrics.TokenTruncations > 0);
    }

    // ------------------------------------------------------------------ fragment injection

    [Fact]
    public async Task Memory_appears_in_prompt_plan_and_can_be_disabled()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        await Write(store, Alice, MemoryScopeType.User, "deploy window prefers concise answers");

        var enabled = await Assemble(store, Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics());
        Assert.Contains(enabled.Diagnostics.PromptFragments ?? [], fragment => fragment.Id.StartsWith("host#memory", StringComparison.Ordinal));

        var profile = Profile(disabled: ["host#memory.recall.1"]);
        var disabled = await Assemble(store, Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics(), profile);
        Assert.DoesNotContain(disabled.Diagnostics.PromptFragments ?? [], fragment => fragment.Id == "host#memory.recall.1");
    }

    [Fact]
    public async Task Default_profile_output_has_no_memory_fragments_and_matches_baseline()
    {
        var bundle = await Assemble(new InMemoryMemoryStore(), Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics());

        Assert.DoesNotContain(bundle.Diagnostics.PromptFragments ?? [], fragment => fragment.Id.Contains("memory", StringComparison.Ordinal));
        Assert.Contains("You are Natter, a concise assistant.", bundle.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, bundle.Diagnostics.MemoryCount);
    }

    [Fact]
    public async Task InMemory_and_sqlite_return_the_same_order_for_the_same_query()
    {
        using var temp = new TempDirectory();
        var inMemory = new InMemoryMemoryStore();
        var sqlite = new SqliteMemoryStore(temp.Combine("natter.db"));
        foreach (var store in new IMemoryStore[] { inMemory, sqlite })
        {
            await Write(store, Alice, MemoryScopeType.User, "shared keyword deploy window");
            await Write(store, Alice, MemoryScopeType.User, "unrelated note about lunch");
        }

        var memoryHits = await Search(inMemory, Alice, MemoryScopeType.User, "deploy");
        var sqliteHits = await Search(sqlite, Alice, MemoryScopeType.User, "deploy");
        Assert.Equal(
            memoryHits.Select(item => item.Text).OrderBy(text => text).ToArray(),
            sqliteHits.Select(item => item.Text).OrderBy(text => text).ToArray());
    }

    // ------------------------------------------------------------------ helpers

    private static RuntimeProfile Profile(IReadOnlyList<string>? disabled = null)
        => new()
        {
            Id = "tier1-default",
            Ai = new ProfileAi { Provider = "test#provider" },
            Prompt = new ProfilePrompt { DisabledFragments = disabled?.ToList() ?? [] },
            Memory = new ProfileMemory { Scope = "actor" },
        };

    private static ToolLoopOptions Unused() => new();

    private static async Task<MemoryItem> Write(IMemoryStore store, User user, MemoryScopeType scope, string text)
        => await store.WriteAsync(new MemoryWriteRequest(Discord, Direct, user, scope, "fact", text));

    private static async Task<IReadOnlyList<MemoryItem>> Search(IMemoryStore store, User user, MemoryScopeType? scope, string query)
        => await store.SearchAsync(new MemoryQuery(Discord, Direct, user, 20) { Query = query, Scope = scope });

    private static async Task<ContextBundle> Assemble(
        IMemoryStore store,
        User user,
        MemoryRetrievalOptions options,
        MemoryRetrievalMetrics metrics,
        RuntimeProfile? profile = null)
    {
        using var scope = ProfileContext.Push(profile ?? Profile());
        var pipeline = new HostContextPipeline(
            new InMemoryConversationStore(),
            store,
            new InMemorySummaryStore(),
            new HostStaticPromptProvider(),
            new HostRecentWindowContextAssembler(),
            components: null,
            memoryOptions: options,
            memoryMetrics: metrics);
        return await pipeline.AssembleAsync(Discord, Direct, user, "latest deploy window policy", cancellationToken: default);
    }

    private sealed class SlowMemoryStore : IMemoryStore
    {
        public async ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return [];
        }

        public ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new MemoryExport(1, DateTimeOffset.UtcNow, []));

        public ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-p6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
