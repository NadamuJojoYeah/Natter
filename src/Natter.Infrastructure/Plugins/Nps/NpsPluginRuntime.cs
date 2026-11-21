using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Application.Admin;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Admin;
using Natter.Infrastructure.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>
/// NPS v1 host runtime: manifest graph, plugin-local DI, staged contributions, activation
/// with rollback, host-driven upstream lifecycle and diagnostics.
/// </summary>
internal sealed partial class NpsPluginRuntime : INpsPluginRuntimeAccessor, IPluginRuntimeDiagnostics
{
    private readonly NpsPluginOptions _options;
    private readonly PluginCatalog _catalog;
    private readonly IPortAdapterRegistry _adapterRegistry;
    private readonly List<NpsPluginCandidate> _candidates = [];
    private readonly List<PluginDescriptor> _legacyDescriptors = [];
    private readonly Dictionary<string, PluginState> _legacyStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _blockedBy = new(StringComparer.Ordinal);
    private readonly List<NpsPluginCandidate> _startedOrder = [];
    private readonly Lock _gate = new();
    private NpsGraphResult? _graph;
    private NpsPortAdapterRouter? _portRouter;
    private IServiceProvider? _hostProvider;
    private ILoggerFactory? _loggerFactory;
    private bool _started;
    private bool _disposed;

