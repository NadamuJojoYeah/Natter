using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Conversation Console P1 (docs/CONVERSATION_CONSOLE.md §2.2/§7): the host-owned read-only query
/// port, its SQLite implementation, and the permission gate that decides whether a plugin can see
/// it at all. These tests are the contract the optional plugin relies on: no write method, hard page
/// limits, session isolation for content search, and "no grant → no service".
/// </summary>
public sealed class ConversationQueryTests
{
    // ------------------------------------------------------------------ list projection

    [Fact]
    public async Task List_returns_sessions_with_counts_and_newest_activity_first()
    {
        using var db = new QueryDatabase();

        var first = db.Append("discord:discord:1001", "c-1", "actor-a", "hello one");
        await Task.Delay(8);
        var second = db.Append("discord:discord:1002", "c-2", "actor-b", "hello two");
        await Task.Delay(8);
        var third = db.Append("dingtalk:dingtalk:1003", "c-3", null, "hello three");

        var page = await db.Query.ListAsync(new ConversationFilter());

        Assert.Null(page.NextCursor);
        Assert.Equal(
            [third.SessionId, second.SessionId, first.SessionId],
            page.Items.Select(item => item.SessionId));

        var latest = page.Items[0];
        Assert.Equal("dingtalk:dingtalk:1003", latest.SourceKey);
        Assert.Equal("dingtalk", latest.Protocol);
        Assert.Equal("dingtalk", latest.Platform);
        Assert.Equal("c-3", latest.ConversationId);
        Assert.Null(latest.GuildId);
        Assert.Null(latest.ActorId);
        Assert.Equal(2, latest.MessageCount);
        Assert.True(latest.FirstMessageAt <= latest.LastMessageAt);
    }

    [Fact]
    public async Task List_filters_by_source_key_and_actor_id()
    {
        using var db = new QueryDatabase();
        var a = db.Append("discord:discord:2001", "c-a", "actor-a", "alpha");
        var b = db.Append("discord:discord:2002", "c-b", "actor-b", "beta");
        db.Append("dingtalk:dingtalk:2003", "c-c", "actor-a", "gamma");

        var bySource = await db.Query.ListAsync(new ConversationFilter(SourceKey: "discord:discord:2002"));
        Assert.Single(bySource.Items);
        Assert.Equal(b.SessionId, bySource.Items[0].SessionId);

        var byActor = await db.Query.ListAsync(new ConversationFilter(ActorId: "actor-a"));
        Assert.Equal(2, byActor.Items.Count);
        Assert.Contains(byActor.Items, item => item.SessionId == a.SessionId);

        var both = await db.Query.ListAsync(new ConversationFilter(
            SourceKey: "discord:discord:2001",
            ActorId: "actor-b"));
        Assert.Empty(both.Items);
    }

    [Fact]
    public async Task List_filters_by_since_and_until()
    {
        using var db = new QueryDatabase();
        var older = db.Append("discord:discord:3001", "c-1", null, "old");
        await Task.Delay(25);

        // A boundary instant taken between the two appends isolates each side of the window, so the
        // assertion does not depend on message timestamps or SQLite's coarse date functions.
        var boundary = DateTimeOffset.UtcNow;
        await Task.Delay(25);

        var newer = db.Append("discord:discord:3002", "c-2", null, "new");

        var all = await db.Query.ListAsync(new ConversationFilter());
        Assert.Equal([newer.SessionId, older.SessionId], all.Items.Select(item => item.SessionId));

        var since = await db.Query.ListAsync(new ConversationFilter(Since: boundary));
        Assert.Single(since.Items);
        Assert.Equal(newer.SessionId, since.Items[0].SessionId);

        var until = await db.Query.ListAsync(new ConversationFilter(Until: boundary));
        Assert.Single(until.Items);
        Assert.Equal(older.SessionId, until.Items[0].SessionId);

        // Both bounds are inclusive: the exact boundary keeps the conversation it belongs to.
        var exact = await db.Query.ListAsync(new ConversationFilter(Since: boundary, Until: boundary));
        Assert.Empty(exact.Items);

        var inverted = await db.Query.ListAsync(new ConversationFilter(
            Since: boundary.AddMinutes(5),
            Until: boundary.AddMinutes(-5)));
        Assert.Empty(inverted.Items);

        // A non-UTC offset is normalised before comparison instead of being compared textually.
        var shifted = await db.Query.ListAsync(new ConversationFilter(
            Since: boundary.ToOffset(TimeSpan.FromHours(8))));
        Assert.Single(shifted.Items);
        Assert.Equal(newer.SessionId, shifted.Items[0].SessionId);
    }

