using Natter.Abstractions.Events;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>Bounded buffer filled by the plugin's Rx subscription.</summary>
public sealed class RecentEventBuffer
{
    private const int Capacity = 5;
    private readonly object _gate = new();
    private readonly Queue<DomainEventEnvelope> _events = new();

    public void Add(DomainEventEnvelope envelope)
    {
        lock (_gate)
        {
            _events.Enqueue(envelope);
            while (_events.Count > Capacity)
                _events.Dequeue();
        }
    }

    public IReadOnlyList<DomainEventEnvelope> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }
}
