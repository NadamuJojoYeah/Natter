namespace Natter.Chat;

/// <summary>
/// Deduplicates replayed events. The primary key is the event id; message creation also
/// gets the recommended <c>(source, conversation, message, "created")</c> key so an adapter
/// that derives a different envelope id cannot trigger a second AI reply.
/// </summary>
public sealed class ChatEventDeduplicator
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _clock;

    public ChatEventDeduplicator(
        TimeSpan? window = null,
        int capacity = 10_000,
        Func<DateTimeOffset>? clock = null)
    {
        _window = window ?? TimeSpan.FromMinutes(10);
        if (_window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "The deduplication window must be positive.");

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

            var expiresAt = now + _window;
            foreach (var key in keys)
                _seen[key] = expiresAt;

            if (_seen.Count > _capacity)
                TrimToCapacity();

            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _seen.Clear();
    }

    private IReadOnlyList<string> BuildKeys(ChatEvent chatEvent)
    {
        var keys = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(chatEvent.Id))
            keys.Add("id:" + chatEvent.Id);

        if (chatEvent is MessageCreatedEvent { Payload.Message: { } message })
        {
            keys.Add($"message:{chatEvent.Source.Key}:{message.Conversation.Id}:{message.Id}:created");
        }

        if (keys.Count == 0)
            keys.Add($"fallback:{chatEvent.Source.Key}:{chatEvent.Type}:{chatEvent.OccurredAt:O}");

        return keys;
    }

    private void Prune(DateTimeOffset now)
    {
        if (_seen.Count == 0)
            return;

        var expired = _seen
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expired)
            _seen.Remove(key);
    }

    private void TrimToCapacity()
    {
        var excess = _seen.Count - _capacity;
        if (excess <= 0)
            return;

        foreach (var key in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToList())
            _seen.Remove(key);
    }
}
