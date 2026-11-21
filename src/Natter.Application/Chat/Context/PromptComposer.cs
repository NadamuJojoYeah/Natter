using System.Text.RegularExpressions;

namespace Natter.Application.Chat;

/// <summary>
/// Host-owned Prompt Compiler. Resolves a preset (profile → locale → default → builtin fallback),
/// merges inheritance, applies enable/disable precedence, orders fragments deterministically,
/// substitutes whitelisted variables, enforces budgets, appends the host mandatory safety block
/// last and hashes the resulting recipe. The composer is stateless and deterministic.
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

        return new PromptPlan(
            preset.Id,
            preset.Version,
            kept,
            disabled,
            hash,
            Deduplicate(warnings));
    }

    public PromptBundle ToBundle(PromptPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var text = string.Join(HostPromptSafety.Separator, plan.Fragments.Select(fragment => fragment.Text));
        var sections = plan.Fragments
            .Select(fragment => new PromptSection(fragment.Id, fragment.Text, fragment.Order))
            .ToArray();

        return new PromptBundle(text, Persona: null, Version: plan.PresetVersion, Sections: sections)
        {
            PlanHash = plan.Hash,
            Plan = plan,
            SafetyAppended = true,
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

            if (fragment.Source is PromptFragmentSource.HostBuiltin)
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentSourceForbidden,
                    $"Fragment '{fragment.Id}' claims hostBuiltin source; host fragments are injected by the composer.");

            if (fragment.Source is PromptFragmentSource.Plugin && fragment.Mandatory)
                throw new PromptCompilationException(
                    PromptCompilerCodes.PluginMandatoryForbidden,
                    $"Plugin fragment '{fragment.Id}' declares mandatory=true; only the host may do that.");

            if (fragment.MaxCharacters > PromptCompilerDefaults.FragmentMaxCharacters
                && fragment.Source is PromptFragmentSource.Plugin or PromptFragmentSource.File)
            {
                warnings.Add($"{PromptCompilerCodes.FragmentTruncated}:{fragment.Id}");
                fragments.Add(fragment with { MaxCharacters = PromptCompilerDefaults.FragmentMaxCharacters });
                continue;
            }

            fragments.Add(fragment);
        }

        foreach (var mandatory in HostPromptSafety.Fragments)
        {
            if (fragments.Any(fragment => string.Equals(fragment.Id, mandatory.Id, StringComparison.Ordinal)))
                throw new PromptCompilationException(
                    PromptCompilerCodes.FragmentReserved,
                    $"Fragment id '{mandatory.Id}' is reserved for host safety.");

            fragments.Add(mandatory);
        }

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
                .Where(fragment => !fragment.Mandatory)
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

            var text = fragment.Text;
            if (section.OverrideText is not null)
            {
                if (fragment.Mandatory)
                {
                    warnings.Add($"{PromptCompilerCodes.MandatoryIgnored}:{fragment.Id}");
                }
                else
                {
                    text = section.OverrideText;
                }
            }

            var order = section.Order ?? fragment.Order;
            var disabledReason = NormalizeReason(section.Reason);
            var explicitlyDisabled = section.Enabled == false;
            var profileDisabled = IsDisabledByProfile(request, fragment.Id);

            if (fragment.Mandatory)
            {
                if (explicitlyDisabled || profileDisabled)
                    warnings.Add($"{PromptCompilerCodes.MandatoryIgnored}:{fragment.Id}");

                candidates.Add(new Candidate(fragment, text, order));
                continue;
            }

            if (explicitlyDisabled || profileDisabled)
            {
                if (!fragment.CanDisable)
                    throw new PromptCompilationException(
                        PromptCompilerCodes.FragmentNotDisableable,
                        $"Fragment '{fragment.Id}' cannot be disabled.");

                continue;
            }

            if (section.Enabled == true)
            {
                candidates.Add(new Candidate(fragment, text, order));
                continue;
            }

            if (!fragment.EnabledByDefault)
                continue;

            candidates.Add(new Candidate(fragment, text, order));
        }

        // Host mandatory fragments are appended even when the preset does not reference them.
        foreach (var mandatory in HostPromptSafety.Fragments)
        {
            if (!referenced.Add(mandatory.Id))
                continue;

            var profileDisabled = IsDisabledByProfile(request, mandatory.Id);
            if (profileDisabled)
                warnings.Add($"{PromptCompilerCodes.MandatoryIgnored}:{mandatory.Id}");

            candidates.Add(new Candidate(mandatory, mandatory.Text, mandatory.Order));
        }

        return candidates
            .OrderBy(candidate => candidate.Fragment.Mandatory ? 1 : 0)
            .ThenBy(candidate => candidate.Order)
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

            if (fragment.Mandatory)
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
                    PromptHasher.ShortHash(fragment.Text)));
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

        var mandatory = candidates.Where(candidate => candidate.Fragment.Mandatory).ToList();
        var optional = candidates.Where(candidate => !candidate.Fragment.Mandatory).ToList();

        var mandatoryTexts = mandatory
            .Select(candidate => Truncate(candidate, fragmentLimit, warnings))
            .ToList();
        var mandatoryCost = mandatoryTexts.Sum(text => (long)text.Length)
                            + (mandatoryTexts.Count > 1 ? 2L * (mandatoryTexts.Count - 1) : 0);
        var optionalBudget = Math.Max(0, max - mandatoryCost);
        if (mandatoryTexts.Count > 0 && optional.Count > 0)
            optionalBudget = Math.Max(0, optionalBudget - 2);

        var kept = new List<PromptFragment>(candidates.Count);
        long used = 0;

        foreach (var candidate in optional)
        {
            var text = Truncate(candidate, fragmentLimit, warnings);
            var separator = kept.Count > 0 ? 2 : 0;
            var cost = text.Length + separator;

            if (used + cost > optionalBudget)
            {
                var room = optionalBudget - used - separator;
                if (room <= 0)
                {
                    warnings.Add($"{PromptCompilerCodes.BudgetExceeded}:{candidate.Fragment.Id}");
                    continue;
                }

                text = text[..(int)room];
                warnings.Add($"{PromptCompilerCodes.BudgetExceeded}:{candidate.Fragment.Id}");
                cost = text.Length + separator;
            }

            kept.Add(candidate.Fragment with { Text = text });
            used += cost;
        }

        for (var index = 0; index < mandatory.Count; index++)
        {
            var candidate = mandatory[index];
            var text = mandatoryTexts[index];
            var separator = kept.Count > 0 ? 2 : 0;

            if (used + separator + text.Length > max)
            {
                var room = max - used - separator;
                if (room < 0)
                    room = 0;

                text = text.Length > room ? text[..(int)room] : text;
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
