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
