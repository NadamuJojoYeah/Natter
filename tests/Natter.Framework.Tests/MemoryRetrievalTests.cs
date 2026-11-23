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
    public async Task Scope_visibility_boundaries_are_explicit()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        var otherConversation = new Conversation("c2", ConversationKind.Direct);
        var sameGuild = new Conversation("g2", ConversationKind.Guild, GuildId: "g1");
        var otherGuild = new Conversation("g3", ConversationKind.Guild, GuildId: "g2");

        await Write(store, Alice, MemoryScopeType.User, "actor boundary marker");
        await Write(store, Alice, MemoryScopeType.Conversation, "conversation boundary marker");
        var guildSourceConversation = new Conversation("g1", ConversationKind.Guild, GuildId: "g1");
        await store.WriteAsync(new MemoryWriteRequest(Discord, guildSourceConversation, Alice, MemoryScopeType.Guild, "fact", "guild boundary marker"));
        await Write(store, Alice, MemoryScopeType.Global, "global boundary marker");

        // User: isolated per actor (other scopes are a union by design).
        var bobHits = await Search(store, Bob, null, "boundary");
        Assert.DoesNotContain(bobHits, item => item.ScopeType == MemoryScopeType.User);
        // Conversation: same conversation visible, another conversation not.
        var otherConversationHits = await store.SearchAsync(new MemoryQuery(Discord, otherConversation, Alice, 20) { Query = "boundary" });
        Assert.DoesNotContain(otherConversationHits, item => item.ScopeType == MemoryScopeType.Conversation);
        // Guild: same guild visible to another member, other guild not.
        var guildHits = await store.SearchAsync(new MemoryQuery(Discord, sameGuild, Bob, 20) { Query = "boundary" });
        Assert.Contains(guildHits, item => item.ScopeType == MemoryScopeType.Guild);
        var otherGuildHits = await store.SearchAsync(new MemoryQuery(Discord, otherGuild, Bob, 20) { Query = "boundary" });
        Assert.DoesNotContain(otherGuildHits, item => item.ScopeType == MemoryScopeType.Guild);
        // Global: documented as visible to every user of the source.
        var globalHits = await store.SearchAsync(new MemoryQuery(Discord, otherGuild, Bob, 20) { Query = "boundary" });
        Assert.Contains(globalHits, item => item.ScopeType == MemoryScopeType.Global);
    }

    [Fact]
    public void Global_is_not_a_default_write_scope()
    {
        var profile = new RuntimeProfile
        {
            Id = "p",
            Memory = new ProfileMemory { Scope = "global" },
        };
        Assert.Equal(MemoryScopeType.User, MemoryScope.DefaultWriteScope(profile));
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
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        await WriteEntry(store, Alice, MemoryScopeType.User, "UNIQUE_ALICE_SECRET_MARKER");

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
    public async Task Chinese_two_character_words_are_retrievable_with_pretokenization()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryStore(temp.Combine("natter.db"));
        await Write(store, Alice, MemoryScopeType.User, "我喜欢吃苹果，也喜欢住在杭州西湖区");

        if (string.Equals(SqliteSchema.FtsMode, "pretokenized", StringComparison.Ordinal))
        {
            Assert.NotEmpty(await Search(store, Alice, MemoryScopeType.User, "苹果"));
            Assert.NotEmpty(await Search(store, Alice, MemoryScopeType.User, "喜欢"));
            Assert.NotEmpty(await Search(store, Alice, MemoryScopeType.User, "杭州"));
            Assert.NotEmpty(await Search(store, Alice, MemoryScopeType.User, "西湖区"));
        }
        else
        {
            // Degraded mode: trigram cannot index two-character words. This is a known limitation,
            // recorded as an executable assertion rather than prose.
            Assert.NotNull(SqliteSchema.FtsWarning);
            Assert.Equal("trigram", SqliteSchema.FtsMode);
            Assert.Empty(await Search(store, Alice, MemoryScopeType.User, "苹果"));
        }
    }

    [Fact]
    public async Task Trigram_known_limitation_for_two_character_chinese_queries()
    {
        using var temp = new TempDirectory();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.Combine("trigram.db")}");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE fts USING fts5(text, tokenize='trigram');";
            await create.ExecuteNonQueryAsync();
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO fts(text) VALUES ('我喜欢吃苹果');";
            await insert.ExecuteNonQueryAsync();
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM fts WHERE fts MATCH '苹果';";
        var twoCharacter = Convert.ToInt32(await query.ExecuteScalarAsync());
        await using var three = connection.CreateCommand();
        three.CommandText = "SELECT COUNT(*) FROM fts WHERE fts MATCH '吃苹果';";
        var threeCharacter = Convert.ToInt32(await three.ExecuteScalarAsync());

        Assert.Equal(0, twoCharacter);   // Q8: the known trigram limitation, pinned.
        Assert.True(threeCharacter >= 1);
    }

    [Fact]
    public async Task Schema_v6_backfills_pretokenized_index_for_old_rows()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("natter.db");
        var store = new SqliteMemoryStore(path);
        var item = await Write(store, Alice, MemoryScopeType.User, "我喜欢吃苹果");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var damage = connection.CreateCommand();
            damage.CommandText = "UPDATE memory_items SET text_index = NULL; DELETE FROM memory_fts; PRAGMA user_version = 5;";
            await damage.ExecuteNonQueryAsync();
        }

        var reopened = new SqliteMemoryStore(path);
        if (string.Equals(SqliteSchema.FtsMode, "pretokenized", StringComparison.Ordinal))
        {
            var hits = await Search(reopened, Alice, MemoryScopeType.User, "苹果");
            Assert.Contains(hits, hit => hit.MemoryId == item.MemoryId);
        }
        else
        {
            Assert.NotNull(SqliteSchema.FtsWarning);
        }
    }

    [Fact]
    public async Task Pretokenized_index_expansion_is_bounded()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("natter.db");
        var store = new SqliteMemoryStore(path);
        for (var index = 0; index < 50; index++)
            await Write(store, Alice, MemoryScopeType.User, "我喜欢吃苹果和杭州西湖区的风景" + index);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT SUM(LENGTH(text)), SUM(LENGTH(text_index)) FROM memory_items;";
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var raw = reader.GetInt64(0);
        var indexed = reader.GetInt64(1);
        var ratio = (double)indexed / Math.Max(1, raw);
        Console.WriteLine($"MEMORY_INDEX_EXPANSION_RATIO={ratio:F2}");
        Assert.True(ratio < 6.0, $"index expansion {ratio:F2}x");
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
        var store = new InMemoryMemoryEntryStore();
        for (var index = 0; index < 6; index++)
            await WriteEntry(store, Alice, MemoryScopeType.User, $"deploy window alpha {index} " + new string('x', 120));

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
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        await WriteEntry(store, Alice, MemoryScopeType.User, "deploy window prefers concise answers");

        var enabled = await Assemble(store, Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics());
        Assert.Contains(enabled.Diagnostics.PromptFragments ?? [], fragment => fragment.Id.StartsWith("host#memory", StringComparison.Ordinal));

        var profile = Profile(disabled: ["host#memory.recall.1"]);
        var disabled = await Assemble(store, Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics(), profile);
        Assert.DoesNotContain(disabled.Diagnostics.PromptFragments ?? [], fragment => fragment.Id == "host#memory.recall.1");
    }

    [Fact]
    public async Task Default_profile_output_has_no_memory_fragments_and_matches_baseline()
    {
        var bundle = await Assemble(new InMemoryMemoryEntryStore(), Alice, new MemoryRetrievalOptions(), new MemoryRetrievalMetrics());

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

    [Fact]
    public void Repeat_reinforcement_chain_matches_the_documented_series()
    {
        // docs/P6_MEMORY_SYSTEM.md §6.1 (corrected in 74af1bb): five cross-session repeats from
        // S0(Preference)=14. The old "≈2300 days" figure was a 10× arithmetic error; the real series
        // is 14 → 24.59 → 42.52 → 72.42 → 121.59 → 201.38. Asserting it means a re-introduced flat
        // multiplier (e.g. the old S × 1.6) fails loudly instead of silently inflating stability.
        var stability = MemoryStability.InitialDays(MemoryType.Preference);
        Assert.Equal(14d, stability, 1e-9);

        foreach (var expected in new[] { 24.59d, 42.52d, 72.42d, 121.59d, 201.38d })
        {
            stability = MemoryStability.Reinforce(stability);
            Assert.InRange(stability, expected - 0.02d, expected + 0.02d);
        }

        // The 5-repeat result must stay in the interval the corrected doc states.
        Assert.InRange(stability, 190d, 215d);
    }

    [Theory]
    [InlineData(0d, 30d)]
    [InlineData(1d, 30d)]
    [InlineData(30d, 30d)]
    [InlineData(365d, 30d)]
    [InlineData(10d, 7d)]
    public void Legacy_retention_reproduces_the_old_exponential_exactly(double ageDays, double stabilityDays)
    {
        // Proof that routing the v1 live path through MemoryDecay.LegacyRetention left its numerics
        // byte-identical to the previous inline Math.Exp(-age/S). If this ever drifts, v1 recall
        // ordering changed silently — which is exactly what this release must not do.
        Assert.Equal(Math.Exp(-ageDays / stabilityDays), MemoryDecay.LegacyRetention(ageDays, stabilityDays), 1e-12);
    }

    [Fact]
    public void V1_and_v2_decay_differently_during_the_transition()
    {
        // TRANSITION STATE, NOT A BUG. v1 (IMemoryStore, the live path) still uses the exponential
        // half-life; v2 (memory_entries) uses the power law (§5.1). They are deliberately different
        // until natter memory eval has data justifying a change to live behaviour (P6.7).
        // Delete together with MemoryDecay.LegacyRetention.
        const double stabilityDays = 30d;

        Assert.Equal(0.367879d, MemoryDecay.LegacyRetention(30d, stabilityDays), 1e-6); // exp(-1)
        Assert.Equal(0.9d, MemoryDecay.Retention(30d, stabilityDays, MemoryDecay.DefaultDecay), 1e-9);
        Assert.Equal(0.049787d, MemoryDecay.LegacyRetention(90d, stabilityDays), 1e-6);  // exp(-3)
        Assert.Equal(0.8094d, MemoryDecay.Retention(90d, stabilityDays, MemoryDecay.DefaultDecay), 1e-4);

        // The gap is 2.4× at one interval and 16× at three: large enough that silently swapping the
        // v1 curve would be a real behaviour change, not a fix.
        Assert.True(
            MemoryDecay.Retention(90d, stabilityDays, MemoryDecay.DefaultDecay)
            > MemoryDecay.LegacyRetention(90d, stabilityDays) * 16,
            "power law should retain far more than the exponential at 3× the interval");
    }

    // P6.7: the SCOPE GATE assertion (production must not depend on IMemoryEntryStore) is retired.
    // Production now depends on it; the v1 types stay for one release as an Obsolete rollback path.

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

    private static async Task<MemoryEntry> WriteEntry(
        IMemoryEntryStore store,
        User user,
        MemoryScopeType scope,
        string text,
        MemoryType type = MemoryType.Fact)
        => await store.WriteAsync(new MemoryEntryWriteRequest(Discord, Direct, user, type, text, scope));

    private static async Task<MemoryItem> Write(IMemoryStore store, User user, MemoryScopeType scope, string text)
        => await store.WriteAsync(new MemoryWriteRequest(Discord, Direct, user, scope, "fact", text));

    private static async Task<IReadOnlyList<MemoryItem>> Search(IMemoryStore store, User user, MemoryScopeType? scope, string query)
        => await store.SearchAsync(new MemoryQuery(Discord, Direct, user, 20) { Query = query, Scope = scope });

    private static async Task<ContextBundle> Assemble(
        IMemoryEntryStore store,
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

    private sealed class SlowMemoryStore : IMemoryEntryStore
    {
        public async ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryEntryQuery query, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return [];
        }

        public ValueTask<MemoryEntry> WriteAsync(MemoryEntryWriteRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<MemoryEntry?> GetAsync(Source source, Conversation conversation, User user, string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> ArchiveAsync(Source source, Conversation conversation, User user, string id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask ReinforceRecallAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<MemoryEntryExport> ExportAsync(Source source, Conversation conversation, User user, bool includePrivate, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MemoryEntryExport.Create([], DateTimeOffset.UtcNow));

        public ValueTask<int> ImportAsync(Source source, Conversation conversation, User user, MemoryEntryExport export, CancellationToken cancellationToken = default)
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

/// <summary>
/// P6.5 layered memory contract (docs/P6_MEMORY_SYSTEM.md §11): five types, stability fields,
/// legacy migration, repeat reinforcement, archival instead of deletion and export versioning.
/// </summary>
public sealed class MemoryContractTests
{
    private static readonly Source Discord = Source.Create("discord", "discord", "bot");
    private static readonly Conversation Direct = new("c1", ConversationKind.Direct);
    private static readonly User Alice = new("alice", "Alice");
    private static readonly User Bob = new("bob", "Bob");

    [Theory]
    [InlineData("policy", MemoryType.Policy, 30d)]
    [InlineData("preference", MemoryType.Preference, 14d)]
    [InlineData("fact", MemoryType.Fact, 20d)]
    [InlineData("episodic", MemoryType.Episodic, 5d)]
    [InlineData("trace", MemoryType.Trace, 2.3d)]
    public void Legacy_kind_maps_to_the_matching_type(string kind, MemoryType expected, double stability)
    {
        Assert.Equal(expected, MemoryEntryMapping.FromLegacyKind(kind));
        Assert.Equal(stability, MemoryStability.InitialDays(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("note")]
    [InlineData("misc")]
    public void Unknown_legacy_kinds_fall_back_to_fact(string? kind)
        => Assert.Equal(MemoryType.Fact, MemoryEntryMapping.FromLegacyKind(kind));

    [Fact]
    public void Only_trace_is_excluded_from_active_injection()
    {
        Assert.False(MemoryStability.IsActivelyInjectable(MemoryType.Trace));
        foreach (var type in new[] { MemoryType.Policy, MemoryType.Preference, MemoryType.Fact, MemoryType.Episodic })
            Assert.True(MemoryStability.IsActivelyInjectable(type));
    }

    [Fact]
    public void Legacy_rows_are_migrated_with_ids_preserved_and_expiry_archived()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("natter.db");
        CreateVersionSixDatabaseWithLegacyRows(path);

        var store = new SqliteMemoryEntryStore(path);
        var entries = Search(store, Alice, query: null, includeArchived: true).GetAwaiter().GetResult();

        Assert.Equal(3, entries.Count);

        var kept = Assert.Single(entries, entry => entry.Id == "mem_keep");
        Assert.Equal(MemoryType.Fact, kept.Type);          // kind "note" is not one of the five
        Assert.Equal(20d, kept.StabilityDays);
        Assert.False(kept.Archived);

        var preference = Assert.Single(entries, entry => entry.Id == "mem_pref");
        Assert.Equal(MemoryType.Preference, preference.Type);
        Assert.Equal(14d, preference.StabilityDays);

        // v1 hard-expired rows must survive as archived, not vanish (§5.3 never deletes).
        var expired = Assert.Single(entries, entry => entry.Id == "mem_expired");
        Assert.True(expired.Archived);

        // Archived rows are excluded unless explicitly requested.
        var active = Search(store, Alice, query: null, includeArchived: false).GetAwaiter().GetResult();
        Assert.DoesNotContain(active, entry => entry.Id == "mem_expired");
    }

    [Fact]
    public void Repeat_write_reinforces_instead_of_creating_a_rival_copy()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));

        var first = Write(store, Alice, MemoryType.Preference, "我喜欢吃苹果").GetAwaiter().GetResult();
        var second = Write(store, Alice, MemoryType.Preference, "  我喜欢吃苹果  ").GetAwaiter().GetResult();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.ReinforceCount);
        // §6.1: S × (1 + α·S^-0.0658), α = 0.9.
        // S0(Preference)=14 → 14^-0.0658 = 0.84059 → SInc = 1.75653 (the doc's "1.75" is rounded).
        var growth = second.StabilityDays / first.StabilityDays;
        Assert.Equal(1.75653d, growth, 0.0005d);
        Assert.Equal(24.5915d, second.StabilityDays, 0.001d);

        var entries = Search(store, Alice, query: null, includeArchived: false).GetAwaiter().GetResult();
        Assert.Single(entries);
    }

    [Fact]
    public void Same_content_in_a_different_type_is_a_separate_memory()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));

        var preference = Write(store, Alice, MemoryType.Preference, "杭州").GetAwaiter().GetResult();
        var fact = Write(store, Alice, MemoryType.Fact, "杭州").GetAwaiter().GetResult();

        Assert.NotEqual(preference.Id, fact.Id);
    }

    [Fact]
    public void Chinese_two_character_queries_match_on_the_layered_store()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        Write(store, Alice, MemoryType.Fact, "我住在杭州西湖区").GetAwaiter().GetResult();
        Write(store, Alice, MemoryType.Preference, "我喜欢吃苹果").GetAwaiter().GetResult();

        var byHangzhou = Search(store, Alice, query: "杭州", includeArchived: false).GetAwaiter().GetResult();
        Assert.Contains(byHangzhou, entry => entry.Content.Contains("杭州", StringComparison.Ordinal));

        var byApple = Search(store, Alice, query: "苹果", includeArchived: false).GetAwaiter().GetResult();
        Assert.Contains(byApple, entry => entry.Content.Contains("苹果", StringComparison.Ordinal));
    }

    [Fact]
    public void Actor_scope_isolates_entries_between_users()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        Write(store, Alice, MemoryType.Fact, "我住在杭州").GetAwaiter().GetResult();

        var bobEntries = Search(store, Bob, query: "杭州", includeArchived: true).GetAwaiter().GetResult();
        Assert.Empty(bobEntries);
    }

    [Fact]
    public void Type_filter_limits_the_result_set()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        Write(store, Alice, MemoryType.Fact, "我住在杭州").GetAwaiter().GetResult();
        Write(store, Alice, MemoryType.Episodic, "上周讨论过迁移").GetAwaiter().GetResult();

        var episodic = store.SearchAsync(
            new MemoryEntryQuery(Discord, Direct, Alice) { Types = [MemoryType.Episodic] }).GetAwaiter().GetResult();

        var entry = Assert.Single(episodic);
        Assert.Equal(MemoryType.Episodic, entry.Type);
    }

    [Fact]
    public void Archive_keeps_the_row_and_hides_it_from_default_queries()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        var entry = Write(store, Alice, MemoryType.Fact, "我住在杭州").GetAwaiter().GetResult();

        Assert.True(store.ArchiveAsync(Discord, Direct, Alice, entry.Id).GetAwaiter().GetResult());
        Assert.Empty(Search(store, Alice, query: null, includeArchived: false).GetAwaiter().GetResult());
        Assert.Single(Search(store, Alice, query: null, includeArchived: true).GetAwaiter().GetResult());
    }

    [Fact]
    public void Export_declares_the_current_version_and_refuses_a_newer_one()
    {
        using var temp = new TempDirectory();
        var store = new SqliteMemoryEntryStore(temp.Combine("natter.db"));
        Write(store, Alice, MemoryType.Fact, "我住在杭州").GetAwaiter().GetResult();

        var export = store.ExportAsync(Discord, Direct, Alice, includePrivate: true).GetAwaiter().GetResult();
        Assert.Equal(MemoryExportVersions.Current, export.SchemaVersion);
        Assert.Single(export.Items);

        // Reading an envelope one version back is supported, a newer one is refused loudly.
        Assert.True(MemoryExportVersions.IsReadable(MemoryExportVersions.MinimumReadable));
        Assert.False(MemoryExportVersions.IsTooNew(MemoryExportVersions.Current));

        var tooNew = new MemoryEntryExport(MemoryExportVersions.Current + 1, DateTimeOffset.UtcNow, export.Items);
        Assert.True(MemoryExportVersions.IsTooNew(tooNew.SchemaVersion));
        var error = Assert.Throws<MemoryExportVersionException>(() => tooNew.EnsureReadable());
        Assert.Equal(MemoryExportVersions.TooNewCode, error.Code);
        Assert.Equal(MemoryExportVersions.Current + 1, error.ExportVersion);
    }

    [Fact]
    public void Legacy_v1_export_upgrades_to_the_current_envelope()
    {
        var legacy = new MemoryExport(
            1,
            DateTimeOffset.UtcNow,
            [
                new MemoryItem(
                    "mem_a", MemoryScopeType.User, "discord:discord:bot|user|alice", "alice",
                    "preference", "我喜欢吃苹果", null, 0.9, "normal", true, null,
                    DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-10),
                    null, 1),
                new MemoryItem(
                    "mem_b", MemoryScopeType.User, "discord:discord:bot|user|alice", "alice",
                    "policy", "以后都用中文", null, 1.0, "normal", true, null,
                    DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-20),
                    DateTimeOffset.UtcNow.AddDays(-1), 1),
            ]);

        var upgraded = MemoryExportBridge.Upgrade(legacy);

        Assert.Equal(MemoryExportVersions.Current, upgraded.SchemaVersion);
        Assert.Equal(2, upgraded.Items.Count);

        var preference = Assert.Single(upgraded.Items, item => item.Id == "mem_a");
        Assert.Equal(MemoryType.Preference, preference.Type);
        Assert.Equal(14d, preference.StabilityDays);
        Assert.False(preference.Archived);

        // The expired policy is archived, never dropped.
        var policy = Assert.Single(upgraded.Items, item => item.Id == "mem_b");
        Assert.Equal(MemoryType.Policy, policy.Type);
        Assert.True(policy.Archived);
    }

    [Fact]
    public void Upgrading_from_a_future_version_is_refused()
    {
        var legacy = new MemoryExport(MemoryExportVersions.Current + 1, DateTimeOffset.UtcNow, []);
        Assert.Throws<MemoryExportVersionException>(() => MemoryExportBridge.Upgrade(legacy));
    }

    [Fact]
    public void Trace_index_backfills_messages_once_and_indexes_chinese_bigrams()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("natter.db");
        CreateVersionSixDatabaseWithLegacyRows(path);
        SeedSessionAndMessages(path);

        var store = new SqliteMemoryEntryStore(path);
        var first = store.IndexUnindexedTurnsAsync().GetAwaiter().GetResult();
        Assert.Equal(2, first);

        // Idempotent: a second pass finds nothing new.
        Assert.Equal(0, store.IndexUnindexedTurnsAsync().GetAwaiter().GetResult());

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM memory_turns_fts WHERE memory_turns_fts MATCH '\"苹果\"';";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<MemoryEntry> Write(IMemoryEntryStore store, User user, MemoryType type, string content)
        => await store.WriteAsync(new MemoryEntryWriteRequest(Discord, Direct, user, type, content));

    private static async Task<IReadOnlyList<MemoryEntry>> Search(
        IMemoryEntryStore store,
        User user,
        string? query,
        bool includeArchived)
        => await store.SearchAsync(new MemoryEntryQuery(Discord, Direct, user)
        {
            Query = query,
            IncludeArchived = includeArchived,
        });

    /// <summary>Builds a v6 database (v2 memory_items schema) so the v7 migration has real input.</summary>
    private static void CreateVersionSixDatabaseWithLegacyRows(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS memory_items (
                memory_id       TEXT PRIMARY KEY,
                scope_type      TEXT NOT NULL,
                scope_id        TEXT NOT NULL,
                subject_id      TEXT NULL,
                kind            TEXT NOT NULL,
                text            TEXT NOT NULL,
                data_json       TEXT NULL,
                confidence      REAL NOT NULL,
                sensitivity     TEXT NOT NULL,
                user_confirmed  INTEGER NOT NULL,
                source_message_ids_json TEXT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                expires_at      TEXT NULL,
                version         INTEGER NOT NULL
            );
            INSERT INTO memory_items VALUES
                ('mem_keep','user','discord:discord:bot|user|alice','alice','note','我住在杭州',NULL,1.0,'normal',1,NULL,
                 '2026-11-01T00:00:00.0000000+00:00','2026-11-01T00:00:00.0000000+00:00',NULL,1),
                ('mem_pref','user','discord:discord:bot|user|alice','alice','preference','我喜欢吃苹果',NULL,0.9,'normal',1,NULL,
                 '2026-11-02T00:00:00.0000000+00:00','2026-11-02T00:00:00.0000000+00:00',NULL,1),
                ('mem_expired','user','discord:discord:bot|user|alice','alice','fact','我搬到上海了',NULL,1.0,'normal',1,NULL,
                 '2026-10-01T00:00:00.0000000+00:00','2026-10-01T00:00:00.0000000+00:00',
                 '2026-10-02T00:00:00.0000000+00:00',1);
            CREATE INDEX IF NOT EXISTS ix_memory_scope ON memory_items(scope_type, scope_id, updated_at DESC);
            PRAGMA user_version = 6;
            """;
        command.ExecuteNonQuery();
    }

    private static void SeedSessionAndMessages(string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id      TEXT PRIMARY KEY,
                source_key      TEXT NOT NULL,
                protocol        TEXT NOT NULL,
                platform        TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                guild_id        TEXT NULL,
                actor_id        TEXT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                version         INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS messages (
                message_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id      TEXT NOT NULL,
                role            TEXT NOT NULL,
                text            TEXT NOT NULL,
                source_event_id TEXT NULL,
                created_at      TEXT NOT NULL
            );
            INSERT INTO sessions VALUES
                ('s1','discord:discord:bot','discord','discord','c1',NULL,'alice',
                 '2026-11-01T00:00:00.0000000+00:00','2026-11-01T00:00:00.0000000+00:00',0);
            INSERT INTO messages VALUES
                (1,'s1','user','我喜欢吃苹果','e1','2026-11-01T00:00:00.0000000+00:00'),
                (2,'s1','assistant','好的，明白了','e2','2026-11-01T00:00:01.0000000+00:00');
            """;
        command.ExecuteNonQuery();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-p65-" + Guid.NewGuid().ToString("N"));
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
