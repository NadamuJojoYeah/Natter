using Natter.Abstractions.Plugins;

namespace Natter.Plugin.Store;

/// <summary>Persistent installed plugin record. <c>installed.json</c> is the source of truth.</summary>
public sealed class InstalledPluginRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ActiveVersion { get; set; } = string.Empty;

    public string? PreviousVersion { get; set; }

    public bool Enabled { get; set; } = true;

    public string Trust { get; set; } = "unsigned";

    public string? SignerKeyId { get; set; }

    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset InstalledAt { get; set; }

    public DateTimeOffset? LastActivatedAt { get; set; }

    public string? FailureReason { get; set; }

    public PluginState LastKnownState { get; set; } = PluginState.Discovered;
}

/// <summary>installed.json document.</summary>
public sealed class PluginStoreState
{
    public int SchemaVersion { get; set; } = 1;

    public List<InstalledPluginRecord> Plugins { get; set; } = [];
}

/// <summary>One pending activation candidate; written before restart.</summary>
public sealed class PendingActivationRecord
{
    public string PluginId { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? PreviousVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>pending.json document.</summary>
public sealed class PendingActivationState
{
    public int SchemaVersion { get; set; } = 1;

    public List<PendingActivationRecord> Plugins { get; set; } = [];
}

/// <summary>Result of installing/updating one package.</summary>
public sealed record PluginStoreInstallResult(
    InstalledPluginRecord Record,
    PluginPackageInfo Package,
    bool IsUpdate);

/// <summary>Result of rolling back one plugin.</summary>
public sealed record PluginStoreRollbackResult(
    string PluginId,
    bool RolledBack,
    string? ActiveVersion,
    string? Reason);

/// <summary>Result of reconciling pending activations against the live plugin graph.</summary>
public sealed record PluginStoreReconcileResult(
    IReadOnlyList<string> Confirmed,
    IReadOnlyList<string> RolledBack,
    IReadOnlyList<string> Pending);
