namespace Natter.Plugin.StateVitals.Definitions;

/// <summary>Closed numeric interval of one vital.</summary>
public sealed record VitalRange(double Min, double Max)
{
    public double Span => Max - Min;

    public double Clamp(double value) => Math.Clamp(double.IsNaN(value) ? Min : value, Min, Max);
}

/// <summary>Decay mode. First version supports exactly one; a second mode must be added on purpose.</summary>
public enum VitalDecayMode
{
    /// <summary><c>v_next = v + (target - v) * clamp(k*Δt, 0, MAX_STEP)</c> (design §3.2).</summary>
    Multiplicative,
}

/// <summary>Stacking rule declared per effect (design §9.6: never a global default).</summary>
public enum VitalApplyRule
{
    Add = 0,
    Max = 1,
    Mul = 2,
}

/// <summary>What an effect changes on the target vital.</summary>
public enum VitalEffectKind
{
    /// <summary>Multiplies <c>k</c>; requires <see cref="VitalApplyRule.Mul"/>.</summary>
    RateMultiplier,

    /// <summary>Additive push in units per hour; requires add/max.</summary>
    Bias,
}

/// <summary>
/// Decay target (design §3.2 point 3): a constant cap/floor or a function of time. Keeping this
/// polymorphic is what removes the need for per-vital maths (menstrual cycle, day/night curve).
/// </summary>
public abstract record VitalTarget
{
    public abstract double Resolve(DateTimeOffset at);

    public sealed record Constant(double Value) : VitalTarget
    {
        public override double Resolve(DateTimeOffset at) => Value;
    }

    /// <summary>Cosine cycle: day/night sleepiness, a 28-day cycle, anything periodic.</summary>
    public sealed record Cycle(double Min, double Max, double PeriodHours, double PeakHourUtc) : VitalTarget
    {
        public override double Resolve(DateTimeOffset at)
        {
            var middle = (Min + Max) / 2d;
            var amplitude = (Max - Min) / 2d;
            var phase = (at.UtcDateTime.TimeOfDay.TotalHours - PeakHourUtc) / PeriodHours * 2d * Math.PI;
            return middle + amplitude * Math.Cos(phase);
        }
    }
}

/// <summary>One bucket: values <c>&gt;= At</c> and below the next level's <c>At</c> belong to it.</summary>
public sealed record VitalLevel(
    string Id,
    double At,
    string Label,
    string? Semantic,
    string? Template);

/// <summary>Declarative decay configuration of one vital.</summary>
public sealed record VitalDecay(VitalDecayMode Mode, double RatePerHour, VitalTarget Target);

/// <summary>
/// One vital: a numeric integrator. Vitals are always pack-scoped (<c>core#hunger</c>) and are the
/// only place where continuous values live.
/// </summary>
public sealed record VitalDefinition(
    string Id,
    string PackId,
    string Label,
    VitalRange Range,
    double Baseline,
    VitalDecay Decay,
    IReadOnlyList<VitalLevel> Levels,
    bool Abstract,
    string? Parent)
{
    /// <summary>Local id without the pack prefix (used for display and diagnostics).</summary>
    public string LocalId => PackScopedId.LocalPart(Id);

    /// <summary>Bucket lookup: the highest level whose <c>At</c> is not above the value.</summary>
    public VitalLevel ResolveLevel(double value)
    {
        var matched = Levels[0];
        foreach (var level in Levels)
        {
            if (value + 1e-9 >= level.At)
                matched = level;
            else
                break;
        }

        return matched;
    }
}

/// <summary>The three state/detail primitives beyond vitals (design §9.2).</summary>
public enum StatePrimitiveKind
{
    Vital,
    AssertedState,
    DerivedState,
    Effect,
    Trigger,
}

/// <summary>
/// An externally asserted state: writable, persisted with an expiry, and the only state that may
/// originate from outside (design §3.1 / §2 早定格 6).
/// </summary>
public sealed record AssertedStateDefinition(
    string Id,
    string PackId,
    string Label,
    string? Semantic,
    string? Template,
    TimeSpan? Duration,
    bool Abstract,
    string? Parent,
    string? MayRequire)
{
    public string LocalId => PackScopedId.LocalPart(Id);
}

/// <summary>
/// A derived state: a read-only view computed from one vital's bucket. Never persisted, so changing
/// thresholds re-interprets history instead of migrating it (design §3.1).
/// </summary>
public sealed record DerivedStateDefinition(
    string Id,
    string PackId,
    string Label,
    string VitalId,
    IReadOnlyList<string> Levels,
    string? Semantic,
    string? Template)
{
    public string LocalId => PackScopedId.LocalPart(Id);
}

