namespace Natter.Chat;

/// <summary>
/// Bounded idempotency window for send commands. Retries that reuse a key receive the
/// original ack instead of sending a second message.
/// </summary>
public sealed class ChatIdempotencyCache
{
    private sealed record Entry(ChatAck Ack, DateTimeOffset ExpiresAt, long Sequence);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    public ChatIdempotencyCache(
        TimeSpan? window = null,
        int capacity = 4096,
        Func<DateTimeOffset>? clock = null)
    {
        _window = window ?? TimeSpan.FromMinutes(10);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The idempotency window must be positive.");

        _capacity = Math.Max(1, capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan Window => _window;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Prune(_clock());
                return _entries.Count;
            }
        }
    }

    public bool TryGet(string? key, out ChatAck? ack)
    {
        ack = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        lock (_gate)
        {
            var now = _clock();
            Prune(now);

            if (!_entries.TryGetValue(key, out var entry))
                return false;

            ack = entry.Ack;
            return true;
        }
    }

    public void Store(string? key, ChatAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
        {
            var now = _clock();
            Prune(now);

            _entries[key] = new Entry(ack, now + _window, ++_sequence);

            if (_entries.Count <= _capacity)
                return;

            var oldest = _entries.MinBy(pair => pair.Value.Sequence);
            if (oldest.Key is not null)
                _entries.Remove(oldest.Key);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count == 0)
            return;

        var expired = _entries
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expired)
            _entries.Remove(key);
    }
}
