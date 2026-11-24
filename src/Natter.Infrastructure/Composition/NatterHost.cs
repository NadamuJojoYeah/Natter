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
        if (_started)
            return;

        try
        {
            _rootProvider.GetService<Natter.Application.Chat.HostRuntimeClock>()?.MarkStarted();

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
            {
                try
                {
                    await _npsRuntime.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The NPS plugin runtime failed to start.");
                }
            }

            if (_rootProvider.GetService(typeof(Natter.Application.Chat.IRuntimeProfileStore)) is Natter.Application.Chat.IRuntimeProfileStore profileStore
                && _rootProvider.GetService(typeof(Natter.Application.Chat.IChatResponderRegistry)) is Natter.Application.Chat.IChatResponderRegistry responderRegistry)
            {
                var components = _rootProvider.GetService(typeof(Natter.Application.Chat.IContextComponentCatalog))
                    as Natter.Application.Chat.IContextComponentCatalog;
                // S2-B2: validation happens before the host is marked started; a failure rolls back
                // every already-started lifecycle/upstream and stays retryable.
                Natter.Application.Chat.RuntimeProfileValidator.Validate(profileStore, responderRegistry, components);
            }

            _started = true;
            _logger.LogInformation(
                "Natter host started with {PluginCount} plugin(s) and {LifecycleCount} lifecycle hook(s).",
                _pluginCatalog.Plugins.Count,
                _startedLifecycles.Count);
        }
        catch
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        _started = false;
    }

    private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
    {
        if (_npsRuntime is not null)
        {
            try
            {
                await _npsRuntime.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The NPS plugin runtime failed to stop.");
            }
        }

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
