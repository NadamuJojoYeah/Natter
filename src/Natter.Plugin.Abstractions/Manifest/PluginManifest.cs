using System.Text.Json;
using System.Text.Json.Serialization;

namespace Natter.Plugin.Abstractions;

/// <summary>NPS v1 plugin kind.</summary>
public enum PluginKind
{
    Transport,
    Feature,
    Tool,
    Memory,
    Sample,
}

/// <summary>NPS v1 manifest. The manifest is the single source of plugin identity and metadata.</summary>
public sealed class PluginManifest
{
    public int SchemaVersion { get; init; } = 1;

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public PluginKind Kind { get; init; }

    /// <summary>Host API range, for example <c>&gt;=0.1.0 &lt;1.0.0</c>.</summary>
    public string HostApi { get; init; } = string.Empty;

    public PluginEntry Entry { get; init; } = new();

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? License { get; init; }

    public string? Homepage { get; init; }

    public List<PluginDependency>? DependsOn { get; init; }

    public List<PluginDependency>? OptionalDependsOn { get; init; }

    public List<string>? LoadAfter { get; init; }

    public List<string>? LoadBefore { get; init; }

    public List<PluginConflict>? Conflicts { get; init; }

    public List<string>? Permissions { get; init; }

    public List<PluginSecret>? Secrets { get; init; }

    public string? ConfigSchema { get; init; }

    public List<PluginContribution>? Contributions { get; init; }

    public List<string>? Platforms { get; init; }

    /// <summary>Legacy display labels; not permissions.</summary>
    public List<string>? Capabilities { get; init; }

    /// <summary>
    /// Subdirectories of the plugin package that the plugin writes at runtime (audit logs, caches,
    /// plugin-local databases). Declared here because the deployment must hand exactly these
    /// directories to the service user: code and <c>lib/</c> stay root-owned and read-only, while a
    /// plugin that cannot write its own data degrades to Failed. Keeping the declaration next to the
    /// plugin means adding a writing plugin no longer requires editing the deploy script.
    /// </summary>
    public List<PluginDataDirectory>? DataDirs { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public PluginContribution? FindContribution(string type, string id)
        => Contributions?.FirstOrDefault(contribution =>
            string.Equals(contribution.Type, type, StringComparison.Ordinal)
            && string.Equals(contribution.Id, id, StringComparison.Ordinal));

    public PluginSecret? FindSecret(string name)
        => Secrets?.FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.Ordinal));
}

/// <summary>Plugin entry assembly and type.</summary>
public sealed class PluginEntry
{
    public string Assembly { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Required or optional plugin dependency.</summary>
public sealed class PluginDependency
{
    public string Id { get; init; } = string.Empty;

    public string? Version { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Conflict declaration: target plugin id and optional SemVer range.</summary>
public sealed class PluginConflict
{
    public string Id { get; init; } = string.Empty;

    public string? Version { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Secret requirement. The manifest never contains the secret value.</summary>
public sealed class PluginSecret
{
    public string Name { get; init; } = string.Empty;

    public bool Required { get; init; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Manifest contribution declaration; runtime registration must match it.</summary>
public sealed class PluginContribution
{
    public string Type { get; init; } = string.Empty;

    public string Id { get; init; } = string.Empty;

    public int Priority { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>A plugin-owned writable directory declared in the manifest.</summary>
public sealed class PluginDataDirectory
{
    /// <summary>Path relative to the plugin directory. Absolute paths and <c>..</c> are rejected.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Why the plugin writes here; surfaced in deployment output for review.</summary>
    public string? Purpose { get; init; }
}