    // ------------------------------------------------------------------ pagination / limits

    [Fact]
    public void Page_size_helpers_clamp_to_the_hard_limits()
    {
        Assert.Equal(ConversationQueryLimits.MaxPageSize, ConversationQueryLimits.ClampPageSize(100_000));
        Assert.Equal(ConversationQueryLimits.MaxPageSize, ConversationQueryLimits.MaxPageSize);
        Assert.Equal(ConversationQueryLimits.DefaultPageSize, ConversationQueryLimits.ClampPageSize(0));
        Assert.Equal(ConversationQueryLimits.DefaultPageSize, ConversationQueryLimits.ClampPageSize(-7));
        Assert.Equal(3, ConversationQueryLimits.ClampPageSize(3));

        Assert.Equal(ConversationQueryLimits.MaxMessagePageSize, ConversationQueryLimits.ClampMessagePageSize(int.MaxValue));
        Assert.Equal(0, ConversationQueryLimits.ClampMessagePageSize(0));
        Assert.Equal(0, ConversationQueryLimits.ClampMessagePageSize(-1));
        Assert.Equal(5, ConversationQueryLimits.ClampMessagePageSize(5));
    }

    [Fact]
    public async Task List_never_returns_more_than_the_hard_page_limit()
    {
        using var db = new QueryDatabase();
        for (var index = 0; index < ConversationQueryLimits.MaxPageSize + 5; index++)
            db.Append($"discord:discord:{4000 + index}", $"c-{index}", null, $"message {index}");

        var page = await db.Query.ListAsync(new ConversationFilter(Limit: int.MaxValue));

        Assert.Equal(ConversationQueryLimits.MaxPageSize, page.Items.Count);
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task List_paginates_with_an_opaque_cursor_without_gaps_or_duplicates()
    {
        using var db = new QueryDatabase();
        var expected = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            expected.Add(db.Append($"discord:discord:{5000 + index}", $"c-{index}", null, $"page {index}").SessionId);
            await Task.Delay(5);
        }

        expected.Reverse();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await db.Query.ListAsync(new ConversationFilter(Limit: 2, Cursor: cursor));
            seen.AddRange(page.Items.Select(item => item.SessionId));
            cursor = page.NextCursor;
            pages++;

            if (cursor is not null)
            {
                // Opaque: no separators, no readable session id, no SQL fragment.
                Assert.DoesNotContain(":", cursor, StringComparison.Ordinal);
                Assert.DoesNotContain(" ", cursor, StringComparison.Ordinal);
                Assert.DoesNotContain("SELECT", cursor, StringComparison.OrdinalIgnoreCase);
            }

            Assert.True(pages <= 10, "cursor pagination did not terminate");
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Last_page_reports_no_next_cursor()
    {
        using var db = new QueryDatabase();
        db.Append("discord:discord:6001", "c-1", null, "only one");

        var page = await db.Query.ListAsync(new ConversationFilter(Limit: 10));

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_forged_or_stale_cursor_falls_back_to_the_first_page()
    {
        using var db = new QueryDatabase();
        db.Append("discord:discord:6101", "c-1", null, "one");

        var page = await db.Query.ListAsync(new ConversationFilter(Cursor: "not-a-cursor"));

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    // ------------------------------------------------------------------ messages

    [Fact]
    public async Task Messages_page_incrementally_by_message_id()
    {
        using var db = new QueryDatabase();
        var session = db.Append("discord:discord:7001", "c-1", "actor-a", "first turn");
        db.AppendTurn("discord:discord:7001", "c-1", "actor-a", "second turn");
        db.AppendTurn("discord:discord:7001", "c-1", "actor-a", "third turn");

        var firstPage = await db.Query.GetMessagesAsync(session.SessionId, 0, 2);
        Assert.Equal(2, firstPage.Count);
        Assert.Equal(["first turn", "reply:first turn"], firstPage.Select(message => message.Text));
        Assert.All(firstPage, message => Assert.True(message.MessageId > 0));

        var after = firstPage[^1].MessageId;
        var secondPage = await db.Query.GetMessagesAsync(session.SessionId, after, 2);
        Assert.Equal(2, secondPage.Count);
        Assert.Equal("second turn", secondPage[0].Text);
        Assert.All(secondPage, message => Assert.True(message.MessageId > after));

        var thirdPage = await db.Query.GetMessagesAsync(session.SessionId, secondPage[^1].MessageId, 10);
        Assert.Equal(2, thirdPage.Count);
        Assert.Equal("third turn", thirdPage[0].Text);

        var past = await db.Query.GetMessagesAsync(session.SessionId, thirdPage[^1].MessageId, 10);
        Assert.Empty(past);
    }

    [Fact]
    public async Task Messages_clamp_the_page_size_and_reject_unknown_sessions()
    {
        using var db = new QueryDatabase();
        var session = db.Append("discord:discord:7101", "c-1", null, "text");

        var clamped = await db.Query.GetMessagesAsync(session.SessionId, 0, int.MaxValue);
        Assert.True(clamped.Count <= ConversationQueryLimits.MaxMessagePageSize);

        var zero = await db.Query.GetMessagesAsync(session.SessionId, 0, 0);
        Assert.Empty(zero);

        var unknown = await db.Query.GetMessagesAsync("000000000000000000000000", 0, 10);
        Assert.Empty(unknown);

        await Assert.ThrowsAnyAsync<ArgumentException>(async ()
            => await db.Query.GetMessagesAsync("  ", 0, 10));
    }

    // ------------------------------------------------------------------ search isolation

    [Fact]
    public async Task Content_search_never_leaks_other_sessions()
    {
        using var db = new QueryDatabase();
        var withNeedle = db.Append("discord:discord:8001", "c-needle", "actor-a", "needle-sentinel alpha");
        var withoutNeedle = db.Append("discord:discord:8002", "c-clean", "actor-b", "beta gamma");

        var search = await db.Query.ListAsync(new ConversationFilter(Query: "needle-sentinel"));
        Assert.Single(search.Items);
        Assert.Equal(withNeedle.SessionId, search.Items[0].SessionId);
        Assert.DoesNotContain(search.Items, item => item.SessionId == withoutNeedle.SessionId);

        // The other session is still reachable, and its messages never contain the needle.
        var other = await db.Query.GetMessagesAsync(withoutNeedle.SessionId, 0, 50);
        Assert.NotEmpty(other);
        Assert.DoesNotContain(other, message => message.Text.Contains("needle-sentinel", StringComparison.Ordinal));

        // Scoping the search to the clean conversation returns nothing, even though the needle
        // exists elsewhere in the same database.
        var scoped = await db.Query.ListAsync(new ConversationFilter(
            SourceKey: "discord:discord:8002",
            Query: "needle-sentinel"));
        Assert.Empty(scoped.Items);

        // Message counts stay whole-session counts, not "matching message" counts.
        Assert.Equal(2, search.Items[0].MessageCount);
    }

    [Fact]
    public async Task Search_treats_like_wildcards_as_literal_characters()
    {
        using var db = new QueryDatabase();
        db.Append("discord:discord:8101", "c-1", null, "progress 100%_done");
        db.Append("discord:discord:8102", "c-2", null, "progress 100XYdone");

        var literal = await db.Query.ListAsync(new ConversationFilter(Query: "100%_done"));

        Assert.Single(literal.Items);
        Assert.Equal("c-1", literal.Items[0].ConversationId);
    }

    [Fact]
    public async Task Search_is_bounded_and_ignores_blank_input()
    {
        using var db = new QueryDatabase();
        db.Append("discord:discord:8201", "c-1", null, "text");

        var blank = await db.Query.ListAsync(new ConversationFilter(Query: "   "));
        Assert.Single(blank.Items);

        var huge = new string('a', ConversationQueryLimits.MaxQueryLength + 500);
        var page = await db.Query.ListAsync(new ConversationFilter(Query: huge));
        Assert.Empty(page.Items);
    }

    // ------------------------------------------------------------------ storage invariants

    [Fact]
    public async Task Missing_database_answers_an_empty_page_instead_of_throwing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-query-" + Guid.NewGuid().ToString("N"));
        var query = new SqliteConversationQuery(Path.Combine(directory, "natter.db"));

        var page = await query.ListAsync(new ConversationFilter());
        var messages = await query.GetMessagesAsync("abc", 0, 10);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Empty(messages);
        Assert.False(File.Exists(Path.Combine(directory, "natter.db")));
    }

    [Fact]
    public async Task Writer_and_reader_can_use_the_same_database_at_once()
    {
        using var db = new QueryDatabase();

        db.Append("discord:discord:9001", "c-1", null, "before");

        // The host keeps writing while the console reads (SQLite concurrency requirement §6).
        var write = Task.Run(() =>
        {
            for (var index = 0; index < 20; index++)
                db.AppendTurn("discord:discord:9001", "c-1", null, $"turn {index}");
        });

        var read = Task.Run(async () =>
        {
            for (var index = 0; index < 20; index++)
            {
                var page = await db.Query.ListAsync(new ConversationFilter(Limit: 5));
                Assert.Single(page.Items);

                var messages = await db.Query.GetMessagesAsync(page.Items[0].SessionId, 0, 200);
                Assert.NotEmpty(messages);
            }
        });

        await Task.WhenAll(write, read);

        var final = await db.Query.ListAsync(new ConversationFilter());
        Assert.Single(final.Items);
        Assert.Equal(2 + 20 * 2, final.Items[0].MessageCount);
    }

    [Fact]
    public async Task Host_without_a_state_directory_answers_empty_pages()
    {
        var query = new EmptyConversationQuery();

        var page = await query.ListAsync(new ConversationFilter(Query: "anything"));
        var messages = await query.GetMessagesAsync("session", 0, 10);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Empty(messages);
    }

    [Fact]
    public void Port_surface_is_read_only_by_type()
    {
        var methods = typeof(IConversationQuery)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName)
            .ToArray();

        // Two read methods, plus the composite-cursor overload of GetMessagesAsync (task-72 ⑤).
        // The guard is about *shape* (read-only, ValueTask only), not about an exact count.
        Assert.Equal(
            ["GetMessagesAsync", "GetMessagesAsync", "ListAsync"],
            methods.Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        string[] writeVerbs = ["write", "append", "clear", "delete", "remove", "save", "update", "set", "add", "create", "import"];
        foreach (var method in methods)
        {
            Assert.DoesNotContain(
                writeVerbs,
                verb => method.Name.Contains(verb, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(typeof(ValueTask<>), method.ReturnType.GetGenericTypeDefinition());
        }

        // No writer can be smuggled in through a property either. The only property is the
        // read-only data-source capability flag (A-02).
        var properties = typeof(IConversationQuery).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var property = Assert.Single(properties);
        Assert.Equal("HasDataSource", property.Name);
        Assert.False(property.CanWrite);
    }

    [Fact]
    public async Task Sqlite_query_connection_never_creates_the_database_on_its_own()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "natter.db");

        try
        {
            var query = new SqliteConversationQuery(path);

            // The implementation must not create or migrate a database on its own: SqliteSchema is
            // the only migration owner, and a console host without persistence has nothing to read.
            var page = await query.ListAsync(new ConversationFilter());

            Assert.Empty(page.Items);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------ permission gate

    [Fact]
    public async Task Plugin_with_chat_read_resolves_the_conversation_query()
    {
        var plugin = new QueryProbePlugin();
        await using var host = await QueryHost.CreateAsync(plugin, ["chat.read"], deny: []);

        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);
        var query = provider.GetService<IConversationQuery>();

        Assert.NotNull(query);
        Assert.Same(host.Host.Services.GetRequiredService<IConversationQuery>(), query);
    }

    [Fact]
    public async Task Plugin_without_chat_read_cannot_resolve_the_conversation_query()
    {
        var plugin = new QueryProbePlugin();
        await using var host = await QueryHost.CreateAsync(plugin, ["network"], deny: []);

        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);

        Assert.Null(provider.GetService<IConversationQuery>());
        Assert.NotNull(host.Host.Services.GetRequiredService<IConversationQuery>());
    }

    [Fact]
    public async Task Denying_chat_read_removes_the_service_even_when_it_is_requested()
    {
        var plugin = new QueryProbePlugin();
        await using var host = await QueryHost.CreateAsync(plugin, ["network", "chat.read"], deny: ["chat.read"]);

        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);

        Assert.Null(provider.GetService<IConversationQuery>());
    }

    [Fact]
    public async Task Host_without_the_console_plugin_still_registers_the_port_and_keeps_running()
    {
        // Regression: installing nothing must not change host behaviour, and the port must exist in
        // the root provider so an optional consumer can be added later without host changes.
        await using var host = await QueryHost.CreateAsync(plugin: null, permissions: [], deny: []);

        var query = host.Host.Services.GetRequiredService<IConversationQuery>();
        Assert.NotNull(query);

        var page = await query.ListAsync(new ConversationFilter());
        Assert.Empty(page.Items);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A seeded <c>natter.db</c> plus the read-only query under test.</summary>
    private sealed class QueryDatabase : IDisposable
    {
        private readonly string _directory;
        private readonly SqliteConversationStore _store;

        public QueryDatabase()
        {
            _directory = Path.Combine(Path.GetTempPath(), "natter-query-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "natter.db");
            _store = new SqliteConversationStore(path);
            Query = new SqliteConversationQuery(path);
        }

        public SqliteConversationQuery Query { get; }

        public ConversationState Append(string sourceKey, string conversationId, string? actorId, string userText)
            => AppendTurn(sourceKey, conversationId, actorId, userText);

        public ConversationState AppendTurn(string sourceKey, string conversationId, string? actorId, string userText)
        {
            var key = new ConversationKey(sourceKey, conversationId, GuildId: null, actorId);
            var state = _store.GetOrCreateAsync(key).AsTask().GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow;

            _store.AppendTurnAsync(
                    key,
                    new StoredMessage("user", userText, SourceEventId: null, now),
                    new StoredMessage("assistant", "reply:" + userText, SourceEventId: null, now))
                .AsTask()
                .GetAwaiter()
                .GetResult();

            return state;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Plugin that records the provider the NPS runtime hands it at activation.</summary>
    public sealed class QueryProbePlugin : INatterPlugin
    {
        public IServiceProvider? Provider { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Provider = context.Services.Provider;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class QueryHost(NatterHost host, string directory) : IAsyncDisposable
    {
        public NatterHost Host { get; } = host;

        public static async Task<QueryHost> CreateAsync(
            QueryProbePlugin? plugin,
            string[] permissions,
            string[] deny)
        {
            var directory = Path.Combine(Path.GetTempPath(), "natter-query-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .ConfigureOptions(options =>
                {
                    options.StateDirectory = Path.Combine(directory, "state");
                    options.PluginConfigDirectory = Path.Combine(directory, "config");
                })
                .ConfigurePlugins(options =>
                {
                    foreach (var permission in deny)
                        options.ForceDeniedPermissions.Add(permission);
                });

            if (plugin is not null)
                builder.AddEmbeddedNpsPlugin(Manifest(permissions), plugin);

            var host = builder.Build();
            await host.StartAsync();
            return new QueryHost(host, directory);
        }

        private static PluginManifest Manifest(string[] permissions)
            => new()
            {
                SchemaVersion = 1,
                Id = "natter.test.conversation-query-probe",
                Name = "Conversation query probe",
                Version = "0.1.0",
                Kind = PluginKind.Tool,
                HostApi = ">=0.1.0 <1.0.0",
                Entry = new PluginEntry
                {
                    Assembly = "Natter.Framework.Tests.dll",
                    Type = typeof(QueryProbePlugin).AssemblyQualifiedName!,
                },
                Permissions = permissions.ToList(),
            };

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
