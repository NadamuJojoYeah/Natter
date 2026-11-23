namespace Natter.Plugin.StateVitals;

/// <summary>
/// Effective State &amp; Vitals configuration. Every default here mirrors
/// <c>config.schema.json</c>; the host applies schema defaults, but tests and direct instantiation
/// go through these values, so they must not drift.
/// </summary>
public sealed class StateVitalsOptions
{
    /// <summary>Opt-in switch. Default false: an installed-but-unconfigured plugin contributes nothing.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Content pack directory (design §9.3), resolved against the plugin directory unless rooted.
    /// The shipped location is <c>definitions/</c>, which the release staging already copies.
    /// </summary>
    public string Packs { get; init; } = StateVitalsDefaults.PacksDirectory;

    /// <summary>Overrides <c>&lt;state&gt;/plugins/&lt;plugin-id&gt;</c>; mostly for tests and operators.</summary>
    public string? StateDirectory { get; init; }

    /// <summary>Identifier of the single global character state row (§2 早定格 1).</summary>
    public string StateId { get; init; } = StateVitalsDefaults.StateId;

    /// <summary>Fragment order; late in the prompt so a level change invalidates little (§5.1).</summary>
    public int FragmentOrder { get; init; } = StateVitalsDefaults.FragmentOrder;

    public bool IncludeDescriptions { get; init; } = true;

    /// <summary>Budget for one description generation attempt; on expiry the template is used (§6.2).</summary>
    public int DescriptionTimeoutMilliseconds { get; init; } = StateVitalsDefaults.DescriptionTimeoutMilliseconds;

    /// <summary>Minimum wall-clock gap between persisted advances. 0 = advance on every read.</summary>
    public int MinAdvanceIntervalSeconds { get; init; }

    /// <summary>MAX_STEP: clamp for <c>k * Δt</c> (§3.2 point 2).</summary>
    public double MaxStep { get; init; } = StateVitalsDefaults.MaxStep;

    /// <summary>Upper clamp for a single elapsed interval, in hours.</summary>
    public int MaxDeltaHours { get; init; } = StateVitalsDefaults.MaxDeltaHours;

    /// <summary>Upper clamp for the total change of one tick, as a fraction of the vital range (§3.4).</summary>
    public double MaxStepPerTickFraction { get; init; } = StateVitalsDefaults.MaxStepPerTickFraction;

    /// <summary>Optimistic-concurrency retries for the version compare-and-set (§7.2).</summary>
    public int MaxAdvanceAttempts { get; init; } = StateVitalsDefaults.MaxAdvanceAttempts;

    /// <summary>Self-imposed fragment cap so the host's per-provider quota is never the binding limit.</summary>
    public int MaxFragmentCharacters { get; init; } = StateVitalsDefaults.MaxFragmentCharacters;

    /// <summary>Quotas that keep pack evaluation bounded (§9.8).</summary>
    public int MaxPacks { get; init; } = StateVitalsDefaults.MaxPacks;

    public int MaxDefinitionsPerKind { get; init; } = StateVitalsDefaults.MaxDefinitionsPerKind;

    public int MaxEffectsPerPack { get; init; } = StateVitalsDefaults.MaxEffectsPerPack;
}

/// <summary>Constants shared by the plugin, its config schema and its tests. One place, no drift.</summary>
public static class StateVitalsDefaults
{
    /// <summary>NPS plugin id; also the state directory name (§8.1).</summary>
    public const string PluginId = "natter.feature.character.state-vitals";

    /// <summary>The one global character state row (§2 早定格 1).</summary>
    public const string StateId = "character";

    /// <summary>Plugin-local fragment id; the host prefixes it as <c>natter.feature.character.state-vitals#state</c>.</summary>
    public const string FragmentId = "state";

    /// <summary>Contribution id declared in <c>plugin.json</c>; must match the registration exactly.</summary>
    public const string ContributionId = "state";

    /// <summary>Pack directory inside the plugin package (copied by the release staging).</summary>
    public const string PacksDirectory = "definitions";

    /// <summary>Late but not last: prompt caching is prefix based (§5.1).</summary>
    public const int FragmentOrder = 800_000;

