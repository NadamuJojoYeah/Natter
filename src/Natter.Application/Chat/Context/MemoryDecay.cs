namespace Natter.Application.Chat;

/// <summary>
/// One decay parameter set: the FSRS-6 decay exponent <c>d</c> and the initial stability <c>S0</c>
/// in days (docs/P6_MEMORY_SYSTEM.md §5.2).
/// </summary>
public readonly record struct MemoryDecayProfile(double DecayD, double InitialStabilityDays);

/// <summary>
/// Forgetting curve of the layered memory system (docs/P6_MEMORY_SYSTEM.md §5.1, §5.2, §6.1).
/// Pure maths: static, stateless, no IO, no DI, no clock — the caller supplies the age.
///
/// <para>
/// The curve is a <b>power law</b>, not an exponential:
/// </para>
/// <code>
/// factor   = 0.9^(-1/d) - 1
/// R(t, S)  = (1 + factor * t / S)^(-d)
/// SInc(S)  = 1 + alpha * S^(-Damping)
/// </code>
///
/// <para>
/// <b>Why not <c>exp(-t/S)</c>:</b> the exponential is wrong for memory. At ten times the stability
/// the exponential is already at 0.00005 (forgotten) while the power law still holds 0.693 — using
/// the exponential archives memories far too early. <see cref="MemoryDecayTests"/> freezes that
/// decision: it asserts the power-law value <em>and</em> that the exponential is below 0.001, so a
/// future "simplification" back to the exponential turns the suite red.
/// </para>
///
/// <para>
/// <b>Numeric type:</b> <see cref="double"/>, because the curve needs <c>pow</c>; a fixed-point type
/// cannot do fractional exponents. This is safe because <b><c>R</c> never enters
/// <c>PromptPlan.Hash</c></b> — the hash covers the selected entries and their content hashes, not
/// the retention value. Cross-platform bit-level equality is therefore not required, and callers must
/// compare with a relative tolerance (tests use 1e-9). Never use <see cref="decimal"/> here: it has
/// no <c>Pow</c> and would force a re-derivation of the whole curve.
/// </para>
///
/// <para>
/// <b>Total function:</b> the parameters arrive from user JSON, so out-of-range input must not throw.
/// <c>d</c> is clamped into <see cref="MinDecay"/>..<see cref="MaxDecay"/>, and an unusable stability
/// falls back to <see cref="MinStabilityDays"/> (see <see cref="Retention"/> for the rationale).
/// </para>
/// </summary>
public static class MemoryDecay
{
    /// <summary>Growth coefficient of <see cref="StabilityGrowth"/>. Our value, between Pimsleur's
    /// 5.28× and "no change"; marked for calibration (§6.1).</summary>
    public const double DefaultAlpha = 0.9;

    /// <summary>FSRS-6 <c>w19</c>: the damping exponent that makes growth slower for stable memories.</summary>
    public const double Damping = 0.0658;

    /// <summary>The curve is calibrated so that one stability interval leaves this retention (§5.1).</summary>
    public const double TargetRetention = 0.9;

    /// <summary>FSRS-6 lower bound of the decay exponent.</summary>
    public const double MinDecay = 0.1;

    /// <summary>Upper bound of the decay exponent (FSRS-6 range end, used by <see cref="MemoryType.Trace"/>).</summary>
    public const double MaxDecay = 0.8;

    /// <summary>FSRS-6 fitted default, used by Policy / Fact / Preference.</summary>
    public const double DefaultDecay = 0.1542;

    /// <summary>
    /// Stability floor used for unusable input. A memory whose stability is missing or corrupt is
    /// treated as one day old rather than as forgotten: §5.3 forbids deleting memories, and a corrupt
    /// field must never silently look like "never worth injecting" — that would erase the entry's
    /// chance to be reinforced by a later recall.
    /// </summary>
    public const double MinStabilityDays = 1d;

