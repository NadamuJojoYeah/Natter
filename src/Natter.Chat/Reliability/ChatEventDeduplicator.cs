namespace Natter.Chat;

/// <summary>
/// Deduplicates replayed events. The primary key is the event id; message creation also
/// gets the recommended <c>(source, conversation, message, "created")</c> key so an adapter
/// that derives a different envelope id cannot trigger a second AI reply.
/// </summary>
public sealed class ChatEventDeduplicator
{
    private sealed record Entry(DateTimeOffset ExpiresAt, long Sequence);

    private readonly Dictionary<string, Entry> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTimeOffset ExpiresAt, long Sequence)> _expiry = new();
    private readonly Queue<(string Key, long Sequence)> _insertion = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;
    private long _lateEvents;

    public ChatEventDeduplicator(
        TimeSpan? window = null,
        int capacity = 10_000,
        Func<DateTimeOffset>? clock = null)
    {
        // S2-A12: the default window must cover the longest platform replay window (DingTalk:
        // 60 minutes). A shorter window let replays after 10 minutes through.
        _window = window ?? TimeSpan.FromMinutes(60);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The deduplication window must be positive.");

        _capacity = Math.Max(1, capacity);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan Window => _window;

    /// <summary>
    /// Accepted events whose age exceeded the window: dedupe could not have seen a replay of them
    /// and such events are the candidates for window-gap pass-through (S2-A12).
    /// </summary>
    public long LateEventCount => Interlocked.Read(ref _lateEvents);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Prune(_clock());
                return _seen.Count;
            }
        }
    }

    /// <summary>Returns true when the event is new and should be processed.</summary>
    public bool TryAccept(ChatEvent chatEvent)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        var keys = BuildKeys(chatEvent);

        lock (_gate)
        {
            var now = _clock();
            Prune(now);

            if (keys.Any(_seen.ContainsKey))
                return false;

            if (now - chatEvent.OccurredAt > _window)
                Interlocked.Increment(ref _lateEvents);

            var expiresAt = now + _window;
            foreach (var key in keys)
            {
                var sequence = ++_sequence;
                _seen[key] = new Entry(expiresAt, sequence);
                _expiry.Enqueue((key, expiresAt, sequence));
                _insertion.Enqueue((key, sequence));
            }

            TrimToCapacity();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _seen.Clear();
    }

    private static IReadOnlyList<string> BuildKeys(ChatEvent chatEvent)
    {
        var keys = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(chatEvent.Id))
            keys.Add(Key("id", chatEvent.Id));

        if (chatEvent is MessageCreatedEvent { Payload.Message: { } message })
        {
            // Structured components separated by a unit separator: no reverse parsing of
            // the display key and no ambiguity when ids contain ':'.
            keys.Add(Key(
                "message",
                chatEvent.Source.Protocol,
                chatEvent.Source.Platform,
                chatEvent.Source.SelfId,
                message.Conversation.Id,
                message.Id,
                "created"));
        }

        if (keys.Count == 0)
        {
            keys.Add(Key(
                "fallback",
                chatEvent.Source.Protocol,
                chatEvent.Source.Platform,
                chatEvent.Source.SelfId,
                chatEvent.Type,
                chatEvent.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return keys;
    }

    private static string Key(params string[] parts) => string.Join('\u001F', parts);

    private void Prune(DateTimeOffset now)
    {
        // S2-A14: incremental expiry via a FIFO of insertion-time deadlines. Each accepted event
        // pays O(expired), not O(total); the lock is never held for a full-table scan.
        while (_expiry.TryDequeue(out var entry))
        {
            if (entry.ExpiresAt > now)
            {
                _expiry.Enqueue(entry);
                break;
            }

            if (_seen.TryGetValue(entry.Key, out var current)
                && current.Sequence == entry.Sequence
                && current.ExpiresAt <= now)
                _seen.Remove(entry.Key);
        }
    }

    private void TrimToCapacity()
    {
        // LRU-ish eviction by insertion order; TTL is never shortened to fit capacity.
        while (_seen.Count > _capacity && _insertion.TryDequeue(out var oldest))
        {
            if (_seen.TryGetValue(oldest.Key, out var current) && current.Sequence == oldest.Sequence)
                _seen.Remove(oldest.Key);
        }
    }
}
