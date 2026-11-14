using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Modules;
using Natter.Infrastructure.Plugins;
using Natter.Application;

namespace Natter.Infrastructure.Composition;

/// <summary>
/// The composition root. Hosts (CLI, Android, tests) describe which modules and plugin
/// directories to load; nothing here references a concrete host technology.
/// </summary>
public sealed class NatterApplicationBuilder
{
    private readonly ServiceCollection _services = new();
    private readonly PortAdapterRegistry _adapterRegistry = new();
    private readonly PluginCatalog _pluginCatalog = new();
    private readonly List<ICompositionModule> _modules = [];
    private readonly List<IPluginModule> _embeddedPlugins = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private readonly CompositionOptions _options = new();
    private Action<ILoggingBuilder>? _loggingConfiguration;
    private bool _built;

    private NatterApplicationBuilder()
    {
    }

    public static NatterApplicationBuilder Create() => new();

    public IServiceCollection Services => _services;

    public CompositionOptions Options => _options;

    public IPortAdapterCatalog AdapterCatalog => _adapterRegistry;

    public IPluginCatalog PluginCatalog => _pluginCatalog;

    /// <summary>Registers the framework's core + application modules.</summary>
    public NatterApplicationBuilder AddNatterFramework()
    {
        AddModule<CoreCompositionModule>();
        AddModule<ChatCompositionModule>();
        AddModule<ApplicationCompositionModule>();
        return this;
    }

    public NatterApplicationBuilder AddModule<TModule>()
        where TModule : ICompositionModule, new()
        => AddModule(new TModule());

    public NatterApplicationBuilder AddModule(ICompositionModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
        return this;
    }

    /// <summary>
    /// Adds a directory scanned for plugins. Each subdirectory (or the directory itself
    /// when it contains plugin.json) is loaded into its own collectible load context.
    /// </summary>
    public NatterApplicationBuilder AddPluginDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return this;

        _options.PluginDirectories.Add(Path.GetFullPath(directory));
        return this;
    }

    /// <summary>Registers a plugin compiled into the host instead of loaded from disk.</summary>
    public NatterApplicationBuilder AddEmbeddedPlugin<TPlugin>()
        where TPlugin : IPluginModule, new()
        => AddEmbeddedPlugin(new TPlugin());

    public NatterApplicationBuilder AddEmbeddedPlugin(IPluginModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _embeddedPlugins.Add(module);
        return this;
    }

    public NatterApplicationBuilder ConfigureOptions(Action<CompositionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Adds arbitrary service registrations after framework and plugin modules.</summary>
    public NatterApplicationBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurations.Add(configure);
        return this;
    }

    public NatterApplicationBuilder ConfigureLogging(Action<ILoggingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _loggingConfiguration = configure;
        return this;
    }

    public NatterHost Build()
    {
        if (_built)
            throw new InvalidOperationException("This composition builder has already been built.");

        _built = true;

        _services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            _loggingConfiguration?.Invoke(builder);
        });

        _services.AddSingleton(_options);
        _services.AddSingleton<IPortAdapterRegistry>(_adapterRegistry);
        _services.AddSingleton<IPortAdapterCatalog>(_adapterRegistry);
        _services.AddSingleton<IPluginCatalog>(_pluginCatalog);
        _services.AddSingleton<IPluginProbe, PluginProbe>();

        var loadedPlugins = LoadPluginDirectories();
        var compositionContext = new CompositionContext
        {
            Options = _options,
            Adapters = _adapterRegistry,
        };

        // Framework modules first so plugins can override default adapter bindings.
        foreach (var module in _modules)
            module.Configure(_services, compositionContext);

        foreach (var plugin in loadedPlugins)
            ConfigurePlugin(plugin.Module, plugin.Descriptor, compositionContext);

        foreach (var plugin in _embeddedPlugins)
        {
            var descriptor = _pluginCatalog.RegisterEmbedded(plugin);
            ConfigurePlugin(plugin, descriptor, compositionContext);
        }

        foreach (var configure in _serviceConfigurations)
            configure(_services);

        ServiceProvider provider;
        try
        {
            provider = _services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = _options.ValidateOnBuild,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The composition root could not be built. Check the inner exception for the service that failed validation.",
                ex);
        }

        return new NatterHost(provider, _pluginCatalog);
    }

    private List<LoadedPlugin> LoadPluginDirectories()
    {
        var loaded = new List<LoadedPlugin>();
        if (!_options.EnablePluginLoading)
            return loaded;

        var loader = new PluginLoader();

        foreach (var directory in _options.PluginDirectories
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = loader.LoadDirectory(directory);
            loaded.AddRange(_pluginCatalog.Add(result));
        }

        return loaded;
    }

    private void ConfigurePlugin(
        IPluginModule module,
        PluginDescriptor descriptor,
        CompositionContext compositionContext)
    {
        var registration = new PluginRegistrationContext(
            _services,
            _adapterRegistry,
            descriptor.Id,
            descriptor.Directory,
            descriptor.AssemblyPath,
            module.Metadata);

        try
        {
            module.Configure(registration);

            _services.AddSingleton(typeof(IPluginModule), module);
            _pluginCatalog.UpdateCapabilities(descriptor, registration.Capabilities);
        }
        catch (Exception ex)
        {
            _pluginCatalog.MarkFailed(descriptor, ex);
        }
    }
}