    /// <summary>Profile of one memory type (§5.2). The type decides how fast it fades.</summary>
    public static MemoryDecayProfile ProfileFor(MemoryType type)
        => type switch
        {
            // Policy lives longest and fades like a fact: it is a standing instruction.
            MemoryType.Policy => new MemoryDecayProfile(DefaultDecay, 30d),
            MemoryType.Fact => new MemoryDecayProfile(DefaultDecay, 20d),
            MemoryType.Preference => new MemoryDecayProfile(DefaultDecay, 14d),
            // Events do fade; raw turns fade fastest and are never actively injected (§8.1).
            MemoryType.Episodic => new MemoryDecayProfile(0.45, 5d),
            MemoryType.Trace => new MemoryDecayProfile(0.8, 2.3d),
            _ => new MemoryDecayProfile(DefaultDecay, 20d),
        };

    /// <summary>Initial stability in days for one memory type (§5.2).</summary>
    public static double InitialStabilityDays(MemoryType type) => ProfileFor(type).InitialStabilityDays;

    /// <summary>Clamps a decay exponent into the supported range; NaN yields <see cref="DefaultDecay"/>.</summary>
    public static double ClampDecay(double decayD)
    {
        if (double.IsNaN(decayD))
            return DefaultDecay;

        return Math.Clamp(decayD, MinDecay, MaxDecay);
    }

    /// <summary>
    /// <c>factor = 0.9^(-1/d) - 1</c>: the normalisation that makes <see cref="Retention"/> return
    /// exactly <see cref="TargetRetention"/> after one stability interval, for any <c>d</c>.
    /// This is how FSRS defines its curve; it is not a free parameter.
    /// </summary>
    public static double Factor(double decayD)
    {
        var d = ClampDecay(decayD);

        // The clamp keeps d >= MinDecay, so 0.9^(-1/d) stays in ~[1.14, 2.87] and cannot overflow.
        return Math.Pow(TargetRetention, -1d / d) - 1d;
    }

    /// <summary>
    /// Retention of a memory <paramref name="ageDays"/> after its last reinforcement, given its
    /// stability <paramref name="stabilityDays"/> and the decay exponent.
    /// Returns a value in [0, 1]; never throws and never returns NaN or infinity.
    /// </summary>
    /// <param name="ageDays">Elapsed days. Negative → 0 (just reinforced); +infinity → ~0 retention.</param>
    /// <param name="stabilityDays">
    /// Stability in days. Non-finite or <c>&lt;= 0</c> is treated as <see cref="MinStabilityDays"/>:
    /// a corrupt stability must not read as "forgotten", because archiving is capacity driven and
    /// §5.3 forbids dropping memories on a time threshold.
    /// </param>
    /// <param name="decayD">Decay exponent, clamped into [<see cref="MinDecay"/>, <see cref="MaxDecay"/>].</param>
    public static double Retention(double ageDays, double stabilityDays, double decayD)
    {
        var d = ClampDecay(decayD);
        var stability = UsableStability(stabilityDays);
        var age = UsableAge(ageDays);

        if (age <= 0d)
            return 1d;

        var ratio = Factor(d) * age / stability;

        // Overflow guard: for enormous ages the base may saturate to +infinity. R then tends to 0,
        // which is the mathematically correct limit; returning it explicitly avoids Inf/NaN leaks.
        if (double.IsPositiveInfinity(ratio))
            return 0d;

        var retention = Math.Pow(1d + ratio, -d);
        if (double.IsNaN(retention))
            return 0d;

        return Math.Clamp(retention, 0d, 1d);
    }

