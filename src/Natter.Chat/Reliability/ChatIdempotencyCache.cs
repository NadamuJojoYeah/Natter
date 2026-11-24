namespace Natter.Chat;

/// <summary>
/// Bounded idempotency window for send commands. Retries that reuse a key receive the
/// original ack instead of sending a second message.
/// </summary>
public sealed class ChatIdempotencyCache
{
    /// <summary>Outcome of <see cref="TryReserve"/>.</summary>
    public enum Reservation
    {
        /// <summary>The caller owns the key and must commit or roll back.</summary>
        Reserved,

        /// <summary>A committed ack exists; return it and do not execute again.</summary>
        Cached,

        /// <summary>Another execution with the same key is still in flight.</summary>
        InFlight,
    }

    private sealed record Entry(ChatAck Ack, DateTimeOffset ExpiresAt, long Sequence);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset ExpiresAt, long Sequence)> _expiry = new();
    private readonly Queue<(string Key, DateTimeOffset Since)> _pendingExpiry = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly TimeSpan _pendingTimeout;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    public ChatIdempotencyCache(
        TimeSpan? window = null,
        int capacity = 4096,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? pendingTimeout = null)
    {
        _window = window ?? TimeSpan.FromMinutes(10);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The idempotency window must be positive.");

        _pendingTimeout = pendingTimeout ?? TimeSpan.FromMinutes(2);
        if (_pendingTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pendingTimeout), "The pending timeout must be positive.");

        _capacity = Math.Max(1, capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan Window => _window;

    /// <summary>Reservations older than this are released so a crashed execution cannot block retries.</summary>
    public TimeSpan PendingTimeout => _pendingTimeout;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                Prune(_clock());
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// S2-A1 three-phase check: reserve before executing. Returns Cached when a committed ack is
    /// available, InFlight when another caller owns the key, and Reserved otherwise.
    /// </summary>
    public Reservation TryReserve(string? key, out ChatAck? cached)
    {
        cached = null;
        if (string.IsNullOrWhiteSpace(key))
            return Reservation.Reserved;

        lock (_gate)
        {
            var now = _clock();
            Prune(now);

            if (_entries.TryGetValue(key, out var entry))
            {
                cached = entry.Ack;
                return Reservation.Cached;
            }

            if (_pending.TryGetValue(key, out var since))
            {
                if (now - since <= _pendingTimeout)
                    return Reservation.InFlight;
                _pending.Remove(key);
            }

            _pending[key] = now;
            _pendingExpiry.Enqueue((key, now));
            TrimPending();
            return Reservation.Reserved;
        }
    }

    public void Commit(string? key, ChatAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
        {
            var now = _clock();
            Prune(now);
            _pending.Remove(key);
            var sequence = ++_sequence;
            _entries[key] = new Entry(ack, now + _window, sequence);
            _expiry.Enqueue((key, now + _window, sequence));
            TrimEntries();
        }
    }

    public void Rollback(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_gate)
            _pending.Remove(key);
    }

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

    /// <summary>Commits a result without an explicit reservation (legacy adapter API).</summary>
    public void Store(string? key, ChatAck ack)
        => Commit(key, ack);

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _pending.Clear();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        // Incremental expiry: no full-table scan under the lock.
        while (_expiry.TryDequeue(out var entry))
        {
            if (entry.ExpiresAt > now)
            {
                _expiry.Enqueue(entry);
                break;
            }

            if (_entries.TryGetValue(entry.Key, out var current)
                && current.Sequence == entry.Sequence
                && current.ExpiresAt <= now)
                _entries.Remove(entry.Key);
        }

        while (_pendingExpiry.TryDequeue(out var pending))
        {
            if (now - pending.Since <= _pendingTimeout)
            {
                _pendingExpiry.Enqueue(pending);
                break;
            }

            if (_pending.TryGetValue(pending.Key, out var since) && since == pending.Since)
                _pending.Remove(pending.Key);
        }
    }

    private void TrimEntries()
    {
        while (_entries.Count > _capacity && _expiry.TryDequeue(out var oldest))
        {
            if (_entries.TryGetValue(oldest.Key, out var entry) && entry.Sequence == oldest.Sequence)
                _entries.Remove(oldest.Key);
        }
    }

    private void TrimPending()
    {
        while (_pending.Count > _capacity && _pendingExpiry.TryDequeue(out var oldest))
        {
            if (_pending.TryGetValue(oldest.Key, out var since) && since == oldest.Since)
                _pending.Remove(oldest.Key);
        }
    }
}
