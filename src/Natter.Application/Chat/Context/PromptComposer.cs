using System.Text.RegularExpressions;

namespace Natter.Application.Chat;

/// <summary>
/// Host-owned Prompt Compiler. Resolves a preset (profile → locale → default → builtin fallback),
/// merges inheritance, applies enable/disable precedence, orders fragments deterministically,
/// substitutes whitelisted variables, enforces budgets and hashes the resulting recipe. The
/// composer injects nothing: safety is an ordinary fragment in the preset. Stateless/deterministic.
/// </summary>
public sealed class PromptComposer : IPromptComposer
{
    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:#-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex VariablePattern = new(
        @"\{\{\s*(?<name>[A-Za-z0-9_.]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public ValueTask<PromptPlan> ComposeAsync(
        PromptComposeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Compose(request));
    }

    public PromptPlan Compose(PromptComposeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        var fragments = PrepareFragments(request, warnings);
        var preset = ResolvePreset(request, fragments);
        var candidates = BuildCandidates(request, preset, fragments, warnings);
        var values = BuildVariableValues(request, preset.Variables);

        var usedVariables = new SortedSet<string>(StringComparer.Ordinal);
        var resolved = new List<Candidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var text = ResolveVariables(
                candidate.Fragment.Id,
                candidate.Text,
                values,
                request.MissingVariables,
                usedVariables,
                warnings);
            resolved.Add(candidate with { Text = text });
        }

        var kept = ApplyBudget(resolved, request.Budget ?? new PromptBudget(), warnings);
        var disabled = ResolveDisabledRecords(request, preset, fragments);
        // The plan hash identifies the recipe, not the rendered text: fragment hashes are taken
        // from the pre-substitution source text and variables contribute names only (documented
        // contract §10). Variable values never enter the hash.
        var sourceTextById = candidates.ToDictionary(
            candidate => candidate.Fragment.Id,
            candidate => candidate.Text,
            StringComparer.Ordinal);
        var hash = PromptHasher.HashPlan(
            preset.Id,
            preset.Version,
            kept.Select(fragment => (fragment.Id, sourceTextById[fragment.Id])),
            disabled.Select(record => record.Id),
            usedVariables);

        var pluginFragmentCount = kept.Count(fragment => fragment.Source == PromptFragmentSource.Plugin);
        var allWarnings = Deduplicate(
            (request.PluginFragmentWarnings ?? []).Concat(warnings));

        return new PromptPlan(
            preset.Id,
            preset.Version,
            kept,
            disabled,
            hash,
            allWarnings,
            pluginFragmentCount)
        {
            FragmentDiagnostics = kept
                .Select(fragment => new PromptFragmentDiagnostic(fragment.Id, fragment.Source, fragment.Order))
                .ToArray(),
            DisabledFragmentDiagnostics = disabled
                .Select(fragment => new PromptFragmentDiagnostic(fragment.Id, fragment.Source, fragment.Order))
                .ToArray(),
            PluginFragmentProviderCount = Math.Max(0, request.PluginFragmentProviderCount),
            PluginFragmentElapsedMilliseconds = Math.Max(0, request.PluginFragmentElapsedMilliseconds),
        };
    }

    public PromptBundle ToBundle(PromptPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var text = string.Join(PromptCompilerDefaults.FragmentSeparator, plan.Fragments.Select(fragment => fragment.Text));
        var sections = plan.Fragments
            .Select(fragment => new PromptSection(fragment.Id, fragment.Text, fragment.Order))
            .ToArray();

        return new PromptBundle(text, Persona: null, Version: plan.PresetVersion, Sections: sections)
        {
            PlanHash = plan.Hash,
            Plan = plan,
            PluginFragmentCount = plan.PluginFragmentCount,
            PluginFragmentProviderCount = plan.PluginFragmentProviderCount,
            PluginFragmentElapsedMilliseconds = plan.PluginFragmentElapsedMilliseconds,
            FragmentDiagnostics = plan.FragmentDiagnostics,
            DisabledFragmentDiagnostics = plan.DisabledFragmentDiagnostics,
        };
    }

    private static List<PromptFragment> PrepareFragments(PromptComposeRequest request, List<string> warnings)
    {
        var fragments = new List<PromptFragment>();

        foreach (var fragment in request.Fragments ?? [])
        {
            if (string.IsNullOrWhiteSpace(fragment.Id) || !IdPattern.IsMatch(fragment.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentInvalid,
                    "Fragment ids must match [A-Za-z0-9][A-Za-z0-9._:#-]{0,127}.");

            if (fragment.Text is null)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentInvalid,
                    $"Fragment '{fragment.Id}' has no text.");

            if (fragment.MaxCharacters > PromptCompilerDefaults.FragmentMaxCharacters
                && fragment.Source is PromptFragmentSource.File)
            {
                warnings.Add($"{PromptCompilerCodes.FragmentTruncated}:{fragment.Id}");
                fragments.Add(fragment with { MaxCharacters = PromptCompilerDefaults.FragmentMaxCharacters });
                continue;
            }

            fragments.Add(fragment);
        }

        var hostIds = fragments
            .Select(fragment => fragment.Id)
            .ToHashSet(StringComparer.Ordinal);

        // Plugin fragments arrive namespaced (owner#local) from HostPromptFragmentSource. The
        // composer stays strict so a direct caller cannot smuggle malformed guest data in, but a
        // defensive host-id collision drops only the guest fragment: a plugin must never fail the
        // whole prompt. HostBuiltin stays reserved for the host.
        var pluginFragments = new List<PromptFragment>();
        foreach (var fragment in request.PluginFragments ?? [])
        {
            if (fragment is null)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentInvalid,
                    "A plugin fragment entry is null.");

            if (fragment.Source is PromptFragmentSource.HostBuiltin)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentSourceForbidden,
                    $"Plugin fragment '{fragment.Id}' claims hostBuiltin source.");

            if (!PromptPluginFragmentRules.IsValidNamespacedId(fragment.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentIdRejected,
                    $"Plugin fragment id '{fragment.Id}' must match {PromptPluginFragmentRules.NamespacedIdPattern}.");

            if (string.IsNullOrWhiteSpace(fragment.Text))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentTextRejected,
                    $"Plugin fragment '{fragment.Id}' has no text.");

            if (fragment.Text.Length > PromptCompilerDefaults.FragmentMaxCharacters)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentTooLong,
                    $"Plugin fragment '{fragment.Id}' exceeds {PromptCompilerDefaults.FragmentMaxCharacters} characters.");

            if (hostIds.Contains(fragment.Id))
            {
                warnings.Add($"{PromptCompilerCodes.FragmentConflict}:{fragment.Id}");
                continue;
            }

            pluginFragments.Add(fragment with
            {
                Source = PromptFragmentSource.Plugin,
                Order = PromptPluginFragmentRules.NormalizeOrder(fragment.Order),
                MaxCharacters = fragment.MaxCharacters <= 0
                    ? PromptCompilerDefaults.FragmentMaxCharacters
                    : Math.Min(fragment.MaxCharacters, PromptCompilerDefaults.FragmentMaxCharacters),
            });
        }

        if (pluginFragments.Count > PromptCompilerDefaults.PluginMaxFragments)
            throw new PromptCompilationException(
                PromptCompilerCodes.PluginFragmentLimit,
                $"Plugin fragments exceed the host limit of {PromptCompilerDefaults.PluginMaxFragments}.");

        fragments.AddRange(pluginFragments);

        var duplicate = fragments
            .GroupBy(fragment => fragment.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new PromptCompilationException(
                PromptCompilerCodes.FragmentDuplicate,
                $"Fragment id '{duplicate.Key}' is registered more than once.");

        return fragments;
    }

    private static PromptPreset ResolvePreset(PromptComposeRequest request, IReadOnlyList<PromptFragment> fragments)
    {
        var presets = (request.Presets ?? []).ToArray();
        var byId = new Dictionary<string, PromptPreset>(StringComparer.Ordinal);
        foreach (var candidate in presets)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id) || !IdPattern.IsMatch(candidate.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetInvalid,
                    "Preset ids must match [A-Za-z0-9][A-Za-z0-9._:#-]{0,127}.");
            if (string.IsNullOrWhiteSpace(candidate.Version))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetInvalid,
                    $"Preset '{candidate.Id}' must declare a version.");
            if (!byId.TryAdd(candidate.Id, candidate))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetDuplicate,
                    $"Preset id '{candidate.Id}' is registered more than once.");
        }

        var requestedId = FirstNonEmpty(request.PresetId, request.Profile.Prompt.Preset);
        PromptPreset preset;
        if (requestedId is not null)
        {
            if (!byId.TryGetValue(requestedId, out var selected))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetMissing,
                    $"Preset '{requestedId}' is not registered.");

            preset = selected;
        }
        else
        {
            preset = SelectByLocale(presets, request.Locale)
                     ?? byId.GetValueOrDefault(PromptCompilerDefaults.FallbackPresetId)
                     ?? presets.FirstOrDefault()
                     ?? BuildFallbackPreset(fragments);
        }

        return MergeInheritance(preset, byId);
    }

    private static PromptPreset BuildFallbackPreset(IReadOnlyList<PromptFragment> fragments)
        => new(
            PromptCompilerDefaults.FallbackPresetId,
            "fallback",
            fragments
                .OrderBy(fragment => fragment.Order)
                .ThenBy(fragment => fragment.Id, StringComparer.Ordinal)
                .Select(fragment => new PromptSectionRef(fragment.Id, fragment.Order))
                .ToArray());

    private static PromptPreset? SelectByLocale(IReadOnlyList<PromptPreset> presets, string? locale)
    {
        if (presets.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(locale))
        {
            var exact = presets.FirstOrDefault(preset =>
                preset.Locale is not null
                && string.Equals(preset.Locale, locale, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;

            var primary = locale.Split('-', '_')[0];
            var language = presets.FirstOrDefault(preset =>
                preset.Locale is not null
                && (string.Equals(preset.Locale, primary, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(preset.Locale.Split('-', '_')[0], primary, StringComparison.OrdinalIgnoreCase)));
            if (language is not null)
                return language;

            var wildcard = presets.FirstOrDefault(preset => preset.Locale == "*");
            if (wildcard is not null)
                return wildcard;
        }

        return null;
    }

    private static PromptPreset MergeInheritance(PromptPreset preset, IReadOnlyDictionary<string, PromptPreset> byId)
    {
        var chain = new List<PromptPreset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = preset;
        while (true)
        {
            if (!seen.Add(current.Id))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetCycle,
                    $"Preset inheritance contains a cycle at '{current.Id}'.");

            chain.Insert(0, current);

            if (string.IsNullOrWhiteSpace(current.Extends))
                break;

            if (!byId.TryGetValue(current.Extends, out var basePreset))
                throw new PromptCompilationException(
                    PromptCompilerCodes.PresetMissing,
                    $"Preset '{current.Id}' extends missing preset '{current.Extends}'.");

            current = basePreset;
        }

        var sections = new List<PromptSectionRef>();
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in chain)
        {
            foreach (var section in item.Sections ?? [])
            {
                if (indexById.TryGetValue(section.FragmentId, out var index))
                {
                    // Child preset overrides the base entry for the same fragment; order is kept
                    // from the child when the child specifies one (otherwise the base order stays).
                    var previous = sections[index];
                    sections[index] = section.Order is null
                        ? section with { Order = previous.Order }
                        : section;
                }
                else
                {
                    indexById[section.FragmentId] = sections.Count;
                    sections.Add(section);
                }
            }

            foreach (var pair in item.Variables ?? new Dictionary<string, string>())
                variables[pair.Key] = pair.Value;
        }

        return preset with { Sections = sections, Variables = variables };
    }

    private static List<Candidate> BuildCandidates(
        PromptComposeRequest request,
        PromptPreset preset,
        IReadOnlyList<PromptFragment> fragments,
        List<string> warnings)
    {
        var byId = fragments.ToDictionary(fragment => fragment.Id, StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();

        foreach (var section in preset.Sections ?? [])
        {
            if (!byId.TryGetValue(section.FragmentId, out var fragment))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentMissing,
                    $"Preset '{preset.Id}' references missing fragment '{section.FragmentId}'.");

            if (!referenced.Add(section.FragmentId))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentDuplicate,
                    $"Preset '{preset.Id}' references fragment '{section.FragmentId}' more than once.");

            if (!string.IsNullOrWhiteSpace(section.Source)
                && !string.Equals(section.Source, fragment.Source.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{PromptCompilerCodes.SourceMismatch}:{fragment.Id}");
            }

            var text = section.OverrideText ?? fragment.Text;

            var order = section.Order ?? fragment.Order;
            var disabledReason = NormalizeReason(section.Reason);
            var explicitlyDisabled = section.Enabled == false;
            var profileDisabled = IsDisabledByProfile(request, fragment.Id);

            if (explicitlyDisabled || profileDisabled)
                continue;

            if (section.Enabled == true)
            {
                candidates.Add(new Candidate(fragment, text, order));
                continue;
            }

            if (!fragment.EnabledByDefault)
                continue;

            candidates.Add(new Candidate(fragment, text, order));
        }

        // Plugin fragments are guests: they are appended (after preset-referenced fragments) even
        // when the preset cannot know their ids. Host/library fragments are not auto-included.
        var referencedByPreset = (preset.Sections ?? [])
            .Select(section => section.FragmentId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var plugin in fragments
                     .Where(fragment => fragment.Source == PromptFragmentSource.Plugin
                                        && !referencedByPreset.Contains(fragment.Id))
                     .OrderBy(fragment => fragment.Order)
                     .ThenBy(fragment => fragment.Id, StringComparer.Ordinal))
        {
            if (IsDisabledByProfile(request, plugin.Id))
                continue;

            if (!plugin.EnabledByDefault)
                continue;

            candidates.Add(new Candidate(plugin, plugin.Text, plugin.Order));
        }

        return candidates
            .OrderBy(candidate => candidate.Order)
            .ThenBy(candidate => candidate.Fragment.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsDisabledByProfile(PromptComposeRequest request, string fragmentId)
        => request.DisabledFragmentIds is { Count: > 0 } ids
           && ids.Contains(fragmentId, StringComparer.Ordinal);

    private static IReadOnlyList<DisabledPromptFragment> ResolveDisabledRecords(
        PromptComposeRequest request,
        PromptPreset preset,
        IReadOnlyList<PromptFragment> fragments)
    {
        var byId = fragments.ToDictionary(fragment => fragment.Id, StringComparer.Ordinal);
        var records = new List<DisabledPromptFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var section in preset.Sections ?? [])
        {
            if (!byId.TryGetValue(section.FragmentId, out var fragment))
                continue;

            var explicitlyDisabled = section.Enabled == false;
            var profileDisabled = IsDisabledByProfile(request, fragment.Id);
            if (!explicitlyDisabled && !profileDisabled && section.Enabled.HasValue)
                continue;

            if (!explicitlyDisabled && !profileDisabled && fragment.EnabledByDefault)
                continue;

            var reason = explicitlyDisabled
                ? NormalizeReason(section.Reason)
                : profileDisabled
                    ? NormalizeReason(section.Reason, "profile")
                    : "default";

            if (seen.Add(fragment.Id))
                records.Add(new DisabledPromptFragment(
                    fragment.Id,
                    fragment.Source,
                    reason,
                    PromptHasher.ShortHash(fragment.Text),
                    fragment.Order));
        }

        // Unreferenced plugin fragments: profile disable wins, otherwise a fragment that opted out
        // by default is recorded so toggling it is visible in the plan hash.
        var referencedByPreset = (preset.Sections ?? [])
            .Select(section => section.FragmentId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var plugin in fragments
                     .Where(fragment => fragment.Source == PromptFragmentSource.Plugin
                                        && !referencedByPreset.Contains(fragment.Id))
                     .OrderBy(fragment => fragment.Order)
                     .ThenBy(fragment => fragment.Id, StringComparer.Ordinal))
        {
            var profileDisabled = IsDisabledByProfile(request, plugin.Id);
            if (!profileDisabled && plugin.EnabledByDefault)
                continue;

            if (seen.Add(plugin.Id))
                records.Add(new DisabledPromptFragment(
                    plugin.Id,
                    plugin.Source,
                    profileDisabled ? "profile" : "default",
                    PromptHasher.ShortHash(plugin.Text),
                    plugin.Order));
        }

        return records;
    }

    private static string NormalizeReason(string? reason, string fallback = "preset")
    {
        if (string.IsNullOrWhiteSpace(reason))
            return fallback;

        var cleaned = new string(reason.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return fallback;
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static IReadOnlyDictionary<string, string> BuildVariableValues(
        PromptComposeRequest request,
        IReadOnlyDictionary<string, string>? presetVariables)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in presetVariables ?? new Dictionary<string, string>())
        {
            if (PromptVariables.IsAllowed(pair.Key))
                values[pair.Key] = PromptVariables.Escape(pair.Value);
        }

        foreach (var pair in request.Variables ?? new Dictionary<string, string>())
        {
            if (PromptVariables.IsAllowed(pair.Key))
                values[pair.Key] = PromptVariables.Escape(pair.Value);
        }

        // Runtime facts win over preset/profile supplied values; they are not spoofable by config.
        if (!string.IsNullOrWhiteSpace(request.Profile.Id))
            values["profile.id"] = PromptVariables.Escape(request.Profile.Id);
        if (!string.IsNullOrWhiteSpace(request.Platform))
            values["platform"] = PromptVariables.Escape(request.Platform);
        if (!string.IsNullOrWhiteSpace(request.Locale))
            values["locale"] = PromptVariables.Escape(request.Locale);
        if (!string.IsNullOrWhiteSpace(request.ConversationKind))
            values["conversation.kind"] = PromptVariables.Escape(request.ConversationKind);
        if (!string.IsNullOrWhiteSpace(request.UserName))
            values["user.name"] = PromptVariables.Escape(request.UserName);

        return values;
    }

    private static string ResolveVariables(
        string fragmentId,
        string text,
        IReadOnlyDictionary<string, string> values,
        PromptMissingVariableBehavior missingBehavior,
        ISet<string> used,
        List<string> warnings)
    {
        if (text.IndexOf("{{", StringComparison.Ordinal) < 0)
            return text;

        return VariablePattern.Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (!PromptVariables.IsAllowed(name))
            {
                warnings.Add($"{PromptCompilerCodes.VariableNotAllowed}:{name}");
                return match.Value;
            }

            used.Add(name);

            if (values.TryGetValue(name, out var value))
                return value;

            if (missingBehavior is PromptMissingVariableBehavior.Error)
                throw new PromptCompilationException(
                    PromptCompilerCodes.VariableMissing,
                    $"Fragment '{fragmentId}' requires variable '{name}' but no value was supplied.");

            warnings.Add($"{PromptCompilerCodes.VariableMissing}:{name}");
            return string.Empty;
        });
    }

    private static IReadOnlyList<PromptFragment> ApplyBudget(
        IReadOnlyList<Candidate> candidates,
        PromptBudget budget,
        List<string> warnings)
    {
        var max = (long)Math.Max(1, budget.MaxCharacters);
        var fragmentLimit = Math.Max(1, budget.MaxFragmentCharacters);
        var kept = new List<PromptFragment>(candidates.Count);
        long used = 0;

        foreach (var candidate in candidates)
        {
            var text = Truncate(candidate, fragmentLimit, warnings);
            var separator = kept.Count > 0 ? 2 : 0;
            var room = max - used - separator;

            if (room <= 0)
            {
                warnings.Add($"{PromptCompilerCodes.BudgetExceeded}:{candidate.Fragment.Id}");
                continue;
            }

            if (text.Length > room)
            {
                text = text[..(int)room];
                warnings.Add($"{PromptCompilerCodes.BudgetExceeded}:{candidate.Fragment.Id}");
            }

            kept.Add(candidate.Fragment with { Text = text });
            used += text.Length + separator;
        }

        return kept;
    }

    private static string Truncate(Candidate candidate, int fragmentLimit, List<string> warnings)
    {
        var limit = candidate.Fragment.MaxCharacters <= 0
            ? fragmentLimit
            : Math.Min(candidate.Fragment.MaxCharacters, fragmentLimit);

        if (candidate.Text.Length <= limit)
            return candidate.Text;

        warnings.Add($"{PromptCompilerCodes.FragmentTruncated}:{candidate.Fragment.Id}");
        return candidate.Text[..limit];
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> warnings)
        => warnings.Distinct(StringComparer.Ordinal).ToArray();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private sealed record Candidate(PromptFragment Fragment, string Text, int Order);
}
