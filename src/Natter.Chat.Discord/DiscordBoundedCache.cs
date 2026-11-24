namespace Natter.Chat.Discord;

/// <summary>
/// Bounded, TTL-based cache (S2-A11). The Discord transport kept interaction and channel entries
/// in plain dictionaries that were only pruned on <c>Ready</c>, so a long-running bot grew them
/// without bound. This cache enforces a hard capacity plus a per-entry expiry on every access and
/// on explicit prune.
/// </summary>
public sealed class DiscordBoundedCache<TValue>
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private long _evicted;
    private long _expired;

    public DiscordBoundedCache(int capacity, TimeSpan ttl)
    {
        _capacity = Math.Max(1, capacity);
        _ttl = ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(5);
    }

    /// <summary>Current live entry count (expired entries are removed by a prune first).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                PruneCore(DateTimeOffset.UtcNow);
                return _entries.Count;
            }
        }
    }

    /// <summary>Entries removed because the capacity was reached.</summary>
    public long EvictedCount => Interlocked.Read(ref _evicted);

    /// <summary>Entries removed because their TTL elapsed.</summary>
    public long ExpiredCount => Interlocked.Read(ref _expired);

    public bool TryGet(string key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    value = entry.Value;
                    return true;
                }

                _entries.Remove(key);
                _expired++;
            }
        }

        value = default!;
        return false;
    }

    public void Set(string key, TValue value)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneCore(now);

            if (!_entries.ContainsKey(key) && _entries.Count >= _capacity)
            {
                // Oldest entry first: the cache is advisory state, never the source of truth.
                var oldest = default(KeyValuePair<string, Entry>);
                var hasOldest = false;
                foreach (var pair in _entries)
                {
                    if (!hasOldest || pair.Value.ExpiresAt < oldest.Value.ExpiresAt)
                    {
                        oldest = pair;
                        hasOldest = true;
                    }
                }

                if (hasOldest && _entries.Remove(oldest.Key))
                    _evicted++;
            }

            _entries[key] = new Entry(value, now.Add(_ttl));
        }
    }

    /// <summary>Drops expired entries; called on Ready and on capacity pressure.</summary>
    public void Prune()
    {
        lock (_gate)
            PruneCore(DateTimeOffset.UtcNow);
    }

    public bool Remove(string key)
    {
        lock (_gate)
            return _entries.Remove(key);
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void PruneCore(DateTimeOffset now)
    {
        List<string>? stale = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt > now)
                continue;

            stale ??= [];
            stale.Add(pair.Key);
        }

        if (stale is null)
            return;

        foreach (var key in stale)
        {
            if (_entries.Remove(key))
                _expired++;
        }
    }

    private readonly record struct Entry(TValue Value, DateTimeOffset ExpiresAt);
}
