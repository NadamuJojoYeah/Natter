using Natter.Plugin.StateVitals.Definitions;

namespace Natter.Plugin.StateVitals.Kernel;

/// <summary>What a force changes: the decay speed, an additive per-hour push, or a one-shot delta.</summary>
public enum VitalForceKind
{
    RateMultiplier,

    /// <summary>Additive push in units per hour; scales with the elapsed interval (declarative effects).</summary>
    Bias,

    /// <summary>
    /// One-shot absolute change applied exactly once in the tick that consumes it (event pushes).
    /// It must not scale with Δt: a retried commit inside the same instant would otherwise lose it.
    /// </summary>
    Delta,
}

/// <summary>
/// One collected push towards a vital for a single tick. Forces are data, not callbacks: they are
/// gathered first and applied once (design §3.4 point 2).
/// </summary>
public readonly record struct VitalForce(string VitalId, int SourcePriority, VitalForceKind Kind, double Amount);

/// <summary>Clamps that make offline catch-up safe (design §3.2 point 2 and §3.4 point 3).</summary>
public sealed record VitalAdvanceOptions(
    double MaxStep = 0.5,
    double MaxDeltaHours = 720,
    double MaxStepPerTickFraction = 0.5);

/// <summary>New values after one tick. Every value is quantized to 1/1000 (§12.2).</summary>
public sealed record VitalAdvanceResult(IReadOnlyDictionary<string, double> Values);

/// <summary>
/// The numeric core (design §3.2): first-order lag with multiplicative saturation,
/// <c>v_next = v + (target - v) * clamp(k*Δt, 0, MAX_STEP)</c>.
///
/// <para>
/// Three properties are load-bearing and covered by tests: the increment is a fraction of the
/// <em>remaining space</em> (so it can never overshoot and slows down near the target), <c>k*Δt</c>
/// is clamped (so an offline week cannot overshoot), and the total change of one tick is clamped
/// again (so several forces cannot add up to a jump).
/// </para>
///
/// <para>
/// Forces are collected per vital and folded in a canonical order
/// <c>(sourcePriority, kind, amount)</c>, so the result is a pure function of the force multiset:
/// neither collection order nor enumeration order can influence it (§3.4 point 2).
/// </para>
///
/// <para>
/// The kernel only ever sees vitals. It has no notion of states, which is what keeps the dependency
/// direction one-way (<c>asserted → vitals → derived</c>, §3.1).
/// </para>
/// </summary>
public static class VitalKernel
{
    /// <summary>Advances every vital from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static VitalAdvanceResult Advance(
        IReadOnlyList<VitalDefinition> vitals,
        IReadOnlyDictionary<string, double> values,
        DateTimeOffset from,
        DateTimeOffset to,
        IEnumerable<VitalForce>? forces = null,
        VitalAdvanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vitals);
        ArgumentNullException.ThrowIfNull(values);

        options ??= new VitalAdvanceOptions();
        var collected = Collect(forces);
        var elapsedHours = ElapsedHours(from, to, options);
        var next = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var vital in vitals)
        {
            var current = VitalFixedPoint.Quantize(
                values.TryGetValue(vital.Id, out var stored) ? vital.Range.Clamp(stored) : vital.Range.Clamp(vital.Baseline));

            var forcesForVital = collected.TryGetValue(vital.Id, out var list) ? list : [];

            var multiplier = 1d;
            var bias = 0d;
            var deltaSum = 0d;
            foreach (var force in forcesForVital)
            {
                switch (force.Kind)
                {
                    case VitalForceKind.RateMultiplier:
                        multiplier *= Math.Max(0d, force.Amount);
                        break;
                    case VitalForceKind.Bias:
                        bias += force.Amount;
                        break;
                    case VitalForceKind.Delta:
                        deltaSum += force.Amount;
                        break;
                }
            }

            // clamp(k * dt, 0, MAX_STEP): the multiplicative part can never overshoot the target.
            var stepFraction = elapsedHours <= 0
                ? 0d
                : Math.Clamp(vital.Decay.RatePerHour * elapsedHours * multiplier, 0d, Math.Max(0d, options.MaxStep));

            var target = vital.Decay.Target.Resolve(to);
            var decay = (target - current) * stepFraction;
            var push = bias * elapsedHours + deltaSum;

            // Second clamp: the summed change of one tick, as a fraction of the range.
            var maxChange = Math.Max(0d, options.MaxStepPerTickFraction) * vital.Range.Span;
            var delta = Math.Clamp(decay + push, -maxChange, maxChange);

            next[vital.Id] = VitalFixedPoint.Quantize(vital.Range.Clamp(current + delta));
        }

        return new VitalAdvanceResult(next);
    }

    /// <summary>
    /// Elapsed hours with the offline clamp. A clock that moved backwards yields zero elapsed time
    /// instead of a negative (and therefore inverted) decay.
    /// </summary>
    public static double ElapsedHours(DateTimeOffset from, DateTimeOffset to, VitalAdvanceOptions? options = null)
    {
        options ??= new VitalAdvanceOptions();
        if (to <= from)
            return 0d;

        var hours = (to - from).TotalHours;
        if (double.IsNaN(hours) || double.IsInfinity(hours))
            return 0d;

        return Math.Clamp(hours, 0d, Math.Max(0d, options.MaxDeltaHours));
    }

    /// <summary>Groups by vital and sorts into the canonical fold order.</summary>
    private static Dictionary<string, List<VitalForce>> Collect(IEnumerable<VitalForce>? forces)
    {
        var map = new Dictionary<string, List<VitalForce>>(StringComparer.Ordinal);
        if (forces is null)
            return map;

        foreach (var force in forces)
        {
            if (string.IsNullOrWhiteSpace(force.VitalId) || double.IsNaN(force.Amount))
                continue;

            if (!map.TryGetValue(force.VitalId, out var list))
                map[force.VitalId] = list = [];

            list.Add(force);
        }

        foreach (var list in map.Values)
            list.Sort(Compare);

        return map;
    }

    private static int Compare(VitalForce left, VitalForce right)
    {
        var byPriority = left.SourcePriority.CompareTo(right.SourcePriority);
        if (byPriority != 0)
            return byPriority;

        var byKind = left.Kind.CompareTo(right.Kind);
        if (byKind != 0)
            return byKind;

        var byAmount = left.Amount.CompareTo(right.Amount);
        return byAmount != 0 ? byAmount : string.CompareOrdinal(left.VitalId, right.VitalId);
    }
}
