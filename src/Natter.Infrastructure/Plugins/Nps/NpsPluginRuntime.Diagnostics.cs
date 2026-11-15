using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

internal sealed partial class NpsPluginRuntime
{
    public IReadOnlyList<PluginGraphNode> Graph
    {
        get
        {
            var nodes = _graph?.Nodes ?? [];
            var result = new List<PluginGraphNode>(nodes.Count);

            foreach (var node in nodes)
            {
                var state = GetPluginState(node.Id);
                var blockedBy = _blockedBy.TryGetValue(node.Id, out var localBlocked)
                    ? localBlocked
                    : _graph?.BlockedBy.TryGetValue(node.Id, out var graphBlocked) == true
                        ? graphBlocked
                        : [];

                result.Add(new PluginGraphNode(
                    node.Id,
                    node.Version,
                    node.Kind,
                    state,
                    (node.Dependencies ?? []).Select(dependency => dependency.Id).ToArray(),
                    node.LoadAfter ?? [],
                    blockedBy));
            }

            return result;
        }
    }

    public IReadOnlyList<PluginGraphEdge> GraphEdges => _graph?.Edges ?? [];

    public IReadOnlyList<PluginContributionInfo> Contributions
    {
        get
        {
            var items = new List<PluginContributionInfo>();

            foreach (var candidate in _candidates)
            {
                if (candidate.Contributions is null)
                    continue;

                foreach (var registration in candidate.Contributions.Registrations)
                {
                    var status = candidate.State switch
                    {
                        PluginState.Activated => "active",
                        PluginState.Configured => "staged",
                        PluginState.Failed or PluginState.Blocked or PluginState.Disabled or PluginState.Skipped => "unavailable",
                        _ => "staged",
                    };

                    items.Add(new PluginContributionInfo(
                        registration.Type,
                        registration.Id,
                        candidate.Id,
                        registration.Priority,
                        status));
                }
            }

            return items;
        }
    }

    public IReadOnlyList<PluginPermissionInfo> Permissions
        => _candidates
            .Where(candidate => candidate.Permissions is not null)
            .Select(candidate => new PluginPermissionInfo(
                candidate.Id,
                candidate.Permissions!.Requested,
                candidate.Permissions.Granted,
                candidate.Permissions.Denied))
            .ToArray();

    public PluginDoctorReport GetDoctor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var candidate = _candidates.FirstOrDefault(item =>
            string.Equals(item.Id, pluginId, StringComparison.Ordinal));

        if (candidate is not null)
        {
            var hostApiCompatible = candidate.Manifest.HostApi is not null
                                    && VersionRange.TryParse(candidate.Manifest.HostApi, out var range)
                                    && range!.IsSatisfiedBy(SemanticVersion.Parse(NatterPluginApi.Version));

            var missingSecrets = GetMissingSecrets(candidate);
            var warnings = candidate.Warnings
                .Concat(candidate.ConfigLoad?.Issues.Select(issue => $"{issue.Pointer}: {issue.Message}") ?? [])
                .ToArray();

            return new PluginDoctorReport(
                candidate.Id,
                candidate.Manifest,
                candidate.State,
                candidate.Manifest.HostApi,
                hostApiCompatible,
                candidate.ConfigLoad?.ConfigPath,
                candidate.ConfigLoad?.Valid ?? candidate.Manifest.ConfigSchema is null,
                candidate.ConfigLoad?.Issues ?? [],
                missingSecrets,
                (candidate.Manifest.DependsOn ?? []).Select(dependency => dependency.Id).ToArray(),
                warnings,
                candidate.Error);
        }

        var descriptor = _catalog.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

        if (descriptor is null)
        {
            throw new KeyNotFoundException($"Plugin '{pluginId}' was not found.");
        }

        var manifestRead = PluginManifestReader.Read(descriptor.Directory);
        var issues = manifestRead.Errors
            .Select(issue => new PluginConfigIssue(issue.Path, issue.Message))
            .ToArray();

        return new PluginDoctorReport(
            descriptor.Id,
            manifestRead.Manifest,
            descriptor.State,
            descriptor.HostApi,
            HostApiCompatible(descriptor.HostApi),
            null,
            manifestRead.Errors.Count == 0,
            issues,
            [],
            descriptor.Dependencies ?? [],
            manifestRead.Warnings.Select(warning => $"{warning.Path}: {warning.Message}").ToArray(),
            descriptor.ErrorDetail);
    }

    public PluginConfigValidationResult ValidateConfig(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var candidate = _candidates.FirstOrDefault(item =>
            string.Equals(item.Id, pluginId, StringComparison.Ordinal));

        if (candidate is not null)
        {
            var load = NpsPluginConfigLoader.Load(candidate.Manifest, candidate.Directory);
            return new PluginConfigValidationResult(pluginId, load.Valid, load.Issues);
        }

        var descriptor = _catalog.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

        if (descriptor is null)
        {
            return new PluginConfigValidationResult(
                pluginId,
                false,
                [new PluginConfigIssue("/", "Plugin was not found.", "existing plugin", pluginId)]);
        }

        var read = PluginManifestReader.Read(descriptor.Directory);
        if (!read.IsValid || read.Manifest is null)
        {
            return new PluginConfigValidationResult(
                pluginId,
                false,
                read.Errors.Select(issue => new PluginConfigIssue(issue.Path, issue.Message)).ToArray());
        }

        var loaded = NpsPluginConfigLoader.Load(read.Manifest, descriptor.Directory);
        return new PluginConfigValidationResult(pluginId, loaded.Valid, loaded.Issues);
    }

    private IReadOnlyList<string> GetMissingSecrets(NpsPluginCandidate candidate)
    {
        var missing = new List<string>();

        foreach (var secret in candidate.Manifest.Secrets ?? [])
        {
            if (!secret.Required)
                continue;

            var permitted = candidate.Permissions?.Granted.Contains(PluginPermissions.Secret(secret.Name)) == true;
            var available = _options.SecretProvider.TryGet(secret.Name, out var value) && !string.IsNullOrEmpty(value);

            if (!permitted || !available)
                missing.Add(secret.Name);
        }

        return missing;
    }

    private static bool HostApiCompatible(string? hostApi)
        => hostApi is not null
           && VersionRange.TryParse(hostApi, out var range)
           && range!.IsSatisfiedBy(SemanticVersion.Parse(NatterPluginApi.Version));
}
