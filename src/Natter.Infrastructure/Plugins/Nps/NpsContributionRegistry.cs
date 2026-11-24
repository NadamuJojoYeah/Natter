using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>One staged contribution. Nothing reaches the host registries before commit.</summary>
internal sealed record NpsContributionRegistration(
    string Type,
    string Id,
    int Priority,
    Type? ServiceType,
    Type? ImplementationType,
    ServiceLifetime Lifetime,
    CliCommandDescriptor? Command)
{
    public string Status { get; set; } = "staged";
}

/// <summary>
/// Staging registry handed to a plugin. It enforces manifest declarations and permissions,
/// and registers services only in the plugin-local collection.
/// </summary>
internal sealed class NpsContributionRegistry : IContributionRegistry
{
    private readonly string _pluginId;
    private readonly PluginManifest _manifest;
    private readonly IReadOnlySet<string> _granted;
    private readonly NpsPluginServices _services;
    private readonly List<string> _warnings = [];
    private readonly List<NpsContributionRegistration> _registrations = [];
    private readonly Lock _gate = new();

    public NpsContributionRegistry(
        string pluginId,
        PluginManifest manifest,
        IReadOnlyList<string> granted,
        NpsPluginServices services)
    {
        _pluginId = pluginId;
        _manifest = manifest;
        _granted = new HashSet<string>(granted, StringComparer.Ordinal);
        _services = services;
    }