    /// <summary>
    /// <b>Legacy exponential decay: <c>exp(-age / S)</c>.</b> This deliberately contradicts the
    /// §5.1 conclusion above (the power law is the correct shape for memory).
    ///
    /// <para>
    /// <b>Why it exists:</b> the v1 contract — <c>IMemoryStore</c> / <c>memory_items</c> — is still
    /// the <em>live</em> recall path. Switching it to the power law now would be an unmeasured
    /// behaviour change dressed up as a bug fix (the same mistake the <c>trigram</c> tokenizer made:
    /// tests stay green while the behaviour silently gets worse). For a 30-day interval the change is
    /// 0.368 → 0.900, i.e. "what you said a month ago" would weigh almost the same as "what you said
    /// today". Until <c>natter memory eval</c> has data, the live path keeps its old curve.
    /// </para>
    ///
    /// <para>
    /// It lives in this file on purpose so there is still exactly <b>one</b> implementation of decay
    /// per mode, both visible side by side, rather than two independent copies that nobody can compare.
    /// </para>
    ///
    /// <para>
    /// <b>Delete this method when v1 retires (P6.7)</b>, together with
    /// <c>MemoryContractTests.V1_and_v2_decay_differently_during_the_transition</c>.
    /// </para>
    /// </summary>
    /// <param name="ageDays">Elapsed days; negative or NaN counts as 0 (full retention).</param>
    /// <param name="stabilityDays">Exponential time constant ("half-life" in v1 terms).</param>
    public static double LegacyRetention(double ageDays, double stabilityDays)
    {
        var stability = UsableStability(stabilityDays);
        var age = UsableAge(ageDays);

        if (age <= 0d)
            return 1d;

        var retention = Math.Exp(-age / stability);
        return double.IsNaN(retention) ? 0d : Math.Clamp(retention, 0d, 1d);
    }

    /// <summary>
    /// One reinforcement step: <c>SInc = 1 + alpha * S^(-Damping)</c>, returned as a
    /// <b>multiplier</b> for the stability (<c>S_next = S * StabilityGrowth(S)</c>).
    ///
    /// <para>
    /// <b>Deliberate deviation from FSRS-6.</b> The literature's same-day formula is
    /// <c>e^(w17*(G-3+w18)) * S^(-w19)</c>, which for large <c>S</c> falls below 1 — i.e. "saying it
    /// again makes the memory fainter". That semantics is unacceptable here, so we keep only the part
    /// we want from it: the damping <c>S^(-Damping)</c> ("the more stable, the harder to grow") and a
    /// positive coefficient. The result is always greater than 1 and decreases monotonically with S.
    /// Do not "fix" this back to the FSRS-6 product form.
    /// </para>
    /// </summary>
    /// <param name="stabilityDays">Current stability; non-finite or non-positive uses the floor.</param>
    /// <param name="alpha">Growth coefficient; non-finite or non-positive falls back to <see cref="DefaultAlpha"/>.</param>
    public static double StabilityGrowth(double stabilityDays, double alpha = DefaultAlpha)
    {
        var stability = UsableStability(stabilityDays);
        var coefficient = double.IsNaN(alpha) || double.IsInfinity(alpha) || alpha <= 0d
            ? DefaultAlpha
            : alpha;

        var growth = 1d + coefficient * Math.Pow(stability, -Damping);
        return double.IsNaN(growth) || double.IsInfinity(growth) ? 1d : growth;
    }

    /// <summary>Applies <paramref name="repeats"/> reinforcement steps (§6.1).</summary>
    public static double Reinforce(double stabilityDays, int repeats, double alpha = DefaultAlpha)
    {
        var stability = UsableStability(stabilityDays);
        for (var index = 0; index < repeats; index++)
            stability *= StabilityGrowth(stability, alpha);

        return double.IsNaN(stability) || double.IsInfinity(stability) ? UsableStability(stabilityDays) : stability;
    }

    /// <summary>Stability that is guaranteed finite and strictly positive.</summary>
    private static double UsableStability(double stabilityDays)
        => double.IsNaN(stabilityDays) || double.IsInfinity(stabilityDays) || stabilityDays <= 0d
            ? MinStabilityDays
            : stabilityDays;

    /// <summary>Age that is guaranteed non-negative; NaN counts as "just now".</summary>
    private static double UsableAge(double ageDays)
    {
        if (double.IsNaN(ageDays))
            return 0d;

        return ageDays < 0d ? 0d : ageDays;
    }
}
