using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>
/// Plugin lifecycle hook. Subscribes to the same Rx stream as the host and keeps the
/// five most recent domain events for the `greetings recent` command.
/// </summary>
public sealed class RecentEventTracker : IPluginLifecycle, IDisposable
{
    private readonly IDomainEventStream _stream;
    private readonly RecentEventBuffer _buffer;
    private IDisposable? _subscription;

    public RecentEventTracker(IDomainEventStream stream, RecentEventBuffer buffer)
    {
        _stream = stream;
        _buffer = buffer;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _subscription ??= _stream.Subscribe(envelope => _buffer.Add(envelope));
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
