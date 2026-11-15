using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Modules;
using Natter.Infrastructure.Plugins;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
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
    private readonly List<(PluginManifest Manifest, INatterPlugin Plugin, string? Directory)> _embeddedNpsPlugins = [];
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];
    private readonly NpsPluginOptions _npsOptions = new();
    private readonly NpsPluginRuntime _npsRuntime;
    private readonly CompositionOptions _options = new();
    private Action<ILoggingBuilder>? _loggingConfiguration;
    private bool _built;

    private NatterApplicationBuilder()
    {
        _npsRuntime = new NpsPluginRuntime(_npsOptions, _pluginCatalog, _adapterRegistry);
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

    /// <summary>Configures NPS v1 host policy (trust, permissions, secrets).</summary>
    public NatterApplicationBuilder ConfigurePlugins(Action<NpsPluginOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_npsOptions);
        return this;
    }

    /// <summary>Registers an NPS v1 plugin compiled into the host instead of loaded from disk.</summary>
    public NatterApplicationBuilder AddEmbeddedNpsPlugin(
        PluginManifest manifest,
        INatterPlugin plugin,
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plugin);
        _embeddedNpsPlugins.Add((manifest, plugin, directory));
        return this;
    }

    /// <summary>Registers a legacy plugin compiled into the host instead of loaded from disk.</summary>
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

        _npsOptions.ConfigDirectory = _options.PluginConfigDirectory;

        var scan = ScanPlugins();
        var loadedPlugins = scan.LegacyPlugins;

        foreach (var failure in scan.Failures)
            _pluginCatalog.RegisterDescriptor(failure);

        foreach (var embedded in _embeddedNpsPlugins)
        {
            var directory = Path.GetFullPath(embedded.Directory ?? AppContext.BaseDirectory);
            var validation = PluginManifestReader.ValidateManifest(embedded.Manifest, directory);
            if (validation.Errors.Count > 0)
            {
                var message = string.Join("; ", validation.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));
                _pluginCatalog.RegisterDescriptor(new PluginDescriptor(
                    embedded.Manifest.Id,
                    embedded.Manifest.Name,
                    embedded.Manifest.Version,
                    "<embedded>",
                    directory,
                    PluginState.Failed,
                    message,
                    embedded.Manifest.Capabilities,
                    embedded.Manifest.Description,
                    IsDynamic: true,
                    Kind: embedded.Manifest.Kind.ToString().ToLowerInvariant(),
                    HostApi: embedded.Manifest.HostApi,
                    ErrorDetail: new PluginErrorDescriptor("manifest", "manifest-invalid", message)));
                continue;
            }

            scan.Candidates.Add(NpsPluginCandidate.FromEmbedded(embedded.Manifest, embedded.Plugin, directory));
        }

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
            scan.LegacyDescriptors.Add(descriptor);
            ConfigurePlugin(plugin, descriptor, compositionContext);
        }

        _npsRuntime.Prepare(
            scan.Candidates,
            scan.LegacyDescriptors,
            _services,
            (module, descriptor) => ConfigurePlugin(module, descriptor, compositionContext));

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

        _npsRuntime.Attach(provider, provider.GetRequiredService<ILoggerFactory>());
        return new NatterHost(provider, _pluginCatalog, _npsRuntime);
    }

    private PluginScanResult ScanPlugins()
    {
        var result = new PluginScanResult();
        if (!_options.EnablePluginLoading)
            return result;

        var loader = new PluginLoader(_npsOptions.LoadContextOptions);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _options.PluginDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                result.Failures.Add(new PluginDescriptor(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(root)),
                    "0.0.0",
                    string.Empty,
                    root,
                    PluginState.Failed,
                    "Plugin directory does not exist.",
                    ErrorDetail: new PluginErrorDescriptor("manifest", "directory-missing", "Plugin directory does not exist.")));
                continue;
            }

            foreach (var pluginDirectory in PluginDirectoryScanner.Discover(root))
            {
                var fullPath = Path.GetFullPath(pluginDirectory);
                if (!visited.Add(fullPath))
                    continue;

                var read = PluginManifestReader.Read(fullPath);
                if (read.IsNps)
                {
                    if (!read.IsValid || read.Manifest is null)
                    {
                        result.Failures.Add(CreateNpsFailure(fullPath, read));
                        continue;
                    }

                    var sensitiveFiles = PluginPackageInspector.FindSensitiveFiles(fullPath);
                    if (sensitiveFiles.Count > 0)
                    {
                        var message = $"Plugin package contains sensitive files: {string.Join(", ", sensitiveFiles)}.";
                        result.Failures.Add(new PluginDescriptor(
                            read.Manifest.Id,
                            read.Manifest.Name,
                            read.Manifest.Version,
                            read.Manifest.Entry?.Assembly ?? string.Empty,
                            fullPath,
                            PluginState.Failed,
                            message,
                            read.Manifest.Capabilities,
                            read.Manifest.Description,
                            Kind: read.Manifest.Kind.ToString().ToLowerInvariant(),
                            HostApi: read.Manifest.HostApi,
                            ErrorDetail: new PluginErrorDescriptor("manifest", "sensitive-file", message)));
                        continue;
                    }

                    result.Candidates.Add(NpsPluginCandidate.FromManifest(
                        read.Manifest,
                        fullPath,
                        read,
                        _npsOptions.FilePluginTrustLevel));
                    continue;
                }

                var loaded = loader.LoadPluginDirectory(fullPath);
                var accepted = _pluginCatalog.Add(loaded);
                result.LegacyPlugins.AddRange(accepted);
                result.LegacyDescriptors.AddRange(accepted.Select(plugin => plugin.Descriptor));
                // Legacy loader failures are already registered through PluginCatalog.Add.
            }
        }

        return result;
    }

    private static PluginDescriptor CreateNpsFailure(string directory, PluginManifestReadResult read)
    {
        var manifest = read.Manifest;
        var id = string.IsNullOrWhiteSpace(manifest?.Id)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))
            : manifest!.Id;
        var message = string.Join("; ", read.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));

        return new PluginDescriptor(
            id,
            manifest?.Name ?? id,
            manifest?.Version ?? "0.0.0",
            manifest?.Entry?.Assembly ?? string.Empty,
            directory,
            PluginState.Failed,
            message,
            manifest?.Capabilities,
            manifest?.Description,
            Kind: manifest?.Kind.ToString().ToLowerInvariant(),
            HostApi: manifest?.HostApi,
            ErrorDetail: new PluginErrorDescriptor("manifest", "manifest-invalid", message));
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
