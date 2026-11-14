using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;

namespace Natter.Sample.Plugin.Metrics;

/// <summary>Subscribes to the domain event stream when the host starts.</summary>
public sealed class MetricsLifecycle : IPluginLifecycle, IDisposable
{
    private readonly IDomainEventStream _stream;
    private readonly EventMetricsCollector _collector;
    private IDisposable? _subscription;

    public MetricsLifecycle(IDomainEventStream stream, EventMetricsCollector collector)
    {
        _stream = stream;
        _collector = collector;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _subscription ??= _stream.Subscribe(envelope => _collector.OnEvent(envelope));
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
