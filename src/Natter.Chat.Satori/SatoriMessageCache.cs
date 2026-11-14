namespace Natter.Chat.Satori;

/// <summary>
/// Snapshot cache used to complete partial <c>message-updated</c> events. When a snapshot
/// cannot be completed the update is skipped rather than emitting a partial message.
/// </summary>
public sealed class SatoriMessageCache
{
    private readonly Dictionary<(Source Source, string MessageId), (Message Message, long Sequence)> _messages = [];
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private long _sequence;

    public SatoriMessageCache(int capacity = 2048)
        => _capacity = Math.Max(1, capacity);

    public int Count
    {
        get
        {
            lock (_gate)
                return _messages.Count;
        }
    }

    public void Store(Source source, Message message)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            _messages[(source, message.Id)] = (message, ++_sequence);
            if (_messages.Count <= _capacity)
                return;

            var oldest = _messages.MinBy(pair => pair.Value.Sequence);
            if (oldest.Key != default)
                _messages.Remove(oldest.Key);
        }
    }

    public bool TryGet(Source source, string messageId, out Message? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(messageId))
            return false;

        lock (_gate)
        {
            if (!_messages.TryGetValue((source, messageId), out var entry))
                return false;

            message = entry.Message;
            return true;
        }
    }

    public bool TryGet(MessageKey key, out Message? message)
        => TryGet(key.Source, key.MessageId, out message);

    public bool Remove(Source source, string messageId)
    {
        lock (_gate)
            return _messages.Remove((source, messageId));
    }

    public void Clear()
    {
        lock (_gate)
            _messages.Clear();
    }
}
