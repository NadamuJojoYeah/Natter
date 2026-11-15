using Microsoft.Data.Sqlite;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

public sealed class ContextStorageTests
{
    [Fact]
    public async Task Memory_and_summary_persist_across_restart_and_export_import()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "natter.db");
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        var key = new ConversationKey(source.Key, conversation.Id, null, null);

        var memory = new SqliteMemoryStore(db);
        var item = await memory.WriteAsync(new MemoryWriteRequest(
            source, conversation, user, MemoryScopeType.Conversation, "fact", "persisted fact"));
        var summaries = new SqliteSummaryStore(db);
        await summaries.SaveAsync(new ConversationSummary(
            "s1", key, 0, 4, "summary", 1, "default", DateTimeOffset.UtcNow));

        var reopenedMemory = new SqliteMemoryStore(db);
        var found = await reopenedMemory.SearchAsync(new MemoryQuery(source, conversation, user));
        Assert.Contains(found, value => value.MemoryId == item.MemoryId);
        var reopenedSummary = new SqliteSummaryStore(db);
        Assert.Equal("summary", (await reopenedSummary.GetLatestAsync(key))?.Text);

        var export = await reopenedMemory.ExportAsync(new MemoryExportRequest(source, conversation, user));
        Assert.Single(export.Items);
        var other = new SqliteMemoryStore(Path.Combine(temp.Path, "other.db"));
        Assert.Equal(1, await other.ImportAsync(new MemoryImportRequest(source, conversation, user, export)));
        Assert.Single(await other.SearchAsync(new MemoryQuery(source, conversation, user)));

        Assert.True(await reopenedMemory.DeleteAsync(new MemoryDeleteRequest(source, conversation, user, MemoryId: item.MemoryId)));
        Assert.Empty(await reopenedMemory.SearchAsync(new MemoryQuery(source, conversation, user)));
    }

    [Fact]
    public async Task Schema_user_version_is_migrated_to_two()
    {
        using var temp = new TempDirectory();
        var db = Path.Combine(temp.Path, "natter.db");
        _ = new SqliteMemoryStore(db);
        using var connection = new SqliteConnection($"Data Source={db}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(2, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task Conversation_store_exposes_monotonic_message_ids_and_get_after()
    {
        using var temp = new TempDirectory();
        var store = new SqliteConversationStore(Path.Combine(temp.Path, "natter.db"));
        var key = new ConversationKey("discord:discord:bot", "c1", null, "u1");
        await store.AppendTurnAsync(
            key,
            new StoredMessage("user", "q1", "e1", DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "a1", null, DateTimeOffset.UtcNow));
        await store.AppendTurnAsync(
            key,
            new StoredMessage("user", "q2", "e2", DateTimeOffset.UtcNow),
            new StoredMessage("assistant", "a2", null, DateTimeOffset.UtcNow));

        var recent = await store.GetRecentAsync(key, 20, 24000);
        Assert.All(recent, message => Assert.True(message.MessageId > 0));
        var after = await store.GetAfterAsync(key, recent[1].MessageId, 10);
        Assert.Equal(2, after.Count);
        Assert.Equal("q2", after[0].Text);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-p3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