    public const int DescriptionTimeoutMilliseconds = 300;
    public const int MaxDescriptionCharacters = 240;

    public const double MaxStep = 0.5;
    public const int MaxDeltaHours = 720;
    public const double MaxStepPerTickFraction = 0.5;
    public const int MaxAdvanceAttempts = 8;
    public const int MaxFragmentCharacters = 1000;

    public const int MaxPacks = 16;
    public const int MaxDefinitionsPerKind = 32;
    public const int MaxEffectsPerPack = 128;

    public const string StateFileName = "state.db";
    public const string StateRootEnvironmentVariable = "NATTER_STATE_DIR";
    public const string PluginsDirectoryName = "plugins";

    /// <summary>Fixed-point scale for persisted values (§2 早定格 7): 1/1000 units.</summary>
    public const long ValueScale = 1000;
}

/// <summary>One configuration problem; the pointer mirrors the JSON Pointer style used by NPS.</summary>
public sealed record StateVitalsOptionIssue(string Pointer, string Code, string Message);

/// <summary>Startup failure: a bad definition or a missing state directory must fail loudly.</summary>
public sealed class StateVitalsStartupException : Exception
{
    public StateVitalsStartupException(string code, string message)
        : base($"{code}: {message}")
        => Code = code;

    public string Code { get; }
}

/// <summary>Static validation executed before anything touches disk or registers a contribution.</summary>
public static class StateVitalsOptionsValidator
{
    public static IReadOnlyList<StateVitalsOptionIssue> Validate(StateVitalsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<StateVitalsOptionIssue>();

        if (string.IsNullOrWhiteSpace(options.Packs))
            issues.Add(new("/packs", StateVitalsCodes.ConfigPacksRequired, "packs must be a non-empty directory path."));

        if (!string.IsNullOrWhiteSpace(options.StateId)
            && (options.StateId.Length > 64 || !options.StateId.All(IsStateIdCharacter)))
        {
            issues.Add(new("/stateId", StateVitalsCodes.ConfigStateIdInvalid, $"stateId '{options.StateId}' is not a valid id."));
        }

        if (options.FragmentOrder is < 0 or > 1_000_000)
            issues.Add(new("/fragmentOrder", StateVitalsCodes.ConfigFragmentOrderInvalid, "fragmentOrder must be between 0 and 1000000."));

        if (options.DescriptionTimeoutMilliseconds is < 0 or > 2000)
        {
            issues.Add(new(
                "/descriptionTimeoutMilliseconds",
                StateVitalsCodes.ConfigDescriptionTimeoutInvalid,
                "descriptionTimeoutMilliseconds must be between 0 and 2000."));
        }

        if (options.MinAdvanceIntervalSeconds is < 0 or > 3600)
        {
            issues.Add(new(
                "/minAdvanceIntervalSeconds",
                StateVitalsCodes.ConfigAdvanceIntervalInvalid,
                "minAdvanceIntervalSeconds must be between 0 and 3600."));
        }

        if (options.MaxStep is <= 0 or > 1)
            issues.Add(new("/maxStep", StateVitalsCodes.ConfigMaxStepInvalid, "maxStep must be in (0, 1]."));

        if (options.MaxDeltaHours is < 1 or > 8760)
            issues.Add(new("/maxDeltaHours", StateVitalsCodes.ConfigMaxDeltaHoursInvalid, "maxDeltaHours must be between 1 and 8760."));

        if (options.MaxStepPerTickFraction is < 0 or > 1)
        {
            issues.Add(new(
                "/maxStepPerTickFraction",
                StateVitalsCodes.ConfigMaxStepPerTickInvalid,
                "maxStepPerTickFraction must be between 0 and 1."));
        }

        if (options.MaxAdvanceAttempts is < 1 or > 32)
        {
            issues.Add(new(
                "/maxAdvanceAttempts",
                StateVitalsCodes.ConfigAdvanceAttemptsInvalid,
                "maxAdvanceAttempts must be between 1 and 32."));
        }

        if (options.MaxFragmentCharacters is < 16 or > 8000)
        {
            issues.Add(new(
                "/maxFragmentCharacters",
                StateVitalsCodes.ConfigMaxFragmentCharactersInvalid,
                "maxFragmentCharacters must be between 16 and 8000."));
        }

        if (options.MaxPacks is < 1 or > 64)
            issues.Add(new("/maxPacks", StateVitalsCodes.ConfigQuotaInvalid, "maxPacks must be between 1 and 64."));

        if (options.MaxDefinitionsPerKind is < 1 or > 256)
            issues.Add(new("/maxDefinitionsPerKind", StateVitalsCodes.ConfigQuotaInvalid, "maxDefinitionsPerKind must be between 1 and 256."));

        if (options.MaxEffectsPerPack is < 1 or > 1024)
            issues.Add(new("/maxEffectsPerPack", StateVitalsCodes.ConfigQuotaInvalid, "maxEffectsPerPack must be between 1 and 1024."));

        return issues;
    }

