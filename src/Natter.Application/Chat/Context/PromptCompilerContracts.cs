using System.Text.Json.Serialization;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Prompt Compiler contracts (docs/PROMPT_COMPILER.md). A prompt is a recipe of
/// fragments + a preset, compiled by the host into a <see cref="PromptPlan"/> and a
/// <see cref="PromptBundle"/>. Fragments stay in the source (config/file/plugin) even when a
/// preset disables them, so disabling is always reversible and never deletes text.
/// </summary>
public static class PromptCompilerDefaults
{
    /// <summary>Built-in preset/provider id kept for the default profile (no profile migration).</summary>
    public const string StaticPresetId = "host#static";

    /// <summary>Preset id used when a library does not declare a default preset.</summary>
    public const string FallbackPresetId = "default";

    public const int FragmentMaxCharacters = 8000;

    public const int TotalMaxCharacters = 12000;

    /// <summary>Global hard cap on plugin-contributed fragments (all providers together).</summary>
    public const int PluginMaxFragments = 64;

    /// <summary>Global hard cap on the combined text contributed by plugin fragment providers.</summary>
    public const int PluginTotalMaxCharacters = 32000;

    /// <summary>Fair per-provider quota: one plugin cannot starve the others.</summary>
    public const int PluginFragmentsPerProvider = 16;

    /// <summary>Fair per-provider text quota.</summary>
    public const int PluginCharactersPerProvider = 8000;

    /// <summary>Fragment separator used when materializing the system prompt.</summary>
    public const string FragmentSeparator = "\n\n";

    /// <summary>Default per-provider timeout used by the host fragment aggregator.</summary>
    public const int PluginFragmentTimeoutMilliseconds = 2000;

    public const int PluginFragmentOrderMax = 1_000_000;

