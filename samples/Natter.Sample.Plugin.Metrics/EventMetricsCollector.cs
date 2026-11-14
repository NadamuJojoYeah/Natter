using Natter.Abstractions.Events;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>Thread-safe event counters fed by the Rx subscription.</summary>
public sealed class EventMetricsCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private long _total;
    private string? _lastEventName;
    private DateTimeOffset? _lastObservedAt;

    public void OnEvent(DomainEventEnvelope envelope)
    {
        lock (_gate)
        {
            _total++;
            _counts.TryGetValue(envelope.EventName, out var current);
            _counts[envelope.EventName] = current + 1;
            _lastEventName = envelope.EventName;
            _lastObservedAt = envelope.ObservedAt;
        }
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new MetricsSnapshot(
                _total,
                new Dictionary<string, long>(_counts, StringComparer.Ordinal),
                _lastEventName,
                _lastObservedAt);
        }
    }
}

public sealed record MetricsSnapshot(
    long Total,
    IReadOnlyDictionary<string, long> Counts,
    string? LastEventName,
    DateTimeOffset? LastObservedAt);