    private static bool IsStateIdCharacter(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}

/// <summary>Stable codes used by the plugin, its tests and diagnostics.</summary>
public static class StateVitalsCodes
{
    // Configuration.
    public const string ConfigPacksRequired = "state-vitals.config.packs-required";
    public const string ConfigStateIdInvalid = "state-vitals.config.state-id-invalid";
    public const string ConfigFragmentOrderInvalid = "state-vitals.config.fragment-order-invalid";
    public const string ConfigDescriptionTimeoutInvalid = "state-vitals.config.description-timeout-invalid";
    public const string ConfigAdvanceIntervalInvalid = "state-vitals.config.advance-interval-invalid";
    public const string ConfigMaxStepInvalid = "state-vitals.config.max-step-invalid";
    public const string ConfigMaxDeltaHoursInvalid = "state-vitals.config.max-delta-hours-invalid";
    public const string ConfigMaxStepPerTickInvalid = "state-vitals.config.max-step-per-tick-invalid";
    public const string ConfigAdvanceAttemptsInvalid = "state-vitals.config.advance-attempts-invalid";
    public const string ConfigMaxFragmentCharactersInvalid = "state-vitals.config.max-fragment-characters-invalid";
    public const string ConfigQuotaInvalid = "state-vitals.config.quota-invalid";
    public const string StateDirectoryRequired = "state-vitals.state-directory-required";

    // Pack discovery.
    public const string PackDirectoryMissing = "state-vitals.pack.directory-missing";
    public const string PackIdInvalid = "state-vitals.pack.id-invalid";
    public const string PackVersionMissing = "state-vitals.pack.version-missing";
    public const string PackDuplicate = "state-vitals.pack.duplicate";
    public const string PackLimit = "state-vitals.pack.limit";
    public const string PackDurationInvalid = "state-vitals.pack.duration-invalid";

    // Definitions.
    public const string DefinitionUnreadable = "state-vitals.definition.unreadable";
    public const string DefinitionInvalidJson = "state-vitals.definition.invalid-json";
    public const string DefinitionSchemaVersionMissing = "state-vitals.definition.schema-version-missing";
    public const string DefinitionSchemaVersionUnsupported = "state-vitals.definition.schema-version-unsupported";
    public const string DefinitionIdInvalid = "state-vitals.definition.id-invalid";
    public const string DefinitionDuplicate = "state-vitals.definition.duplicate";
    public const string DefinitionQuota = "state-vitals.definition.quota";
    public const string DefinitionVitalMissing = "state-vitals.definition.vital-missing";
    public const string DefinitionRangeInvalid = "state-vitals.definition.range-invalid";
    public const string DefinitionBaselineOutOfRange = "state-vitals.definition.baseline-out-of-range";
    public const string DefinitionDecayModeUnsupported = "state-vitals.definition.decay-mode-unsupported";
    public const string DefinitionDecayRateOutOfRange = "state-vitals.definition.decay-rate-out-of-range";
    public const string DefinitionDecayTargetInvalid = "state-vitals.definition.decay-target-invalid";
    public const string DefinitionLevelMissing = "state-vitals.definition.level-missing";
    public const string DefinitionLevelIdInvalid = "state-vitals.definition.level-id-invalid";
    public const string DefinitionLevelDuplicate = "state-vitals.definition.level-duplicate";
    public const string DefinitionLevelNotMonotonic = "state-vitals.definition.level-not-monotonic";
    public const string DefinitionLevelGap = "state-vitals.definition.level-gap";
    public const string DefinitionLevelOutOfRange = "state-vitals.definition.level-out-of-range";
    public const string DefinitionEffectTargetUnknown = "state-vitals.definition.effect-target-unknown";
    public const string DefinitionEffectStackingMissing = "state-vitals.definition.effect-stacking-missing";
    public const string DefinitionEffectStackingUnknown = "state-vitals.definition.effect-stacking-unknown";
    public const string DefinitionEffectStackingIncompatible = "state-vitals.definition.effect-stacking-incompatible";
    public const string DefinitionEffectKindInvalid = "state-vitals.definition.effect-kind-invalid";
    public const string EffectTargetUnknown = "state-vitals.definition.effect-target-unknown";

