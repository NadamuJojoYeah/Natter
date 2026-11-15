using Natter.Application.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task Persists_turns_filters_by_key_and_survives_restart()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "natter.db");
        var key = new ConversationKey("discord:discord:bot", "channel", null, "user");
        var other = key with { ConversationId = "other" };

        var store = new SqliteConversationStore(path);
        await store.AppendTurnAsync(
            key,
            new StoredMessage("user", "hello", "event-1", DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "world", null, DateTimeOffset.UtcNow));

        var recent = await store.GetRecentAsync(key, 20, 24000);
        Assert.Equal(2, recent.Count);
        Assert.Equal("hello", recent[0].Text);
        Assert.Empty(await store.GetRecentAsync(other, 20, 24000));

        var reopened = new SqliteConversationStore(path);
        Assert.Equal(2, (await reopened.GetRecentAsync(key, 20, 24000)).Count);

        await reopened.AppendTurnAsync(
            key,
            new StoredMessage("user", "duplicate", "event-1", DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "duplicate", null, DateTimeOffset.UtcNow));
        Assert.Equal(2, (await reopened.GetRecentAsync(key, 20, 24000)).Count);
    }

    [Fact]
    public async Task State_directory_and_database_permissions_are_restricted_on_unix()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var path = Path.Combine(state, "natter.db");
        _ = new SqliteConversationStore(path);

        if (OperatingSystem.IsWindows())
            return;

        var directory = new DirectoryInfo(state);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            directory.UnixFileMode);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Character_and_message_limits_are_applied_from_newest()
    {
        using var temp = new TempDirectory();
        var store = new SqliteConversationStore(Path.Combine(temp.Path, "natter.db"));
        var key = new ConversationKey("discord:discord:bot", "channel", null, "user");
        for (var index = 0; index < 4; index++)
        {
            await store.AppendTurnAsync(
                key,
                new StoredMessage("user", $"question-{index}", "event-" + index, DateTimeOffset.UtcNow),
                new StoredMessage("assistant", $"answer-{index}", null, DateTimeOffset.UtcNow));
        }

        var recent = await store.GetRecentAsync(key, maxMessages: 2, maxCharacters: 24000);
        Assert.Equal(2, recent.Count);
        Assert.Equal("question-3", recent[0].Text);
        Assert.Equal("answer-3", recent[1].Text);

        var shortened = await store.GetRecentAsync(key, maxMessages: 20, maxCharacters: 10);
        Assert.Single(shortened);
        Assert.Equal("answer-3", shortened[0].Text);
    }

    [Fact]
    public async Task Sources_and_actors_are_isolated_and_clear_removes_the_session()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "natter.db");
        var store = new SqliteConversationStore(path);

        // P2-020: the database file is created eagerly.
        Assert.True(File.Exists(path));

        var discord = new ConversationKey("discord:discord:bot", "chan-1", "guild-1", null);
        var dingtalk = new ConversationKey("dingtalk:dingtalk:bot", "chan-1", "guild-1", null);
        var actorA = discord with { ActorId = "actor-a" };
        var actorB = discord with { ActorId = "actor-b" };
        var now = DateTimeOffset.UtcNow;

        await store.AppendTurnAsync(discord,
            new StoredMessage("user", "hi-discord", "evt-d", now),
            new StoredMessage("assistant", "hello-from-deepseek", null, now));
        await store.AppendTurnAsync(dingtalk,
            new StoredMessage("user", "hi-dingtalk", "evt-t", now),
            new StoredMessage("assistant", "hello-from-spark", null, now));

        // P2-023: same conversation id on two sources must not share history.
        var discordHistory = await store.GetRecentAsync(discord, 20, 24000);
        var dingtalkHistory = await store.GetRecentAsync(dingtalk, 20, 24000);
        Assert.Equal(2, discordHistory.Count);
        Assert.Equal(2, dingtalkHistory.Count);
        Assert.Equal("hello-from-deepseek", discordHistory[1].Text);
        Assert.Equal("hello-from-spark", dingtalkHistory[1].Text);
        Assert.Equal(2, await store.CountAsync());

        // P2-024: actor-scoped keys create independent sessions.
        await store.AppendTurnAsync(actorA,
            new StoredMessage("user", "a", "evt-a", now),
            new StoredMessage("assistant", "A", null, now));
        await store.AppendTurnAsync(actorB,
            new StoredMessage("user", "b", "evt-b", now),
            new StoredMessage("assistant", "B", null, now));
        Assert.NotEqual(
            (await store.GetOrCreateAsync(actorA)).SessionId,
            (await store.GetOrCreateAsync(actorB)).SessionId);
        Assert.Equal(4, await store.CountAsync());

        // P2-026: provider switch keeps the session; the store is provider-agnostic.
        await store.AppendTurnAsync(discord,
            new StoredMessage("user", "after-switch", "evt-d2", now),
            new StoredMessage("assistant", "spark-now", null, now));
        var mixed = await store.GetRecentAsync(discord, 20, 24000);
        Assert.Contains(mixed, message => message.Text == "hello-from-deepseek");
        Assert.Contains(mixed, message => message.Text == "spark-now");

        // P2-027: ClearAsync removes the session and cascades its messages.
        await store.ClearAsync(discord);
        Assert.Equal(3, await store.CountAsync());
        Assert.Empty(await store.GetRecentAsync(discord, 20, 24000));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
