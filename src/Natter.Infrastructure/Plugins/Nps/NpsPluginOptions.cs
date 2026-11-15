using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Host policy for NPS v1 plugin discovery, trust, permissions and secrets.</summary>
public sealed class NpsPluginOptions
{
    /// <summary>Trust level assigned to file-based plugins.</summary>
    public PluginTrustLevel FilePluginTrustLevel { get; set; } = PluginTrustLevel.Local;

    public bool AllowLocalPlugins { get; set; } = true;

    public bool AutoGrantLocalPermissions { get; set; } = true;

    public bool AutoGrantBundledPermissions { get; set; } = true;

    public bool LoadUntrustedPlugins { get; set; }

    public ISecretProvider SecretProvider { get; set; } = new EnvironmentSecretProvider();

    /// <summary>Explicit contract/private assembly policy used by NPS file loading.</summary>
    public PluginLoadContextOptions LoadContextOptions { get; set; } = PluginLoadContextOptions.Default;

    /// <summary>Optional app-private config directory; see CompositionOptions.PluginConfigDirectory.</summary>
    public string? ConfigDirectory { get; set; }

    public HashSet<string> DisabledPlugins { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ForceGrantedPermissions { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ForceDeniedPermissions { get; } = new(StringComparer.Ordinal);

    public NpsPluginOptions Clone()
    {
        var clone = new NpsPluginOptions
        {
            FilePluginTrustLevel = FilePluginTrustLevel,
            AllowLocalPlugins = AllowLocalPlugins,
            AutoGrantLocalPermissions = AutoGrantLocalPermissions,
            AutoGrantBundledPermissions = AutoGrantBundledPermissions,
            LoadUntrustedPlugins = LoadUntrustedPlugins,
            SecretProvider = SecretProvider,
            LoadContextOptions = LoadContextOptions,
        };

        foreach (var plugin in DisabledPlugins)
            clone.DisabledPlugins.Add(plugin);
        foreach (var permission in ForceGrantedPermissions)
            clone.ForceGrantedPermissions.Add(permission);
        foreach (var permission in ForceDeniedPermissions)
            clone.ForceDeniedPermissions.Add(permission);

        return clone;
    }
}

/// <summary>Permission decision for one plugin manifest.</summary>
public sealed record NpsPermissionDecision(
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Granted,
    IReadOnlyList<string> Denied);

/// <summary>Applies the v1 trust policy. Permissions are governance, not a sandbox.</summary>
public static class NpsPermissionPolicy
{
    public static NpsPermissionDecision Decide(
        PluginManifest manifest,
        PluginTrustLevel trust,
        NpsPluginOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var requested = (manifest.Permissions ?? [])
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();

        var granted = new List<string>();
        var denied = new List<string>();

        foreach (var permission in requested)
        {
            if (options.ForceDeniedPermissions.Contains(permission))
            {
                denied.Add(permission);
                continue;
            }

            if (options.ForceGrantedPermissions.Contains(permission))
            {
                granted.Add(permission);
                continue;
            }

            var allow = trust switch
            {
                PluginTrustLevel.Bundled => options.AutoGrantBundledPermissions,
                PluginTrustLevel.Local or PluginTrustLevel.Signed => options.AllowLocalPlugins && options.AutoGrantLocalPermissions,
                PluginTrustLevel.Untrusted => false,
                _ => false,
            };

            if (allow)
                granted.Add(permission);
            else
                denied.Add(permission);
        }

        return new NpsPermissionDecision(requested, granted, denied);
    }

    public static IReadOnlyList<string> RequiredPermissions(string contributionType)
        => contributionType switch
        {
            PluginContributionTypes.ChatUpstream =>
                [PluginPermissions.ChatRead, PluginPermissions.ChatSend],
            PluginContributionTypes.ChatResponder =>
                [PluginPermissions.ChatRead, PluginPermissions.ChatSend],
            PluginContributionTypes.ChatEventSubscriber =>
                [PluginPermissions.ChatRead],
            PluginContributionTypes.CliCommand =>
                [PluginPermissions.CliCommands],
            PluginContributionTypes.MemoryStore =>
                [PluginPermissions.Storage],
            _ => [],
        };
}
