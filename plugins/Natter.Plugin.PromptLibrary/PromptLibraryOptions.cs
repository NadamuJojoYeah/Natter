namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// Prompt Library plugin configuration (NPS config.json). The plugin only provides data
/// (fragments/presets); compilation, ordering and budgets stay
/// in the host composer. See docs/PROMPT_COMPILER.md §5/§6.
/// </summary>
public sealed class PromptLibraryOptions
{
    /// <summary>When false the provider degrades to the safe host fallback and logs one warning.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Preset used when the profile does not select one explicitly.</summary>
    public string? Preset { get; init; }

    /// <summary>Locale used when neither the request nor the profile supplies one.</summary>
    public string? Locale { get; init; }

    public int MaxCharacters { get; init; } = 12000;

    public int MaxFragmentCharacters { get; init; } = 8000;

    /// <summary><c>warn</c> or <c>error</c>: behavior for a whitelisted variable without a value.</summary>
    public string MissingVariables { get; init; } = "warn";

    /// <summary>Directory (relative to the plugin folder) that <see cref="PromptFragmentDefinition.File"/> resolves inside.</summary>
    public string PromptDirectory { get; init; } = "prompts";

    /// <summary>Optional JSON file with additional <c>fragments</c>/<c>presets</c> arrays.</summary>
    public string? PresetFile { get; init; }

    /// <summary>Fragments disabled from this installation. Sources stay on disk and can be re-enabled.</summary>
    public List<string> DisabledFragments { get; init; } = [];

    public List<PromptFragmentDefinition> Fragments { get; init; } = [];

    public List<PromptPresetDefinition> Presets { get; init; } = [];
}

/// <summary>Inline (<see cref="Text"/>) or file-backed (<see cref="File"/>) fragment definition.</summary>
public sealed class PromptFragmentDefinition
{
    public string Id { get; init; } = string.Empty;

    public string? Text { get; init; }

    /// <summary>Path relative to <see cref="PromptLibraryOptions.PromptDirectory"/>; read-only.</summary>
    public string? File { get; init; }

    public int Order { get; init; }

    public bool EnabledByDefault { get; init; } = true;

    public int MaxCharacters { get; init; } = 8000;

    public string? Version { get; init; }

    public List<string> Tags { get; init; } = [];
}

public sealed class PromptPresetDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Version { get; init; } = "1";

    public string? Extends { get; init; }

    public string? Locale { get; init; }

    public Dictionary<string, string> Variables { get; init; } = new(StringComparer.Ordinal);

    public List<PromptSectionDefinition> Sections { get; init; } = [];
}

public sealed class PromptSectionDefinition
{
    public string FragmentId { get; init; } = string.Empty;

    public int? Order { get; init; }

    public bool? Enabled { get; init; }

    public string? Source { get; init; }

    public string? OverrideText { get; init; }

    public string? Reason { get; init; }
}

/// <summary>Shape of the optional <see cref="PromptLibraryOptions.PresetFile"/> document.</summary>
public sealed class PromptLibraryFile
{
    public List<PromptFragmentDefinition> Fragments { get; init; } = [];

    public List<PromptPresetDefinition> Presets { get; init; } = [];
}

/// <summary>Startup failure surfaced as an NPS configure error; messages never include prompt text.</summary>
public sealed class PromptLibraryStartupException(string code, string message)
    : InvalidOperationException($"{code}: {message}")
{
    public string Code { get; } = code;
}