    // Hierarchy and soft dependencies.
    public const string ParentMissing = "state-vitals.definition.parent-missing";
    public const string ParentCycle = "state-vitals.definition.parent-cycle";

    // Cross references (one-way graph).
    public const string DerivedVitalMissing = "state-vitals.definition.derived-vital-missing";
    public const string DerivedVitalUnknown = "state-vitals.definition.derived-vital-unknown";
    public const string DerivedLevelsMissing = "state-vitals.definition.derived-levels-missing";
    public const string DerivedLevelUnknown = "state-vitals.definition.derived-level-unknown";
    public const string EffectSourceUnknown = "state-vitals.definition.effect-source-unknown";
    public const string EffectSourceMustBeState = "state-vitals.definition.effect-source-must-be-state";
    public const string EffectTargetMustBeVital = "state-vitals.definition.effect-target-must-be-vital";
    public const string TriggerEventMissing = "state-vitals.definition.trigger-event-missing";
    public const string TriggerStateNotAsserted = "state-vitals.definition.trigger-state-not-asserted";
    public const string TriggerProbabilityInvalid = "state-vitals.definition.trigger-probability-invalid";
    public const string RuleUnknown = "state-vitals.rule-unknown";

    // Runtime / diagnostics.
    public const string DescriptionFallback = "state-vitals.description.fallback";
    public const string FragmentSkipped = "state-vitals.fragment.skipped";
    public const string AdvanceContended = "state-vitals.advance.contended";
}

/// <summary>Paths and directory resolution for the plugin's own database (design §8: never natter.db).</summary>
public static class StateVitalsPaths
{
    /// <summary><c>&lt;state&gt;/plugins/natter.feature.character.state-vitals</c>, or the configured override.</summary>
    public static string ResolveStateDirectory(StateVitalsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.StateDirectory))
            return Path.GetFullPath(options.StateDirectory);

        var root = Environment.GetEnvironmentVariable(StateVitalsDefaults.StateRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new StateVitalsStartupException(
                StateVitalsCodes.StateDirectoryRequired,
                $"State directory is unknown; set {StateVitalsDefaults.StateRootEnvironmentVariable} "
                + "or configure stateDirectory. The plugin never writes next to its own package.");
        }

        return Path.GetFullPath(Path.Combine(
            root,
            StateVitalsDefaults.PluginsDirectoryName,
            StateVitalsDefaults.PluginId));
    }

    /// <summary>The plugin's private SQLite file (design §8.1).</summary>
    public static string ResolveDatabasePath(string stateDirectory)
        => Path.Combine(stateDirectory, StateVitalsDefaults.StateFileName);

    /// <summary>
    /// Resolves the pack directory. Relative paths are rooted at the plugin directory and may not
    /// escape it; a rooted path is honoured verbatim (tests and operators).
    /// </summary>
    public static string ResolvePacksDirectory(StateVitalsOptions options, string pluginDirectory)
        => ResolveInsidePlugin(options.Packs, pluginDirectory);

    private static string ResolveInsidePlugin(string relativeOrRooted, string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(pluginDirectory);

        if (Path.IsPathRooted(relativeOrRooted))
            return Path.GetFullPath(relativeOrRooted);

        var root = Path.GetFullPath(pluginDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativeOrRooted));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new StateVitalsStartupException(
                StateVitalsCodes.DefinitionUnreadable,
                $"packs '{relativeOrRooted}' escapes the plugin directory.");
        }

        return candidate;
    }
}