    public IReadOnlyList<NpsContributionRegistration> Registrations
    {
        get
        {
            lock (_gate)
                return _registrations.ToArray();
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_gate)
                return _warnings.ToArray();
        }
    }

    public void AddChatUpstream<TUpstream>(string id)
        where TUpstream : class, IChatUpstream
    {
        Require(PluginContributionTypes.ChatUpstream, id);
        RequirePermissions(PluginContributionTypes.ChatUpstream, id);
        _services.Collection.TryAddSingleton<TUpstream>();
        Stage(PluginContributionTypes.ChatUpstream, id, 0, typeof(IChatUpstream), typeof(TUpstream), ServiceLifetime.Singleton, null);
    }

    public void AddChatResponder<TResponder>(string id, int priority = 0)
        where TResponder : class, IChatResponder
    {
        var declaration = Require(PluginContributionTypes.ChatResponder, id);
        RequirePermissions(PluginContributionTypes.ChatResponder, id);

        if (declaration.Priority != priority)
        {
            lock (_gate)
            {
                _warnings.Add(
                    $"Manifest priority {declaration.Priority} for chat.responder '{id}' differs from registration priority {priority}.");
            }
        }

        _services.Collection.TryAddSingleton<TResponder>();
        Stage(PluginContributionTypes.ChatResponder, id, priority, typeof(IChatResponder), typeof(TResponder), ServiceLifetime.Singleton, null);
    }

    public void AddChatEventSubscriber<TSubscriber>(string id)
        where TSubscriber : class, IChatEventSubscriber
    {
        Require(PluginContributionTypes.ChatEventSubscriber, id);
        RequirePermissions(PluginContributionTypes.ChatEventSubscriber, id);
        _services.Collection.TryAddSingleton<TSubscriber>();
        Stage(PluginContributionTypes.ChatEventSubscriber, id, 0, typeof(IChatEventSubscriber), typeof(TSubscriber), ServiceLifetime.Singleton, null);
    }

    public void AddCliCommand(CliCommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var id = NormalizeCliCommandId(descriptor.Name);
        Require(PluginContributionTypes.CliCommand, id);
        RequirePermissions(PluginContributionTypes.CliCommand, id);

        var staged = new CliCommandDescriptor
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Usage = descriptor.Usage,
            Aliases = descriptor.Aliases,
            Options = descriptor.Options,
            Arguments = descriptor.Arguments,
            Origin = $"plugin:{_pluginId}",
            HandlerFactory = descriptor.HandlerFactory,
        };

        Stage(PluginContributionTypes.CliCommand, id, 0, typeof(ICliCommandHandler), null, ServiceLifetime.Singleton, staged);
    }

    public void AddPromptProvider<TProvider>(string id)
        where TProvider : class, IPromptProvider
    {
        Require(PluginContributionTypes.PromptProvider, id);
        RequirePermissions(PluginContributionTypes.PromptProvider, id);
        _services.Collection.TryAddSingleton<TProvider>();
        Stage(PluginContributionTypes.PromptProvider, id, 0, typeof(IPromptProvider), typeof(TProvider), ServiceLifetime.Singleton, null);
    }

    public void AddPromptFragmentProvider<TProvider>(string id)
        where TProvider : class, IPromptFragmentProvider
    {
        Require(PluginContributionTypes.PromptFragment, id);
        RequirePermissions(PluginContributionTypes.PromptFragment, id);
        _services.Collection.TryAddSingleton<TProvider>();
        Stage(PluginContributionTypes.PromptFragment, id, 0, typeof(IPromptFragmentProvider), typeof(TProvider), ServiceLifetime.Singleton, null);
    }

    public void AddContextAssembler<TAssembler>(string id)
        where TAssembler : class, IContextAssembler
    {
        Require(PluginContributionTypes.ContextAssembler, id);
        RequirePermissions(PluginContributionTypes.ContextAssembler, id);
        _services.Collection.TryAddSingleton<TAssembler>();
        Stage(PluginContributionTypes.ContextAssembler, id, 0, typeof(IContextAssembler), typeof(TAssembler), ServiceLifetime.Singleton, null);
    }

    public void AddMemoryStore<TStore>(string id)
        where TStore : class, IMemoryEntryStore
    {
        Require(PluginContributionTypes.MemoryStore, id);
        RequirePermissions(PluginContributionTypes.MemoryStore, id);
        _services.Collection.TryAddSingleton<TStore>();
        Stage(PluginContributionTypes.MemoryStore, id, 0, typeof(IMemoryEntryStore), typeof(TStore), ServiceLifetime.Singleton, null);
    }

    public void AddEmbeddingProvider<TProvider>(string id)
        where TProvider : class, IEmbeddingProvider
    {
        Require(PluginContributionTypes.EmbeddingProvider, id);
        RequirePermissions(PluginContributionTypes.EmbeddingProvider, id);
        RequireSecretPermission(PluginContributionTypes.EmbeddingProvider, id);
        _services.Collection.TryAddSingleton<TProvider>();
        Stage(PluginContributionTypes.EmbeddingProvider, id, 0, typeof(IEmbeddingProvider), typeof(TProvider), ServiceLifetime.Singleton, null);
    }

    public void AddRerankProvider<TProvider>(string id)
        where TProvider : class, IRerankProvider
    {
        Require(PluginContributionTypes.RerankProvider, id);
        RequirePermissions(PluginContributionTypes.RerankProvider, id);
        RequireSecretPermission(PluginContributionTypes.RerankProvider, id);
        _services.Collection.TryAddSingleton<TProvider>();
        Stage(PluginContributionTypes.RerankProvider, id, 0, typeof(IRerankProvider), typeof(TProvider), ServiceLifetime.Singleton, null);
    }

    public void AddPortAdapter<TService, TImplementation>(
        string id,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class
        where TImplementation : class, TService
    {
        Require(PluginContributionTypes.PortAdapter, id);
        _services.Collection.TryAdd(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));
        Stage(PluginContributionTypes.PortAdapter, id, 0, typeof(TService), typeof(TImplementation), lifetime, null);
    }

    /// <summary>Declared contributions that were not registered during ConfigureAsync.</summary>
    public IReadOnlyList<string> FindMissingContributions()
    {
        var missing = new List<string>();
        var registrations = Registrations;

        foreach (var declaration in _manifest.Contributions ?? [])
        {
            if (!registrations.Any(registration =>
                    string.Equals(registration.Type, declaration.Type, StringComparison.Ordinal)
                    && string.Equals(registration.Id, declaration.Id, StringComparison.Ordinal)))
            {
                missing.Add($"{declaration.Type}/{declaration.Id}");
            }
        }

        return missing;
    }

    private PluginContribution Require(string type, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new PluginContributionException($"Contribution '{type}' needs a non-empty id.");

        var declaration = _manifest.FindContribution(type, id);
        if (declaration is null)
        {
            throw new PluginContributionException(
                $"Plugin '{_pluginId}' registered undeclared contribution '{type}/{id}'.");
        }

        return declaration;
    }

    private void RequirePermissions(string type, string id)
    {
        foreach (var permission in NpsPermissionPolicy.RequiredPermissions(type))
        {
            if (!_granted.Contains(permission))
            {
                throw new PluginPermissionException(
                    $"Plugin '{_pluginId}' lacks permission '{permission}' required by contribution '{type}/{id}'.");
            }
        }
    }

    private void RequireSecretPermission(string type, string id)
    {
        if (_granted.Any(permission => permission.StartsWith(PluginPermissions.SecretsPrefix, StringComparison.Ordinal)))
            return;

        throw new PluginPermissionException(
            $"Plugin '{_pluginId}' lacks a '{PluginPermissions.SecretsPrefix}' permission required by contribution '{type}/{id}'.");
    }

    private void Stage(
        string type,
        string id,
        int priority,
        Type? serviceType,
        Type? implementationType,
        ServiceLifetime lifetime,
        CliCommandDescriptor? command)
    {
        lock (_gate)
        {
            if (_registrations.Any(registration =>
                    string.Equals(registration.Type, type, StringComparison.Ordinal)
                    && string.Equals(registration.Id, id, StringComparison.Ordinal)))
            {
                throw new PluginContributionException(
                    $"Contribution '{type}/{id}' is registered more than once by plugin '{_pluginId}'.");
            }

            _registrations.Add(new NpsContributionRegistration(
                type, id, priority, serviceType, implementationType, lifetime, command));
        }
    }

    internal static string NormalizeCliCommandId(string commandName)
        => commandName.Trim().Replace(' ', '.');
}