/// <summary>
/// One connection in the one-way graph: <c>state → vital</c>. Because the source must be a state and
/// the target must be a vital, a cycle cannot be expressed (design §3.1 / §9.2).
/// </summary>
public sealed record StateEffect(
    string Id,
    string PackId,
    string FromStateId,
    string ToVitalId,
    VitalEffectKind Kind,
    VitalApplyRule Rule,
    double Amount,
    int Priority)
{
    public string LocalId => PackScopedId.LocalPart(Id);
}

/// <summary>Where an asserted state comes from (design §9.2 trigger).</summary>
public sealed record TriggerDefinition(
    string Id,
    string PackId,
    string Event,
    string StateId,
    double Probability,
    TimeSpan? Duration,
    string? MayRequire)
{
    public string LocalId => PackScopedId.LocalPart(Id);
}

/// <summary>
/// Pack-supplied description voice (design §6.3 / §12.3): prompt, tone, examples and banned words
/// all come from content. The core plugin never authors wording of its own.
/// </summary>
public sealed record DescriptionVoice(
    string? Prompt,
    string? Tone,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> BannedWords)
{
    public static DescriptionVoice Empty { get; } = new(null, null, [], []);
}

/// <summary>One content pack file (design §9.3).</summary>
public sealed record ContentPack(
    string PackId,
    string Version,
    bool Enabled,
    IReadOnlyList<string> MayRequire,
    DescriptionVoice Voice,
    IReadOnlyList<VitalDefinition> Vitals,
    IReadOnlyList<AssertedStateDefinition> AssertedStates,
    IReadOnlyList<DerivedStateDefinition> DerivedStates,
    IReadOnlyList<StateEffect> Effects,
    IReadOnlyList<TriggerDefinition> Triggers,
    string SourcePath);

/// <summary>A definition skipped because a soft dependency (<c>mayRequire</c>) is not installed.</summary>
public sealed record SkippedDefinition(string PackId, string DefinitionId, string Reason);

/// <summary>
/// The resolved content set: all enabled packs merged into one scope. <see cref="PackVersions"/> and
/// <see cref="DefinitionVersion"/> are persisted with every snapshot so old data can be re-read with
/// the definitions that produced it (design §8.2).
/// </summary>
public sealed record ContentPackSet(
    int SchemaVersion,
    string DefinitionVersion,
    IReadOnlyList<ContentPack> Packs,
    IReadOnlyList<VitalDefinition> Vitals,
    IReadOnlyList<AssertedStateDefinition> AssertedStates,
    IReadOnlyList<DerivedStateDefinition> DerivedStates,
    IReadOnlyList<StateEffect> Effects,
    IReadOnlyList<TriggerDefinition> Triggers,
    IReadOnlyList<string> Rules,
    IReadOnlyList<SkippedDefinition> Skipped)
{
    public static IReadOnlyList<int> SupportedSchemaVersions { get; } = [1];

    public IReadOnlyDictionary<string, string> PackVersions { get; } = Packs
        .ToDictionary(pack => pack.PackId, pack => pack.Version, StringComparer.Ordinal);

    public VitalDefinition? FindVital(string id)
        => Vitals.FirstOrDefault(vital => string.Equals(vital.Id, id, StringComparison.Ordinal));

    public AssertedStateDefinition? FindAsserted(string id)
        => AssertedStates.FirstOrDefault(state => string.Equals(state.Id, id, StringComparison.Ordinal));

    public DerivedStateDefinition? FindDerived(string id)
        => DerivedStates.FirstOrDefault(state => string.Equals(state.Id, id, StringComparison.Ordinal));

    public bool IsKnownState(string id)
        => FindAsserted(id) is not null || FindDerived(id) is not null;
}

/// <summary>Parsing and validation of <c>&lt;packId&gt;#&lt;localId&gt;</c> scoped identifiers.</summary>
public static class PackScopedId
{
    public const string Pattern = "^[a-z0-9][a-z0-9._-]{0,63}#[a-z0-9][a-z0-9._-]{0,63}$";

    public static string Scoped(string packId, string localId) => $"{packId}#{localId}";

    public static bool IsScoped(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        var separator = id.IndexOf('#');
        return separator > 0
               && separator < id.Length - 1
               && LocalOk(id[..separator])
               && LocalOk(id[(separator + 1)..]);
    }

    public static string PackPart(string scopedId)
    {
        var separator = scopedId.IndexOf('#');
        return separator > 0 ? scopedId[..separator] : scopedId;
    }

    public static string LocalPart(string scopedId)
    {
        var separator = scopedId.IndexOf('#');
        return separator > 0 && separator < scopedId.Length - 1 ? scopedId[(separator + 1)..] : scopedId;
    }

    /// <summary>Resolves a reference inside a pack: a local id is promoted to <c>pack#local</c>.</summary>
    public static string Resolve(string currentPackId, string reference)
        => reference.Contains('#', StringComparison.Ordinal) ? reference : Scoped(currentPackId, reference);

    private static bool LocalOk(string value)
        => value.Length is > 0 and <= 64
           && char.IsAsciiLetterOrDigit(value[0])
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
