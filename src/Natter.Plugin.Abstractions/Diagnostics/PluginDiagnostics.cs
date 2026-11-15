using Natter.Abstractions.Plugins;

namespace Natter.Plugin.Abstractions;

/// <summary>One runtime or manifest contribution.</summary>
public sealed record PluginContributionInfo(
    string Type,
    string Id,
    string PluginId,
    int Priority,
    string Status);

/// <summary>Graph node shown by <c>plugins graph</c>.</summary>
public sealed record PluginGraphNode(
    string Id,
    string Version,
    string Kind,
    PluginState State,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> BlockedBy);

/// <summary>Graph edge: dependency or ordering constraint.</summary>
public sealed record PluginGraphEdge(string From, string To, string Kind);

/// <summary>Permission decision for one plugin.</summary>
public sealed record PluginPermissionInfo(
    string PluginId,
    IReadOnlyList<string> Requested,
    IReadOnlyList<string> Granted,
    IReadOnlyList<string> Denied);

/// <summary>One config/schema validation issue with a JSON Pointer.</summary>
public sealed record PluginConfigIssue(
    string Pointer,
    string Message,
    string? Expected = null,
    string? Actual = null);

/// <summary>Result of validating one plugin's config.json.</summary>
public sealed record PluginConfigValidationResult(
    string PluginId,
    bool Valid,
    IReadOnlyList<PluginConfigIssue> Issues);

/// <summary>Detailed diagnosis of one plugin.</summary>
public sealed record PluginDoctorReport(
    string PluginId,
    PluginManifest? Manifest,
    PluginState State,
    string? HostApi,
    bool HostApiCompatible,
    string? ConfigPath,
    bool ConfigValid,
    IReadOnlyList<PluginConfigIssue> ConfigIssues,
    IReadOnlyList<string> MissingSecrets,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Warnings,
    PluginErrorDescriptor? Error);

/// <summary>Read-only NPS runtime view consumed by CLI diagnostics.</summary>
public interface IPluginRuntimeDiagnostics
{
    IReadOnlyList<PluginGraphNode> Graph { get; }

    IReadOnlyList<PluginGraphEdge> GraphEdges { get; }

    IReadOnlyList<PluginContributionInfo> Contributions { get; }

    IReadOnlyList<PluginPermissionInfo> Permissions { get; }

    PluginDoctorReport GetDoctor(string pluginId);

    PluginConfigValidationResult ValidateConfig(string pluginId);
}
