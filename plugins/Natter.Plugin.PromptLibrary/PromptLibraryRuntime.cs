using System.Text.Json;
using Natter.Application.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.PromptLibrary;

/// <summary>
/// Immutable runtime state of the Prompt Library plugin: loaded library data, identity and the
/// plugin-scoped logger. Diagnostics go through <see cref="Describe"/> and never expose fragment
/// text or variable values.
/// </summary>
public sealed class PromptLibraryRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PromptLibraryRuntime(string pluginId, string pluginVersion, PromptLibrary library, IPluginLog log)
    {
        PluginId = string.IsNullOrWhiteSpace(pluginId) ? "natter.feature.prompt.library" : pluginId;
        PluginVersion = string.IsNullOrWhiteSpace(pluginVersion) ? "0.0.0" : pluginVersion;
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string PluginId { get; }

    public string PluginVersion { get; }

    public PromptLibrary Library { get; }

    public IPluginLog Log { get; }

    /// <summary>Builds the explain request with a synthetic profile; no user data is involved.</summary>
    public PromptComposeRequest BuildExplainRequest()
    {
        var profile = new RuntimeProfile
        {
            Id = "prompt-explain",
            Prompt = new ProfilePrompt
            {
                Provider = "natter.feature.prompt.library#default",
                Preset = Library.ActivePreset,
                Locale = Library.Locale,
            },
        };

        return new PromptComposeRequest(
            profile,
            Library.Presets,
            Library.Fragments,
            PresetId: Library.ActivePreset,
            Budget: Library.Budget,
            Locale: Library.Locale,
            Platform: "host",
            Protocol: "host",
            ConversationKind: "Unknown",
            UserName: null,
            Variables: null,
            DisabledFragmentIds: Library.DisabledFragments,
            MissingVariables: Library.MissingVariables);
    }

    /// <summary>
    /// Diagnostic text/JSON: preset id/version, fragment ids, disabled ids + reasons, warnings and
    /// the plan hash. Prompt text and variable values are never included.
    /// </summary>
    public string Describe(bool json)
    {
        PromptPlan plan;
        try
        {
            plan = new PromptComposer().Compose(BuildExplainRequest());
        }
        catch (PromptCompilationException exception)
        {
            return json
                ? JsonSerializer.Serialize(new { plugin = PluginId, pluginVersion = PluginVersion, error = exception.Code }, JsonOptions)
                : $"prompt explain failed code={exception.Code}";
        }

        if (json)
        {
            return JsonSerializer.Serialize(
                new
                {
                    plugin = PluginId,
                    pluginVersion = PluginVersion,
                    presetId = plan.PresetId,
                    presetVersion = plan.PresetVersion,
                    hash = plan.Hash,
                    fragments = plan.Fragments.Select(fragment => fragment.Id).ToArray(),
                    disabled = plan.Disabled
                        .Select(fragment => new { id = fragment.Id, source = fragment.Source.ToString(), reason = fragment.Reason })
                        .ToArray(),
                    warnings = plan.Warnings,
                },
                JsonOptions);
        }

        var disabled = plan.Disabled.Count == 0
            ? "none"
            : string.Join(", ", plan.Disabled.Select(fragment => $"{fragment.Id}({fragment.Reason})"));
        var warnings = plan.Warnings.Count == 0 ? "none" : string.Join(", ", plan.Warnings);
        return string.Join(
            Environment.NewLine,
            plan.Explain,
            "plugin=" + PluginId + " v=" + PluginVersion,
            "fragments: " + string.Join(", ", plan.Fragments.Select(fragment => fragment.Id)),
            "disabled: " + disabled,
            "warnings: " + warnings);
    }
}
