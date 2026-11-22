namespace Natter.Chat;

/// <summary>
/// Remembers how far a chunked send got (S2-A15). A retry with the same idempotency key resumes
/// from the first undelivered chunk instead of resending chunks the platform already accepted.
/// </summary>
public sealed class ChatChunkProgress
{
    private sealed record Entry(int NextIndex, IReadOnlyList<string> MessageIds, DateTimeOffset UpdatedAt);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset UpdatedAt)> _order = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;

    public ChatChunkProgress(
        TimeSpan? ttl = null,
        int capacity = 512,
        Func<DateTimeOffset>? clock = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _capacity = Math.Max(1, capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public (int NextIndex, IReadOnlyList<string> MessageIds)? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        lock (_gate)
        {
            Prune(_clock());
            return _entries.TryGetValue(key, out var entry)
                ? (entry.NextIndex, entry.MessageIds)
                : null;
        }
    }

    public void Record(string key, int nextIndex, IEnumerable<string> messageIds)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            var ids = _entries.TryGetValue(key, out var previous)
                ? previous.MessageIds.Concat(messageIds).ToArray()
                : messageIds.ToArray();
            _entries[key] = new Entry(nextIndex, ids, now);
            _order.Enqueue((key, now));

            while (_entries.Count > _capacity && _order.TryDequeue(out var oldest))
            {
                if (_entries.TryGetValue(oldest.Key, out var entry) && entry.UpdatedAt == oldest.UpdatedAt)
                    _entries.Remove(oldest.Key);
            }
        }
    }

    public void Complete(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
            _entries.Remove(key);
    }

    private void Prune(DateTimeOffset now)
    {
        while (_order.TryDequeue(out var oldest))
        {
            if (now - oldest.UpdatedAt <= _ttl)
            {
                _order.Enqueue(oldest);
                return;
            }

            if (_entries.TryGetValue(oldest.Key, out var entry) && entry.UpdatedAt == oldest.UpdatedAt)
                _entries.Remove(oldest.Key);
        }
    }
}