    public NpsPluginRuntime(
        NpsPluginOptions options,
        PluginCatalog catalog,
        IPortAdapterRegistry adapterRegistry)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _adapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
    }

    /// <summary>
    /// Validates graph/config/permissions, loads and configures plugins, and registers host
    /// bridges. Called before the host service provider is built.
    /// </summary>
    public void Prepare(
        IReadOnlyList<NpsPluginCandidate> candidates,
        IReadOnlyList<PluginDescriptor> legacyDescriptors,
        IServiceCollection services,
        Action<IPluginModule, PluginDescriptor> configureLegacyPlugin)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(legacyDescriptors);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureLegacyPlugin);

        _candidates.AddRange(candidates);
        _legacyDescriptors.AddRange(legacyDescriptors);

        foreach (var descriptor in legacyDescriptors)
            _legacyStates[descriptor.Id] = descriptor.State;

        foreach (var candidate in _candidates)
        {
            candidate.InitializeDescriptor();
            _catalog.RegisterDescriptor(candidate.Descriptor);
        }

        var inputs = new List<NpsGraphInput>();
        inputs.AddRange(legacyDescriptors.Select(descriptor => new NpsGraphInput(
            descriptor.Id,
            descriptor.Version,
            "legacy",
            descriptor.State,
            Dependencies: null,
            OptionalDependencies: null,
            LoadAfter: null,
            LoadBefore: null,
            Conflicts: null,
            Candidate: null)));

        inputs.AddRange(_candidates.Select(candidate => new NpsGraphInput(
            candidate.Manifest.Id,
            candidate.Manifest.Version,
            candidate.Manifest.Kind.ToString().ToLowerInvariant(),
            candidate.State,
            candidate.Manifest.DependsOn,
            candidate.Manifest.OptionalDependsOn,
            candidate.Manifest.LoadAfter,
            candidate.Manifest.LoadBefore,
            candidate.Manifest.Conflicts,
            candidate)));

        _graph = NpsPluginGraph.Build(inputs, SemanticVersion.Parse(NatterPluginApi.Version));

        foreach (var candidate in _candidates)
        {
            if (_graph.Errors.TryGetValue(candidate.Id, out var graphError))
            {
                ApplyGraphError(candidate, graphError);
                continue;
            }

            if (_options.DisabledPlugins.Contains(candidate.Id))
            {
                ApplyGraphError(candidate, new PluginErrorDescriptor(
                    "manifest",
                    "disabled",
                    $"Plugin '{candidate.Id}' is disabled by host policy."));
                continue;
            }

            TryPrepareCandidate(candidate, configureLegacyPlugin);
        }

        DetectDuplicateContributions();
        BlockDependents();
        RegisterHostBridges(services);
    }

    /// <summary>Sets the host provider/logger factory after Build.</summary>
    public void Attach(IServiceProvider hostProvider, ILoggerFactory loggerFactory)
    {
        _hostProvider = hostProvider ?? throw new ArgumentNullException(nameof(hostProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;

        _started = true;

        if (_hostProvider is null)
            throw new InvalidOperationException("The NPS runtime has not been attached to a host provider.");

        var order = _graph?.Order ?? [];
        foreach (var pluginId in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = _candidates.FirstOrDefault(item => item.Id == pluginId);
            if (candidate is null || candidate.State != PluginState.Configured)
                continue;

            await StartCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (candidate.State == PluginState.Activated)
                await ConnectUpstreamsAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        _started = false;

        for (var index = _startedOrder.Count - 1; index >= 0; index--)
        {
            var candidate = _startedOrder[index];

            try
            {
                await StopCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Stop is best-effort; a broken plugin must not keep the host alive.
            }
        }

        _startedOrder.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopAsync().ConfigureAwait(false);
        _disposed = true;

        foreach (var candidate in _candidates)
        {
            await candidate.Lifetime.DisposeAsync().ConfigureAwait(false);
            candidate.DisposeLoadContext();
        }

        _portRouter = null;
        _hostProvider = null;
    }

    public PluginState GetPluginState(string pluginId)
    {
        lock (_gate)
        {
            var candidate = _candidates.FirstOrDefault(item => item.Id == pluginId);
            return candidate?.State
                   ?? (_legacyStates.TryGetValue(pluginId, out var legacyState) ? legacyState : PluginState.Discovered);
        }
    }

    public IServiceProvider? GetPluginProvider(string pluginId)
    {
        lock (_gate)
            return _candidates.FirstOrDefault(item => item.Id == pluginId)?.Provider;
    }

    public IReadOnlyList<CliCommandDescriptor> GetCliCommands()
    {
        var commands = new List<CliCommandDescriptor>();

        foreach (var candidate in _candidates)
        {
            if (candidate.State != PluginState.Activated || candidate.Provider is null)
                continue;

            foreach (var registration in candidate.Contributions?.Registrations ?? [])
            {
                if (!string.Equals(registration.Type, PluginContributionTypes.CliCommand, StringComparison.Ordinal)
                    || registration.Command is null)
                {
                    continue;
                }

                var original = registration.Command.HandlerFactory;
                var provider = candidate.Provider;

                commands.Add(new CliCommandDescriptor
                {
                    Name = registration.Command.Name,
                    Description = registration.Command.Description,
                    Usage = registration.Command.Usage,
                    Aliases = registration.Command.Aliases,
                    Options = registration.Command.Options,
                    Arguments = registration.Command.Arguments,
                    Origin = $"plugin:{candidate.Id}",
                    HandlerFactory = _ => original(provider),
                });
            }
        }

        return commands;
    }

    private void ApplyGraphError(NpsPluginCandidate candidate, PluginErrorDescriptor error)
    {
        var state = error.Code switch
        {
            "disabled" => PluginState.Disabled,
            "platform-unsupported" => PluginState.Skipped,
            "duplicate-plugin-id" or "host-api-incompatible" or "invalid-host-api" or "plugin-conflict" => PluginState.Failed,
            _ => PluginState.Blocked,
        };

        candidate.Error = error;
        candidate.State = state;
        candidate.SetContributionStatus("unavailable");
        candidate.Descriptor = candidate.Descriptor with
        {
            State = state,
            Error = $"{error.Code}: {error.Message}",
            ErrorDetail = error,
        };
        _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
    }

    private void TryPrepareCandidate(NpsPluginCandidate candidate, Action<IPluginModule, PluginDescriptor> configureLegacyPlugin)
    {
        try
        {
            if (!ShouldLoad(candidate))
                return;

            if (candidate.Manifest.HostApi is null
                || !VersionRange.TryParse(candidate.Manifest.HostApi, out _))
            {
                SetFailure(candidate, "manifest", "invalid-host-api", "Manifest hostApi is not a valid SemVer range.");
                return;
            }

            candidate.Permissions = NpsPermissionPolicy.Decide(candidate.Manifest, candidate.Trust, _options);
            candidate.Descriptor = candidate.Descriptor with
            {
                Permissions = candidate.Permissions.Requested,
                GrantedPermissions = candidate.Permissions.Granted,
            };

            if (!ValidateRequiredSecrets(candidate))
                return;

            if (!LoadConfig(candidate))
                return;

            candidate.SetState(PluginState.Validated);
            _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);

            candidate.Log = new NpsPluginLog(candidate.Id);
            candidate.Dependencies = new NpsPluginDependencies(
                GetPluginState,
                pluginId => GetPluginVersion(pluginId));

            if (!LoadEntry(candidate, configureLegacyPlugin))
                return;

            if (candidate.IsLegacyEntry)
                return;

            candidate.SetState(PluginState.Loaded);
            _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);

            candidate.Contributions = new NpsContributionRegistry(
                candidate.Id,
                candidate.Manifest,
                candidate.Permissions.Granted,
                candidate.Services);

            candidate.Context = new NpsPluginContext(
                candidate.Manifest,
                candidate.Config!,
                candidate.Secrets!,
                candidate.Log,
                candidate.Lifetime,
                candidate.Services,
                candidate.Contributions,
                candidate.Dependencies);

            RunSynchronously(candidate.Plugin!.ConfigureAsync(candidate.Context));

            var missing = candidate.Contributions.FindMissingContributions();
            if (missing.Count > 0)
            {
                SetFailure(
                    candidate,
                    "configure",
                    "missing-contribution",
                    $"Declared contributions were not registered: {string.Join(", ", missing)}.");
                return;
            }

            candidate.UpdateContributions(candidate.Contributions.Registrations);
            candidate.Warnings.AddRange(candidate.Contributions.Warnings);
            candidate.SetState(PluginState.Configured);
            _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
        }
        catch (PluginPermissionException ex)
        {
            SetFailure(candidate, "permission", "permission-denied", ex.Message);
        }
        catch (Exception ex)
        {
            SetFailure(candidate, "configure", "configure-failed", ex.Message, ex);
        }
    }

    private bool ShouldLoad(NpsPluginCandidate candidate)
    {
        if (candidate.Trust == PluginTrustLevel.Untrusted && !_options.LoadUntrustedPlugins)
        {
            ApplyGraphError(candidate, new PluginErrorDescriptor(
                "permission",
                "untrusted-disabled",
                $"Untrusted plugin '{candidate.Id}' is not allowed by host policy."));
            return false;
        }

        if (candidate.Trust is PluginTrustLevel.Local or PluginTrustLevel.Signed && !_options.AllowLocalPlugins)
        {
            ApplyGraphError(candidate, new PluginErrorDescriptor(
                "permission",
                "local-disabled",
                $"Local plugin '{candidate.Id}' is not allowed by host policy."));
            return false;
        }

        return true;
    }

    private bool ValidateRequiredSecrets(NpsPluginCandidate candidate)
    {
        candidate.Secrets = new NpsPluginSecrets(
            candidate.Id,
            _options.SecretProvider,
            candidate.Permissions?.Granted ?? []);

        foreach (var secret in candidate.Manifest.Secrets ?? [])
        {
            if (!secret.Required)
                continue;

            if (candidate.Permissions?.Granted.Contains(PluginPermissions.Secret(secret.Name)) != true)
            {
                SetFailure(
                    candidate,
                    "permission",
                    "secret-permission-denied",
                    $"Plugin '{candidate.Id}' does not have permission 'secrets:{secret.Name}'.");
                return false;
            }

            if (!_options.SecretProvider.TryGet(secret.Name, out var value) || string.IsNullOrEmpty(value))
            {
                SetFailure(
                    candidate,
                    "config",
                    "missing-secret",
                    $"Required secret '{secret.Name}' for plugin '{candidate.Id}' is missing.",
                    detail: new Dictionary<string, string>(StringComparer.Ordinal) { ["secret"] = secret.Name });
                return false;
            }
        }

        return true;
    }

    private bool LoadConfig(NpsPluginCandidate candidate)
    {
        var load = NpsPluginConfigLoader.Load(candidate.Manifest, candidate.Directory, _options.ConfigDirectory);
        candidate.ConfigLoad = load;

        if (!load.Valid)
        {
            var issueText = string.Join(
                "; ",
                load.Issues.Select(issue => $"{issue.Pointer}: {issue.Message}"));
            SetFailure(candidate, "config", "invalid-config", issueText);
            return false;
        }

        candidate.Config = new NpsPluginConfig(candidate.Id, load.Effective);
        return true;
    }

    private bool LoadEntry(NpsPluginCandidate candidate, Action<IPluginModule, PluginDescriptor> configureLegacyPlugin)
    {
        if (candidate.Plugin is not null)
        {
            candidate.SetState(PluginState.Loaded);
            _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
            return true;
        }

        if (!File.Exists(candidate.AssemblyPath))
        {
            SetFailure(candidate, "load", "entry-missing", $"Entry assembly '{candidate.Manifest.Entry.Assembly}' was not found.");
            return false;
        }

        try
        {
            candidate.LoadContext = new PluginLoadContext(candidate.AssemblyPath, candidate.Id, _options.LoadContextOptions);
            var assembly = candidate.LoadContext.LoadFromAssemblyPath(candidate.AssemblyPath);
            var type = assembly.GetType(candidate.Manifest.Entry.Type, throwOnError: false, ignoreCase: false);

            if (type is null)
            {
                SetFailure(
                    candidate,
                    "load",
                    "entry-type-missing",
                    $"Entry type '{candidate.Manifest.Entry.Type}' was not found in '{candidate.Manifest.Entry.Assembly}'.");
                return false;
            }

            candidate.EntryType = type;

            if (typeof(INatterPlugin).IsAssignableFrom(type))
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    SetFailure(candidate, "load", "invalid-entry-type", $"Entry type '{type.FullName}' must be concrete.");
                    return false;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    SetFailure(
                        candidate,
                        "load",
                        "constructor-required",
                        $"Entry type '{type.FullName}' needs a public parameterless constructor in NPS v1.");
                    return false;
                }

                candidate.Plugin = (INatterPlugin)Activator.CreateInstance(type)!;
                candidate.SetState(PluginState.Loaded);
                _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
                return true;
            }

            if (typeof(IPluginModule).IsAssignableFrom(type))
            {
                var module = (IPluginModule)Activator.CreateInstance(type)!;
                candidate.Descriptor = candidate.Descriptor with
                {
                    Kind = "legacy",
                    State = PluginState.Loaded,
                };

                configureLegacyPlugin(module, candidate.Descriptor);
                candidate.State = PluginState.Loaded;
                candidate.IsLegacyEntry = true;
                candidate.Warnings.Add("NPS manifest points at a legacy IPluginModule; NPS contributions are not used.");
                _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
                return true;
            }

            SetFailure(
                candidate,
                "load",
                "invalid-entry-type",
                $"Entry type '{type.FullName}' must implement INatterPlugin or IPluginModule.");
            return false;
        }
        catch (Exception ex)
        {
            var diagnostics = candidate.LoadContext?.Diagnostics;
            var message = diagnostics is { Count: > 0 }
                ? ex.Message + " Resolution: " + string.Join(" | ", diagnostics)
                : ex.Message;
            SetFailure(candidate, "load", "load-failed", message, ex);
            return false;
        }
    }

    private async ValueTask StartCandidateAsync(NpsPluginCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var provider = BuildPluginProvider(candidate);
            if (provider is null)
                return;

            candidate.Provider = provider;
            candidate.Services.Provider = provider;
            candidate.Log.Attach(_loggerFactory!.CreateLogger($"Natter.Plugin.{candidate.Id}"));

            if (candidate.Contributions is not null)
            {
                var missing = candidate.Contributions.FindMissingContributions();
                if (missing.Count > 0)
                {
                    await FailDuringActivationAsync(
                        candidate,
                        new PluginErrorDescriptor(
                            "configure",
                            "missing-contribution",
                            $"Declared contributions were not registered: {string.Join(", ", missing)}."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                CommitChatContributions(candidate, provider);
            }

            await candidate.Plugin!.ActivateAsync(candidate.Context!, cancellationToken).ConfigureAwait(false);
            candidate.SetState(PluginState.Activated);
            candidate.SetContributionStatus("active");
            _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
            _startedOrder.Add(candidate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FailDuringActivationAsync(
                candidate,
                new PluginErrorDescriptor("activate", "activate-failed", ex.Message),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ConnectUpstreamsAsync(NpsPluginCandidate candidate, CancellationToken cancellationToken)
    {
        var contributions = GetActiveContributions(candidate);
        if (contributions is null)
            return;

        foreach (var upstream in contributions.Upstreams)
        {
            try
            {
                await upstream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await FailDuringActivationAsync(
                    candidate,
                    new PluginErrorDescriptor("activate", "upstream-connect-failed", ex.Message),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private ServiceProvider? BuildPluginProvider(NpsPluginCandidate candidate)
    {
        if (_hostProvider is null)
            return null;

        var services = candidate.Services.Collection;
        var context = candidate.Context!;

        services.AddSingleton(context);
        services.AddSingleton<IPluginContext>(context);
        services.AddSingleton<IPluginConfig>(context.Config);
        services.AddSingleton<IPluginSecrets>(context.Secrets);
        services.AddSingleton<IPluginLog>(context.Log);
        services.AddSingleton<IPluginLifetime>(context.Lifetime);
        services.AddSingleton<IPluginServices>(context.Services);
        services.AddSingleton<IContributionRegistry>(context.Contributions);
        services.AddSingleton<IPluginDependencies>(context.Dependencies);

        services.AddSingleton(_hostProvider.GetRequiredService<IClock>());
        services.AddSingleton(_hostProvider.GetRequiredService<ChatCommandFactory>());
        services.AddSingleton(_hostProvider.GetRequiredService<IDomainEventStream>());
        services.AddSingleton(_hostProvider.GetRequiredService<Natter.Application.Chat.IContextPipeline>());
        services.AddSingleton(_hostProvider.GetRequiredService<ILoggerFactory>());

        if (candidate.Permissions?.Granted.Contains(PluginPermissions.ChatRead) == true)
            services.AddSingleton(_hostProvider.GetRequiredService<IChatEventSource>());

        if (candidate.Permissions?.Granted.Contains(PluginPermissions.Storage) == true)
        {
            services.AddSingleton(_hostProvider.GetRequiredService<Natter.Application.Chat.IConversationStore>());
            services.AddSingleton(_hostProvider.GetRequiredService<Natter.Application.Chat.IMemoryStore>());
            services.AddSingleton(_hostProvider.GetRequiredService<Natter.Application.Chat.ISummaryStore>());
        }

        services.AddSingleton<IChatCommandDispatcher>(provider => new NpsPermissionedChatCommandDispatcher(
            _hostProvider.GetRequiredService<IChatCommandDispatcher>(),
            candidate.Permissions?.Granted ?? []));

        BindAdminServices(candidate, services);

        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
    }

    /// <summary>
    /// Bridges the admin surface into the plugin provider. Only explicit, host-owned services are
    /// registered — the plugin never receives the root <see cref="IServiceProvider"/>. A plugin
    /// without the matching admin permission keeps its 501 fallback because the service is absent.
    /// </summary>
    private void BindAdminServices(NpsPluginCandidate candidate, IServiceCollection services)
    {
        if (_hostProvider is null)
            return;

        if (_hostProvider.GetService<AdminOptions>() is { EnableAdminSurface: false })
            return;

        var granted = candidate.Permissions?.Granted ?? [];

        // Least privilege: each admin contract has its own permission word.
        if (granted.Contains(PluginPermissions.AdminRead))
        {
            services.AddSingleton<IPluginRuntimeDiagnostics>(this);

            if (_hostProvider.GetService<IAdminLogReader>() is { } logReader)
                services.AddSingleton(logReader);
        }

        if (granted.Contains(PluginPermissions.AdminConfigWrite)
            && _hostProvider.GetService<IAdminConfigStore>() is { } configStore)
        {
            services.AddSingleton(configStore);
        }

        if (granted.Contains(PluginPermissions.AdminCommandExecute)
            && _hostProvider.GetService<IAdminCommandRunner>() is { } commandRunner)
        {
            services.AddSingleton(commandRunner);
        }
    }

    private void CommitChatContributions(NpsPluginCandidate candidate, IServiceProvider provider)
    {
        var active = GetOrCreateActiveContributions(candidate);

        foreach (var registration in candidate.Contributions!.Registrations)
        {
            switch (registration.Type)
            {
                case PluginContributionTypes.ChatUpstream:
                {
                    var upstream = (IChatUpstream)provider.GetRequiredService(registration.ImplementationType!);
                    _hostProvider!.GetRequiredService<IChatUpstreamRegistry>().Register(upstream);
                    active.Upstreams.Add(upstream);
                    break;
                }

                case PluginContributionTypes.ChatResponder:
                {
                    var factory = provider;
                    var implementationType = registration.ImplementationType!;
                    _hostProvider!.GetRequiredService<IChatResponderRegistry>().Register(
                        new ChatResponderRegistration(
                            registration.Id,
                            registration.Priority,
                            candidate.Id,
                            () => (IChatResponder)factory.GetRequiredService(implementationType)));
                    active.Responders.Add((candidate.Id, registration.Id));
                    break;
                }

                case PluginContributionTypes.PromptProvider:
                case PluginContributionTypes.ContextAssembler:
                case PluginContributionTypes.MemoryStore:
                {
                    var catalog = _hostProvider!.GetRequiredService<Natter.Application.Chat.IContextComponentCatalog>();
                    var reference = $"{candidate.Id}#{registration.Id}";
                    var implementation = provider.GetRequiredService(registration.ImplementationType!);
                    switch (registration.Type)
                    {
                        case PluginContributionTypes.PromptProvider:
                            catalog.RegisterPrompt(reference, (Natter.Application.Chat.IPromptProvider)implementation);
                            break;
                        case PluginContributionTypes.ContextAssembler:
                            catalog.RegisterAssembler(reference, (Natter.Application.Chat.IContextAssembler)implementation);
                            break;
                        case PluginContributionTypes.MemoryStore:
                            catalog.RegisterMemory(reference, (Natter.Application.Chat.IMemoryStore)implementation);
                            break;
                    }
                    active.ContextComponents.Add(reference);
                    break;
                }

                case PluginContributionTypes.ChatEventSubscriber:
                {
                    var subscriber = (IChatEventSubscriber)provider.GetRequiredService(registration.ImplementationType!);
                    var source = _hostProvider!.GetRequiredService<IChatEventSource>();
                    var subscription = source.Events.Subscribe(new ChatSubscriberObserver(subscriber, candidate.Log));
                    active.Subscriptions.Add(subscription);
                    break;
                }
            }
        }
    }

    private async ValueTask FailDuringActivationAsync(
        NpsPluginCandidate candidate,
        PluginErrorDescriptor error,
        CancellationToken cancellationToken)
    {
        candidate.SetError(error);
        _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
        await RollbackCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
        BlockDependents();
    }

    private async ValueTask RollbackCandidateAsync(NpsPluginCandidate candidate, CancellationToken cancellationToken)
    {
        var active = GetActiveContributions(candidate);
        if (active is not null)
        {
            foreach (var upstream in active.Upstreams)
            {
                try
                {
                    if (upstream.IsConnected)
                        await upstream.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }

                _hostProvider?.GetService<IChatUpstreamRegistry>()?.Unregister(upstream.Source);
            }

            foreach (var (owner, id) in active.Responders)
                _hostProvider?.GetService<IChatResponderRegistry>()?.Unregister(owner, id);

            foreach (var subscription in active.Subscriptions)
                subscription.Dispose();
            var catalog = _hostProvider?.GetService<Natter.Application.Chat.IContextComponentCatalog>();
            if (catalog is not null)
                foreach (var reference in active.ContextComponents)
                    catalog.Unregister(reference);

            active.Upstreams.Clear();
            active.Responders.Clear();
            active.Subscriptions.Clear();
            active.ContextComponents.Clear();
        }

        try
        {
            if (candidate.Plugin is not null && candidate.Context is not null)
                await candidate.Plugin.DeactivateAsync(candidate.Context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        await DisposePluginInstanceAsync(candidate).ConfigureAwait(false);
        await candidate.Lifetime.StopAsync().ConfigureAwait(false);

        if (candidate.Provider is { } provider)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            candidate.Provider = null;
            candidate.Services.Provider = null;
        }

        candidate.DisposeLoadContext();
        _startedOrder.Remove(candidate);
    }

    private async ValueTask StopCandidateAsync(NpsPluginCandidate candidate, CancellationToken cancellationToken)
    {
        var active = GetActiveContributions(candidate);

        if (active is not null)
        {
            foreach (var upstream in active.Upstreams)
            {
                try
                {
                    if (upstream.IsConnected)
                        await upstream.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }

                _hostProvider?.GetService<IChatUpstreamRegistry>()?.Unregister(upstream.Source);
            }

            foreach (var (owner, id) in active.Responders)
                _hostProvider?.GetService<IChatResponderRegistry>()?.Unregister(owner, id);

            foreach (var subscription in active.Subscriptions)
                subscription.Dispose();
            var catalog = _hostProvider?.GetService<Natter.Application.Chat.IContextComponentCatalog>();
            if (catalog is not null)
                foreach (var reference in active.ContextComponents)
                    catalog.Unregister(reference);

            active.Upstreams.Clear();
            active.Responders.Clear();
            active.Subscriptions.Clear();
            active.ContextComponents.Clear();
        }

        await candidate.Lifetime.StopAsync().ConfigureAwait(false);

        try
        {
            if (candidate.Plugin is not null && candidate.Context is not null)
                await candidate.Plugin.DeactivateAsync(candidate.Context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        await DisposePluginInstanceAsync(candidate).ConfigureAwait(false);

        if (candidate.Provider is { } provider)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            candidate.Provider = null;
            candidate.Services.Provider = null;
        }

        candidate.DisposeLoadContext();
        candidate.SetState(PluginState.Stopped);
        candidate.SetContributionStatus("inactive");
        _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
    }

    private void BlockDependents()
    {
        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var candidate in _candidates)
            {
                if (candidate.State is PluginState.Failed or PluginState.Blocked or PluginState.Disabled or PluginState.Skipped)
                    continue;

                var blockedBy = (candidate.Manifest.DependsOn ?? [])
                    .Select(dependency => dependency.Id)
                    .FirstOrDefault(dependencyId =>
                        GetPluginState(dependencyId) is PluginState.Failed or PluginState.Blocked or PluginState.Disabled or PluginState.Skipped);

                if (blockedBy is null)
                    continue;

                candidate.SetBlocked(new PluginErrorDescriptor(
                    "dependency",
                    "dependency-blocked",
                    $"Required plugin '{blockedBy}' is not available.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["dependency"] = blockedBy }));
                _blockedBy[candidate.Id] = [blockedBy];
                _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
                changed = true;
            }
        }
    }

    private void DetectDuplicateContributions()
    {
        var seen = new Dictionary<(string Type, string Id), string>();

        foreach (var candidate in _candidates.OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
        {
            if (candidate.State != PluginState.Configured || candidate.Contributions is null)
                continue;

            foreach (var registration in candidate.Contributions.Registrations)
            {
                var key = (registration.Type, registration.Id);
                if (seen.TryGetValue(key, out var owner))
                {
                    candidate.SetError(new PluginErrorDescriptor(
                        "configure",
                        "duplicate-contribution",
                        $"Contribution '{registration.Type}/{registration.Id}' is also declared by '{owner}'.",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["other"] = owner }));
                    _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
                    break;
                }

                seen[key] = candidate.Id;
            }
        }
    }

    private void RegisterHostBridges(IServiceCollection services)
    {
        var router = new NpsPortAdapterRouter(this);
        _portRouter = router;

        services.AddSingleton(this);
        services.AddSingleton<IPluginRuntimeDiagnostics>(this);
        services.AddSingleton<ICliCommandContributor, NpsCliCommandContributor>();
        services.AddSingleton(router);

        var seenServices = new HashSet<Type>();
        foreach (var candidate in _candidates)
        {
            if (candidate.Contributions is null)
                continue;

            foreach (var registration in candidate.Contributions.Registrations
                         .Where(item => item.Type == PluginContributionTypes.PortAdapter))
            {
                router.Register(
                    candidate.Id,
                    registration.ServiceType!,
                    registration.ImplementationType!,
                    registration.Lifetime);

                _adapterRegistry.Register(new PortAdapterDescriptor(
                    registration.ServiceType!.FullName ?? registration.ServiceType.Name,
                    registration.ImplementationType!.FullName ?? registration.ImplementationType.Name,
                    registration.Lifetime,
                    $"plugin:{candidate.Id}",
                    $"NPS port.adapter '{registration.Id}'."));

                if (!seenServices.Add(registration.ServiceType))
                    continue;

                var fallback = services.LastOrDefault(descriptor =>
                    descriptor.ServiceType == registration.ServiceType);
                router.RegisterFallback(
                    registration.ServiceType,
                    NpsPortAdapterRouter.CreateFallbackFactory(fallback));

                // The bridge must be transient: the provider would otherwise cache the
                // first plugin instance and keep using it after deactivation/rollback.
                services.Add(new ServiceDescriptor(
                    registration.ServiceType,
                    provider => router.Resolve(registration.ServiceType, provider)!,
                    ServiceLifetime.Transient));
            }
        }
    }

    private void SetFailure(
        NpsPluginCandidate candidate,
        string phase,
        string code,
        string message,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? detail = null)
    {
        var error = new PluginErrorDescriptor(phase, code, message, detail);
        candidate.SetError(error);
        _catalog.UpdateDescriptor(candidate.Id, _ => candidate.Descriptor);
        candidate.DisposeLoadContext();

        if (exception is not null)
            candidate.Log?.Error(exception, "Plugin {PluginId} failed: {ErrorMessage}", candidate.Id, message);
    }

    private sealed record ActiveContributions(
        List<IChatUpstream> Upstreams,
        List<(string Owner, string Id)> Responders,
        List<IDisposable> Subscriptions,
        List<string> ContextComponents);

    private readonly Dictionary<string, ActiveContributions> _activeContributions = new(StringComparer.Ordinal);

    private ActiveContributions GetOrCreateActiveContributions(NpsPluginCandidate candidate)
    {
        lock (_gate)
        {
            if (!_activeContributions.TryGetValue(candidate.Id, out var active))
            {
                active = new ActiveContributions([], [], [], []);
                _activeContributions[candidate.Id] = active;
            }

            return active;
        }
    }

    private ActiveContributions? GetActiveContributions(NpsPluginCandidate candidate)
    {
        lock (_gate)
            return _activeContributions.TryGetValue(candidate.Id, out var active) ? active : null;
    }

    private static async ValueTask DisposePluginInstanceAsync(NpsPluginCandidate candidate)
    {
        if (candidate.PluginDisposed || candidate.Plugin is null)
            return;

        candidate.PluginDisposed = true;

        try
        {
            if (candidate.Plugin is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (candidate.Plugin is IDisposable disposable)
                disposable.Dispose();
        }
        catch
        {
            // Disposal is best-effort.
        }
    }

    private static void RunSynchronously(ValueTask task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        task.AsTask().GetAwaiter().GetResult();
    }

    private string? GetPluginVersion(string pluginId)
    {
        var candidate = _candidates.FirstOrDefault(item => item.Id == pluginId);
        if (candidate is not null)
            return candidate.Version;

        return _legacyDescriptors.FirstOrDefault(descriptor =>
            string.Equals(descriptor.Id, pluginId, StringComparison.Ordinal))?.Version;
    }

    private sealed class ChatSubscriberObserver(IChatEventSubscriber subscriber, NpsPluginLog log) : IObserver<ChatEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
            => log.Error(error, "NLMP event stream failed for a plugin subscriber.");

        public void OnNext(ChatEvent value)
        {
            _ = HandleAsync(value);
        }

        private async Task HandleAsync(ChatEvent value)
        {
            try
            {
                await subscriber.HandleAsync(value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Plugin NLMP event subscriber failed for {EventId}.", value.Id);
            }
        }
    }
}