    public const int VariableMaxCharacters = 200;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptFragmentSource
{
    Inline,
    File,
    Plugin,
    HostBuiltin,
}

/// <summary>One addressable prompt fragment. The text is never written to normal logs.</summary>
public sealed record PromptFragment(
    string Id,
    string Text,
    PromptFragmentSource Source,
    int Order,
    bool EnabledByDefault = true,
    int MaxCharacters = PromptCompilerDefaults.FragmentMaxCharacters,
    string? Version = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>Preset entry referencing a fragment; <see cref="Enabled"/> false disables only, never deletes.</summary>
public sealed record PromptSectionRef(
    string FragmentId,
    int? Order = null,
    bool? Enabled = null,
    string? Source = null,
    string? OverrideText = null,
    string? Reason = null);

/// <summary>Versioned recipe. <see cref="Extends"/> inherits base sections (child overrides by fragment id).</summary>
public sealed record PromptPreset(
    string Id,
    string Version,
    IReadOnlyList<PromptSectionRef>? Sections,
    string? Extends = null,
    string? Locale = null,
    IReadOnlyDictionary<string, string>? Variables = null);

/// <summary>Diagnostics-only record for a disabled fragment: id/source/reason/hash, never the text.</summary>
public sealed record DisabledPromptFragment(
    string Id,
    PromptFragmentSource Source,
    string Reason,
    string Hash,
    int Order = 0);

/// <summary>Compiled prompt recipe. <see cref="Fragments"/> is the final, ordered, enabled list.</summary>
public sealed record PromptPlan(
    string PresetId,
    string PresetVersion,
    IReadOnlyList<PromptFragment> Fragments,
    IReadOnlyList<DisabledPromptFragment> Disabled,
    string Hash,
    IReadOnlyList<string> Warnings,
    int PluginFragmentCount = 0)
{
    /// <summary>Per-fragment diagnostics: id/source/order only, never the text.</summary>
    public IReadOnlyList<PromptFragmentDiagnostic> FragmentDiagnostics { get; init; } = [];

    /// <summary>Disabled fragments (id/source/reason/hash only).</summary>
    public IReadOnlyList<PromptFragmentDiagnostic> DisabledFragmentDiagnostics { get; init; } = [];

    public int PluginFragmentProviderCount { get; init; }

    public long PluginFragmentElapsedMilliseconds { get; init; }

    /// <summary>One-line diagnostic; contains ids/counts/hash only, never prompt text or variable values.</summary>
    public string Explain
        => $"preset={PresetId} v={PresetVersion} fragments={Fragments.Count} "
           + $"pluginFragments={PluginFragmentCount} providers={PluginFragmentProviderCount} "
           + $"elapsedMs={PluginFragmentElapsedMilliseconds} disabled={Disabled.Count} hash={Hash}";
}

/// <summary>Text-free fragment metadata exposed by plan/context diagnostics.</summary>
public sealed record PromptFragmentDiagnostic(string Id, PromptFragmentSource Source, int Order);

/// <summary>Compilation failure. <see cref="Code"/> is a stable <c>prompt.*</c> code.</summary>
public sealed class PromptCompilationException(string code, string message)
    : InvalidOperationException($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>Stable codes used by host and tests.</summary>
public static class PromptCompilerCodes
{
    public const string FragmentInvalid = "prompt.fragment_invalid";
    public const string FragmentDuplicate = "prompt.fragment_duplicate";
    public const string FragmentMissing = "prompt.fragment_missing";
    public const string FragmentSourceForbidden = "prompt.fragment_source_forbidden";
    public const string PluginFragmentLimit = "prompt.fragment_limit";
    public const string PluginFragmentProviderLimit = "prompt.fragment_provider_limit";
    public const string PluginFragmentProviderBudget = "prompt.fragment_provider_budget";
    public const string PresetInvalid = "prompt.preset_invalid";
    public const string PresetDuplicate = "prompt.preset_duplicate";
    public const string PresetMissing = "prompt.preset_missing";
    public const string PresetCycle = "prompt.preset_cycle";
    public const string VariableMissing = "prompt.variable_missing";
    public const string VariableNotAllowed = "prompt.variable_not_allowed";
    public const string VariableTruncated = "prompt.variable_truncated";

    public const string FragmentTruncated = "prompt.fragment_truncated";
    public const string BudgetExceeded = "prompt.budget_exceeded";
    public const string SourceMismatch = "prompt.fragment_source_mismatch";

    // Aggregator warnings (plugin fragments are dropped, the prompt still compiles).
    public const string FragmentTimeout = "prompt.fragment_timeout";
    public const string FragmentFailed = "prompt.fragment_failed";
    public const string FragmentSourceRejected = "prompt.fragment_source_rejected";
    public const string FragmentIdRejected = "prompt.fragment_id_rejected";
    public const string FragmentTextRejected = "prompt.fragment_text_rejected";
    public const string FragmentTooLong = "prompt.fragment_too_long";
    public const string FragmentConflict = "prompt.fragment_conflict";
    public const string FragmentBudget = "prompt.fragment_budget";
}

public enum PromptMissingVariableBehavior
{
    Warning,
    Error,
}

/// <summary>Length budgets applied at compile time. Defaults match the documented 8000 / 12000.</summary>
public sealed record PromptBudget(
    int MaxCharacters = PromptCompilerDefaults.TotalMaxCharacters,
    int MaxFragmentCharacters = PromptCompilerDefaults.FragmentMaxCharacters)
{
    /// <summary>No truncation. Used by the <c>host#static</c> adapter so profile text is byte-identical to P0.</summary>
    public static PromptBudget Unbounded { get; } = new(int.MaxValue, int.MaxValue);
}

/// <summary>
/// Input to the host composer. Callers only provide data (presets/fragments); the composer injects
/// nothing: safety is an ordinary fragment in the preset and can be disabled like any other.
/// </summary>
public sealed record PromptComposeRequest(
    RuntimeProfile Profile,
    IReadOnlyList<PromptPreset>? Presets,
    IReadOnlyList<PromptFragment>? Fragments,
    string? PresetId = null,
    PromptBudget? Budget = null,
    string? Locale = null,
    string? Platform = null,
    string? Protocol = null,
    string? ConversationKind = null,
    string? UserName = null,
    IReadOnlyDictionary<string, string>? Variables = null,
    IReadOnlyList<string>? DisabledFragmentIds = null,
    PromptMissingVariableBehavior MissingVariables = PromptMissingVariableBehavior.Warning,
    IReadOnlyList<PromptFragment>? PluginFragments = null,
    IReadOnlyList<string>? PluginFragmentWarnings = null,
    int PluginFragmentProviderCount = 0,
    long PluginFragmentElapsedMilliseconds = 0)
{
    /// <summary>Builds a host request from an <see cref="IPromptProvider"/> call.</summary>
    public static PromptComposeRequest FromPromptRequest(
        PromptRequest request,
        IReadOnlyList<PromptPreset>? presets,
        IReadOnlyList<PromptFragment>? fragments,
        string? presetId = null,
        PromptBudget? budget = null,
        PromptMissingVariableBehavior missingVariables = PromptMissingVariableBehavior.Warning)
    {
        ArgumentNullException.ThrowIfNull(request);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in request.Profile.Prompt.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;
        foreach (var pair in request.Variables ?? new Dictionary<string, string>())
            variables[pair.Key] = pair.Value;

        return new PromptComposeRequest(
            request.Profile,
            presets,
            fragments,
            PresetId: string.IsNullOrWhiteSpace(presetId) ? null : presetId,
            Budget: budget,
            Locale: request.Locale ?? request.Profile.Prompt.Locale,
            Platform: request.Source.Platform,
            Protocol: request.Source.Protocol,
            ConversationKind: request.Conversation.Kind.ToString(),
            UserName: request.User.Name,
            Variables: variables,
            DisabledFragmentIds: request.Profile.Prompt.DisabledFragments,
            MissingVariables: missingVariables);
    }
}

/// <summary>
/// Session identity handed to prompt fragment providers (STATE_VITALS_DESIGN.md §11.1). Providers
/// that only need platform/locale keep ignoring it; providers that need "who am I talking to" (for
/// example per-conversation content next to a global character state) can read it.
/// </summary>
/// <param name="SourceKey">Structured <c>protocol:platform:selfId</c> source key, never parsed back.</param>
/// <param name="ConversationId">Platform conversation id (opaque).</param>
/// <param name="GuildId">Guild/space id when the platform has one.</param>
/// <param name="ActorId">Acting user id (opaque). Absent for platform-level prompts.</param>
public sealed record ConversationScope(
    string SourceKey,
    string ConversationId,
    string? GuildId = null,
    string? ActorId = null)
{
    /// <summary>Projects the facts of one <see cref="PromptRequest"/> into a scope.</summary>
    public static ConversationScope FromPromptRequest(PromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ConversationScope(
            request.Source.Key,
            request.Conversation.Id,
            request.Conversation.GuildId,
            string.IsNullOrWhiteSpace(request.User.Id) ? null : request.User.Id);
    }
}

/// <summary>
/// Target facts exposed to <see cref="IPromptFragmentProvider"/> implementations.
/// <see cref="Conversation"/> is an <c>init</c>-only addition on purpose: it must not become a
/// positional parameter, because changing the positional signature would break every existing
/// provider (Prompt Library, test fixtures) at compile time. <c>null</c> keeps the pre-existing
/// behaviour byte-for-byte.
/// </summary>
public sealed record PromptFragmentContext(
    RuntimeProfile Profile,
    string? PresetId,
    string? Locale,
    string Platform,
    string Protocol,
    ConversationKind ConversationKind,
    IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>Session identity; <c>null</c> when the caller has none (e.g. <c>prompt explain</c>).</summary>
    public ConversationScope? Conversation { get; init; }
}

/// <summary>
/// Plugin contribution point for fragments. The host aggregates providers through
/// <see cref="IPromptFragmentSource"/> and hands the cleaned result to the composer.
/// </summary>
public interface IPromptFragmentProvider
{
    ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
        PromptFragmentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned compiler contract: plugins provide data, the host compiles and appends safety.</summary>
public interface IPromptComposer
{
    ValueTask<PromptPlan> ComposeAsync(PromptComposeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Materializes the plan into the legacy <see cref="PromptBundle"/> shape.</summary>
    PromptBundle ToBundle(PromptPlan plan);
}
