using Natter.Plugin.StateVitals.Definitions;

namespace Natter.Plugin.StateVitals.Kernel;

/// <summary>A derived state that is currently true, with the bucket that produced it.</summary>
public sealed record ActiveDerivedState(DerivedStateDefinition Definition, string LevelId);

/// <summary>
/// Evaluates the one-way graph (design §3.1): asserted states come from storage, derived states are
/// recomputed from vitals on every read, and effects are the only arrows — always
/// <c>state → vital</c>. Nothing here can write a vital from a derived state, which is what makes the
/// "injury lowers recovery" + "low energy means exhausted" cycle unrepresentable.
/// </summary>
public static class StateResolver
{
    /// <summary>Effects sort after code-hook rules unless the author lowers their priority.</summary>
    public const int EffectPriorityBase = 1000;

    /// <summary>Derived states that are true for the given values, in declaration order.</summary>
    public static IReadOnlyList<ActiveDerivedState> Derived(
        ContentPackSet set,
        IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(values);

        var active = new List<ActiveDerivedState>();
        foreach (var derived in set.DerivedStates)
        {
            var vital = set.FindVital(derived.VitalId);
            if (vital is null)
                continue;

            var value = values.TryGetValue(vital.Id, out var stored) ? stored : vital.Baseline;
            var level = vital.ResolveLevel(value);

            if (derived.Levels.Any(candidate => string.Equals(candidate, level.Id, StringComparison.Ordinal)))
                active.Add(new ActiveDerivedState(derived, level.Id));
        }

        return active;
    }

    /// <summary>States that became true between two snapshots (used for one-shot descriptions).</summary>
    public static IReadOnlyList<ActiveDerivedState> Entered(
        IReadOnlyList<ActiveDerivedState> before,
        IReadOnlyList<ActiveDerivedState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var previous = before.Select(state => state.Definition.Id).ToHashSet(StringComparer.Ordinal);
        return after.Where(state => !previous.Contains(state.Definition.Id)).ToArray();
    }

    /// <summary>States that stopped being true between two snapshots.</summary>
    public static IReadOnlyList<ActiveDerivedState> Left(
        IReadOnlyList<ActiveDerivedState> before,
        IReadOnlyList<ActiveDerivedState> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var current = after.Select(state => state.Definition.Id).ToHashSet(StringComparer.Ordinal);
        return before.Where(state => !current.Contains(state.Definition.Id)).ToArray();
    }

    /// <summary>
    /// Turns active states into vital forces. The order is canonical
    /// <c>(priority, effect id)</c>, so the same active set always yields the same force list.
    /// </summary>
    public static IReadOnlyList<VitalForce> Forces(
        ContentPackSet set,
        IReadOnlySet<string> activeStateIds)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(activeStateIds);

        if (activeStateIds.Count == 0)
            return [];

        return set.Effects
            .Where(effect => activeStateIds.Contains(effect.FromStateId))
            .OrderBy(effect => effect.Priority)
            .ThenBy(effect => effect.Id, StringComparer.Ordinal)
            .Select(effect => new VitalForce(
                effect.ToVitalId,
                EffectPriorityBase + effect.Priority,
                effect.Kind == VitalEffectKind.RateMultiplier ? VitalForceKind.RateMultiplier : VitalForceKind.Bias,
                effect.Amount))
            .ToArray();
    }

    /// <summary>Convenience overload that also folds in the derived states of the given values.</summary>
    public static IReadOnlyList<VitalForce> ForcesFor(
        ContentPackSet set,
        IReadOnlyDictionary<string, double> values,
        IReadOnlySet<string> assertedStateIds)
    {
        var active = new HashSet<string>(assertedStateIds, StringComparer.Ordinal);
        foreach (var derived in Derived(set, values))
            active.Add(derived.Definition.Id);

        return Forces(set, active);
    }
}
