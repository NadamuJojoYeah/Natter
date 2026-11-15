using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Plugins;
using Natter.Infrastructure.Plugins.Nps;

namespace Natter.Infrastructure.Composition;

/// <summary>
/// A composed Natter application. Owns the DI container, the plugin load contexts and
/// the lifecycle of every discovered <see cref="IPluginLifecycle"/>.
/// </summary>
public sealed class NatterHost : IAsyncDisposable
{
    private readonly ServiceProvider _rootProvider;
    private readonly PluginCatalog _pluginCatalog;
    private readonly NpsPluginRuntime? _npsRuntime;
    private readonly ILogger<NatterHost> _logger;
    private readonly List<IPluginLifecycle> _startedLifecycles = [];
    private bool _started;
    private bool _disposed;

    internal NatterHost(ServiceProvider rootProvider, PluginCatalog pluginCatalog, NpsPluginRuntime? npsRuntime = null)
    {
        _rootProvider = rootProvider;
        _pluginCatalog = pluginCatalog;
        _npsRuntime = npsRuntime;
        _logger = rootProvider.GetRequiredService<ILogger<NatterHost>>();
    }

    public IServiceProvider Services => _rootProvider;

    public IPluginCatalog Plugins => _pluginCatalog;

    public IPortAdapterCatalog Adapters => _rootProvider.GetRequiredService<IPortAdapterCatalog>();

    public IReadOnlyList<PluginDescriptor> PluginDescriptors => _pluginCatalog.Plugins;

    public IServiceScope CreateScope() => _rootProvider.CreateScope();

    /// <summary>
    /// Materializes the event journal and starts every plugin lifecycle hook
    /// (used by plugins to subscribe to the Rx domain event stream).
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;

        _started = true;

        // Force journal subscription before any event can be published.
        _ = _rootProvider.GetService(typeof(IEventJournal));

        foreach (var lifecycle in _rootProvider.GetServices<IPluginLifecycle>())
        {
            try
            {
                await lifecycle.StartAsync(_rootProvider, cancellationToken).ConfigureAwait(false);
                _startedLifecycles.Add(lifecycle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin lifecycle {Lifecycle} failed to start.", lifecycle.GetType().FullName);
            }
        }

        // Activate NPS plugins and connect their upstreams after host lifecycles have
        // subscribed (ChatIngressLifecycle attaches to upstreams at registration).
        if (_npsRuntime is not null)
            await _npsRuntime.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Natter host started with {PluginCount} plugin(s) and {LifecycleCount} lifecycle hook(s).",
            _pluginCatalog.Plugins.Count,
            _startedLifecycles.Count);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started) return;

        if (_npsRuntime is not null)
            await _npsRuntime.StopAsync(cancellationToken).ConfigureAwait(false);

        for (var index = _startedLifecycles.Count - 1; index >= 0; index--)
        {
            try
            {
                await _startedLifecycles[index].StopAsync(_rootProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin lifecycle {Lifecycle} failed to stop.", _startedLifecycles[index].GetType().FullName);
            }
        }

        _startedLifecycles.Clear();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await StopAsync().ConfigureAwait(false);
        _disposed = true;

        if (_npsRuntime is not null)
            await _npsRuntime.DisposeAsync().ConfigureAwait(false);

        await _rootProvider.DisposeAsync().ConfigureAwait(false);
        _pluginCatalog.Dispose();
    }
}
