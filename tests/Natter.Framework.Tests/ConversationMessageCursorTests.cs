using Natter.Application.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Message pagination cursor (task-72 ⑤). A timestamp is not unique, so the cursor is
/// <c>(createdAt, messageId)</c>. These tests build the two cases a single-key cursor gets wrong:
/// several messages inside the same second, and ids whose order disagrees with their timestamps.
/// </summary>
public sealed class ConversationMessageCursorTests
{
    private static readonly ConversationKey Key = new("discord:discord:11", "conversation-1", null, "actor-1");

    [Fact]
    public async Task Messages_within_one_second_page_without_duplicates_or_gaps()
    {
        using var db = new TempDatabase();
        var store = new SqliteConversationStore(db.Path);
        var query = new SqliteConversationQuery(db.Path);
        var sameSecond = DateTimeOffset.Parse("2026-11-24T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

        // Two turns inside the same instant: four rows share one timestamp, which is three or more
        // rows with a non-unique sort key — the exact shape a single-key cursor cannot page safely.
        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "a", "event-1", sameSecond),
            new StoredMessage("assistant", "b", null, sameSecond));
        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "c2-user", "event-2", sameSecond),
            new StoredMessage("assistant", "d", null, sameSecond));

        var sessionId = await GetSessionIdAsync(query);
        var collected = new List<StoredMessage>();
        MessageCursor? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var batch = cursor is null
                ? await query.GetMessagesAsync(sessionId, MessageCursor.Start, limit: 1)
                : await query.GetMessagesAsync(sessionId, cursor, limit: 1);
            if (batch.Count == 0)
                break;

            collected.AddRange(batch);
            cursor = new MessageCursor(batch[^1].CreatedAt, batch[^1].MessageId);
        }

        Assert.Equal(4, collected.Count);
        Assert.Equal(["a", "b", "c2-user", "d"], collected.Select(message => message.Text).ToArray());
        Assert.Equal(collected.Count, collected.Select(message => message.MessageId).Distinct().Count());
        Assert.All(collected, message => Assert.Equal(sameSecond, message.CreatedAt));
    }

    [Fact]
    public async Task Cursor_orders_by_timestamp_then_id_even_when_ids_disagree()
    {
        using var db = new TempDatabase();
        var store = new SqliteConversationStore(db.Path);
        var query = new SqliteConversationQuery(db.Path);
        var newer = DateTimeOffset.Parse("2026-11-24T10:00:05.000Z", System.Globalization.CultureInfo.InvariantCulture);
        var older = DateTimeOffset.Parse("2026-11-24T10:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

        // Ids 1..2 carry the NEWER timestamp, ids 3..4 the older one: insertion order and timestamp
        // order disagree, which is exactly where an id-only cursor returns rows out of order.
        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "newer-user", null, newer),
            new StoredMessage("assistant", "newer-assistant", null, newer));
        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "older-user", null, older),
            new StoredMessage("assistant", "older-assistant", null, older));

        var sessionId = await GetSessionIdAsync(query);

        var first = await query.GetMessagesAsync(sessionId, MessageCursor.Start, limit: 2);
        Assert.Equal(["older-user", "older-assistant"], first.Select(message => message.Text).ToArray());

        var cursor = new MessageCursor(first[^1].CreatedAt, first[^1].MessageId);
        var second = await query.GetMessagesAsync(sessionId, cursor, limit: 2);
        Assert.Equal(["newer-user", "newer-assistant"], second.Select(message => message.Text).ToArray());

        var tail = await query.GetMessagesAsync(
            sessionId,
            new MessageCursor(second[^1].CreatedAt, second[^1].MessageId),
            limit: 2);
        Assert.Empty(tail);
    }

    [Fact]
    public void Cursor_round_trips_and_rejects_forged_values()
    {
        var original = new MessageCursor(DateTimeOffset.Parse("2026-11-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), 42);
        var encoded = original.Encode();

        var decoded = MessageCursor.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(original.MessageId, decoded!.MessageId);
        Assert.Equal(original.CreatedAt, decoded.CreatedAt);

        Assert.Null(MessageCursor.TryDecode("not-a-cursor"));
        Assert.Null(MessageCursor.TryDecode(null));
        Assert.Null(MessageCursor.TryDecode(""));
    }

    [Fact]
    public async Task Legacy_after_parameter_still_works()
    {
        using var db = new TempDatabase();
        var store = new SqliteConversationStore(db.Path);
        var query = new SqliteConversationQuery(db.Path);
        var at = DateTimeOffset.Parse("2026-11-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "first", null, at),
            new StoredMessage("assistant", "second", null, at));
        await store.AppendTurnAsync(
            Key,
            new StoredMessage("user", "third", null, at.AddSeconds(1)),
            new StoredMessage("assistant", "fourth", null, at.AddSeconds(1)));

        var sessionId = await GetSessionIdAsync(query);
        var all = await query.GetMessagesAsync(sessionId, 0, limit: 10);
        Assert.Equal(4, all.Count);

        var afterSecond = await query.GetMessagesAsync(sessionId, all[1].MessageId, limit: 10);
        Assert.Equal(["third", "fourth"], afterSecond.Select(message => message.Text).ToArray());
    }

    private static async ValueTask<string> GetSessionIdAsync(SqliteConversationQuery query)
    {
        var page = await query.ListAsync(new ConversationFilter(Limit: 10));
        return Assert.Single(page.Items).SessionId;
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-cursor-" + Guid.NewGuid().ToString("N")[..8]);
            System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            Path = System.IO.Path.Combine(directory, "conversations.db");
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }
}
