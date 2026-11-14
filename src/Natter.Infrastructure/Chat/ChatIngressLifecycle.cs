using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Chat;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Bridges every registered upstream into the NLMP ingress while the host is running.
/// Subscriptions are idempotent, so re-registering an upstream does not double-deliver.
/// </summary>
public sealed class ChatIngressLifecycle : IPluginLifecycle, IDisposable
{
    private readonly ChatUpstreamRegistry _registry;
    private readonly IChatEventIngress _ingress;
    private readonly ILogger<ChatIngressLifecycle> _logger;
    private readonly Lock _gate = new();
    private readonly List<(IChatUpstream Upstream, IDisposable Subscription)> _subscriptions = [];
    private bool _started;

    public ChatIngressLifecycle(
        ChatUpstreamRegistry registry,
        IChatEventIngress ingress,
        ILogger<ChatIngressLifecycle> logger)
    {
        _registry = registry;
        _ingress = ingress;
        _logger = logger;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
                return ValueTask.CompletedTask;

            _started = true;
            _registry.UpstreamRegistered += OnUpstreamRegistered;

            foreach (var upstream in _registry.Upstreams)
                SubscribeLocked(upstream);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_started)
                return ValueTask.CompletedTask;

            _started = false;
            _registry.UpstreamRegistered -= OnUpstreamRegistered;

            foreach (var (_, subscription) in _subscriptions)
                subscription.Dispose();

            _subscriptions.Clear();
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _registry.UpstreamRegistered -= OnUpstreamRegistered;

            foreach (var (_, subscription) in _subscriptions)
                subscription.Dispose();

            _subscriptions.Clear();
            _started = false;
        }
    }

    private void OnUpstreamRegistered(IChatUpstream upstream)
    {
        lock (_gate)
        {
            if (_started)
                SubscribeLocked(upstream);
        }
    }

    private void SubscribeLocked(IChatUpstream upstream)
    {
        if (_subscriptions.Any(entry => ReferenceEquals(entry.Upstream, upstream)))
            return;

        var subscription = upstream.Events.Subscribe(new IngressObserver(upstream, _ingress, _logger));
        _subscriptions.Add((upstream, subscription));

        _logger.LogDebug("NLMP ingress subscribed to upstream {Source}.", upstream.Source.Key);
    }

    private sealed class IngressObserver(
        IChatUpstream upstream,
        IChatEventIngress ingress,
        ILogger logger) : IObserver<ChatEvent>
    {
        public void OnCompleted()
            => logger.LogInformation("NLMP event stream for {Source} completed.", upstream.Source.Key);

        public void OnError(Exception error)
            => logger.LogError(error, "NLMP event stream for {Source} failed.", upstream.Source.Key);

        public void OnNext(ChatEvent value)
        {
            try
            {
                ingress.TryPublish(value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NLMP ingress failed for event {EventId}.", value.Id);
            }
        }
    }
}
