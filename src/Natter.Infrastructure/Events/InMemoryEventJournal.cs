using Natter.Abstractions.Events;

namespace Natter.Infrastructure.Events;

/// <summary>
/// Subscribes to the domain event stream and keeps a bounded in-memory ring buffer.
/// The host materializes it on startup so `events list` always has data.
/// </summary>
public sealed class InMemoryEventJournal : IEventJournal, IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly List<DomainEventEnvelope> _events = [];
    private readonly IDisposable _subscription;
    private readonly int _capacity;
    private bool _disposed;

    public InMemoryEventJournal(IDomainEventStream stream, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _capacity = Math.Max(1, capacity);
        _subscription = stream.Subscribe(new JournalObserver(this));
    }

    public int Count
    {
        get
        {
            lock (_gate) return _events.Count;
        }
    }

    public IReadOnlyList<DomainEventEnvelope> Snapshot(int? limit = null)
    {
        lock (_gate)
        {
            if (limit is null or <= 0 || limit.Value >= _events.Count)
                return _events.ToArray();

            return _events.Skip(_events.Count - limit.Value).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
    }

    private void Append(DomainEventEnvelope envelope)
    {
        lock (_gate)
        {
            _events.Add(envelope);
            if (_events.Count > _capacity)
                _events.RemoveRange(0, _events.Count - _capacity);
        }
    }

    private sealed class JournalObserver(InMemoryEventJournal owner) : IObserver<DomainEventEnvelope>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(DomainEventEnvelope value) => owner.Append(value);
    }
}
