namespace Natter.Plugin.StateVitals.Definitions;

/// <summary>
/// One actionable validation problem (design §10.1): source file, JSON Pointer, stable code, what is
/// wrong, and how the author can fix it. Pack authors are not C# developers, so the suggestion is
/// part of the contract, not a nicety.
/// </summary>
public sealed record PackIssue(string Source, string Pointer, string Code, string Message, string? Suggestion)
{
    public override string ToString()
        => Suggestion is null
            ? $"{Source}{Pointer} [{Code}] {Message}"
            : $"{Source}{Pointer} [{Code}] {Message}{Environment.NewLine}  建议：{Suggestion}";
}

/// <summary>Result of a tolerant load: either a resolved set or the full list of problems.</summary>
public sealed record PackLoadResult(
    ContentPackSet? Set,
    IReadOnlyList<PackIssue> Issues,
    IReadOnlyList<SkippedDefinition> Skipped)
{
    public bool Succeeded => Set is not null && Issues.Count == 0;
}

/// <summary>Quotas that keep evaluation bounded (design §9.8). Exceeding them rejects at load time.</summary>
public sealed record PackLoadOptions(
    int MaxPacks = 16,
    int MaxVitalsPerPack = 32,
    int MaxAssertedStatesPerPack = 32,
    int MaxDerivedStatesPerPack = 32,
    int MaxEffectsPerPack = 128,
    int MaxTriggersPerPack = 32)
{
    public static PackLoadOptions Default { get; } = new();
}
