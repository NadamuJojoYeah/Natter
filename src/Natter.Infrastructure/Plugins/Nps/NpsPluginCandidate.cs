using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Mutable host-side state for one NPS plugin through discovery → activation → unload.</summary>
internal sealed class NpsPluginCandidate
{
    public required PluginManifest Manifest { get; init; }

    public required string Directory { get; init; }

    public required string AssemblyPath { get; init; }

    public bool IsEmbedded { get; init; }

    public PluginTrustLevel Trust { get; init; }

    public PluginManifestReadResult? ReadResult { get; init; }

    public PluginLoadContext? LoadContext { get; set; }

    public Type? EntryType { get; set; }

    public INatterPlugin? Plugin { get; set; }

    public NpsPluginServices Services { get; } = new();

    public NpsPluginLog Log { get; set; } = null!;

    public NpsPluginLifetime Lifetime { get; } = new();

    public NpsContributionRegistry? Contributions { get; set; }

    public NpsPluginContext? Context { get; set; }

    public NpsPluginConfig? Config { get; set; }

    public NpsPluginSecrets? Secrets { get; set; }

    public NpsPluginDependencies? Dependencies { get; set; }

    public NpsConfigLoadResult? ConfigLoad { get; set; }

    public NpsPermissionDecision? Permissions { get; set; }

    public ServiceProvider? Provider { get; set; }

    public PluginState State { get; set; } = PluginState.Discovered;

    public PluginErrorDescriptor? Error { get; set; }

    public List<string> Warnings { get; } = [];

    public bool PluginDisposed { get; set; }

    public bool IsLegacyEntry { get; set; }

    public PluginDescriptor Descriptor { get; set; } = null!;

    public string Id => Manifest.Id;

    public string Version => Manifest.Version;

    public static NpsPluginCandidate FromManifest(
        PluginManifest manifest,
        string directory,
        PluginManifestReadResult readResult,
        PluginTrustLevel trust)
    {
        var assembly = manifest.Entry?.Assembly ?? string.Empty;
        var assemblyPath = string.IsNullOrWhiteSpace(directory)
            ? assembly
            : Path.GetFullPath(Path.Combine(directory, assembly));

        var candidate = new NpsPluginCandidate
        {
            Manifest = manifest,
            Directory = directory,
            AssemblyPath = assemblyPath,
            IsEmbedded = false,
            Trust = trust,
            ReadResult = readResult,
        };

        candidate.Warnings.AddRange(readResult.Warnings.Select(warning => $"{warning.Path}: {warning.Message}"));
        return candidate;
    }

    public static NpsPluginCandidate FromEmbedded(
        PluginManifest manifest,
        INatterPlugin plugin,
        string directory)
    {
        return new NpsPluginCandidate
        {
            Manifest = manifest,
            Directory = directory,
            AssemblyPath = "<embedded>",
            IsEmbedded = true,
            Trust = PluginTrustLevel.Bundled,
            Plugin = plugin,
            EntryType = plugin.GetType(),
            ReadResult = new PluginManifestReadResult(
                Path.Combine(directory, "plugin.json"),
                IsNps: true,
                manifest,
                Errors: [],
                Warnings: []),
        };
    }

    public void InitializeDescriptor(string? error = null, PluginErrorDescriptor? errorDetail = null)
    {
        var contributions = (Manifest.Contributions ?? [])
            .Select(contribution => new PluginContributionDescriptor(
                contribution.Type,
                contribution.Id,
                contribution.Priority))
            .ToArray();

        Descriptor = new PluginDescriptor(
            Id: Manifest.Id,
            Name: Manifest.Name,
            Version: Manifest.Version,
            AssemblyPath: AssemblyPath,
            Directory: Directory,
            State: State,
            Error: error,
            Capabilities: Manifest.Capabilities,
            Description: Manifest.Description,
            IsDynamic: IsEmbedded,
            Kind: Manifest.Kind.ToString().ToLowerInvariant(),
            HostApi: Manifest.HostApi,
            Trust: Trust.ToString().ToLowerInvariant(),
            Dependencies: (Manifest.DependsOn ?? [])
                .Select(dependency => dependency.Id)
                .Concat((Manifest.OptionalDependsOn ?? []).Select(dependency => dependency.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Permissions: Permissions?.Requested,
            GrantedPermissions: Permissions?.Granted,
            Contributions: contributions,
            ErrorDetail: errorDetail);
    }

    public void SetState(PluginState state)
    {
        State = state;
        Descriptor = Descriptor with { State = state };
    }

    public void SetError(PluginErrorDescriptor error)
    {
        Error = error;
        State = PluginState.Failed;
        Descriptor = Descriptor with
        {
            State = PluginState.Failed,
            Error = $"{error.Code}: {error.Message}",
            ErrorDetail = error,
        };
    }

    public void SetBlocked(PluginErrorDescriptor error)
    {
        Error = error;
        State = PluginState.Blocked;
        Descriptor = Descriptor with
        {
            State = PluginState.Blocked,
            Error = $"{error.Code}: {error.Message}",
            ErrorDetail = error,
        };
    }

    public void SetContributionStatus(string status)
    {
        if (Descriptor.Contributions is not { Count: > 0 } contributions)
            return;

        Descriptor = Descriptor with
        {
            Contributions = contributions
                .Select(contribution => contribution with { Status = status })
                .ToArray(),
        };
    }

    public void UpdateContributions(IReadOnlyList<NpsContributionRegistration> registrations)
    {
        var existing = Descriptor.Contributions ?? [];
        var updated = existing
            .Select(contribution =>
            {
                var registration = registrations.FirstOrDefault(item =>
                    string.Equals(item.Type, contribution.Type, StringComparison.Ordinal)
                    && string.Equals(item.Id, contribution.Id, StringComparison.Ordinal));

                return registration is null
                    ? contribution with { Status = "missing" }
                    : contribution with { Priority = registration.Priority, Status = registration.Status };
            })
            .ToArray();

        Descriptor = Descriptor with { Contributions = updated };
    }

    public void DisposeLoadContext()
    {
        try
        {
            LoadContext?.Unload();
        }
        catch
        {
            // Best-effort unload.
        }
        finally
        {
            LoadContext = null;
        }
    }
}
