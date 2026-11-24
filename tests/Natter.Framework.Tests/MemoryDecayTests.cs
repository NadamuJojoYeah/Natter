using Natter.Application.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Locks the memory forgetting curve (docs/P6_MEMORY_SYSTEM.md §5.1, §5.2, §6.1).
///
/// <para>
/// These tests are the guard rails for three decisions that are easy to undo by accident:
/// the curve is a <b>power law</b> and not an exponential, <c>R(S,S) == 0.9</c> holds by
/// construction for every <c>d</c>, and stability growth is a <b>deliberate simplification</b> of
/// FSRS-6 that is always &gt; 1. Turning any of them back into the "obvious" version fails here.
/// </para>
/// </summary>
public sealed class MemoryDecayTests
{
    /// <summary>Relative tolerance for double comparisons (the task's contract; no bit-level equality).</summary>
    private const double Tolerance = 1e-9;

    // ------------------------------------------------------------------ per-kind parameters (§5.2)

    [Fact]
    public void Each_memory_type_has_the_documented_decay_profile()
    {
        Assert.Equal(new MemoryDecayProfile(0.1542, 30d), MemoryDecay.ProfileFor(MemoryType.Policy));
        Assert.Equal(new MemoryDecayProfile(0.1542, 20d), MemoryDecay.ProfileFor(MemoryType.Fact));
        Assert.Equal(new MemoryDecayProfile(0.1542, 14d), MemoryDecay.ProfileFor(MemoryType.Preference));
        Assert.Equal(new MemoryDecayProfile(0.45, 5d), MemoryDecay.ProfileFor(MemoryType.Episodic));
        Assert.Equal(new MemoryDecayProfile(0.8, 2.3d), MemoryDecay.ProfileFor(MemoryType.Trace));

        // Every profile must stay inside the FSRS-6 supported range, or the curve is not defined.
        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var profile = MemoryDecay.ProfileFor(type);
            Assert.InRange(profile.DecayD, MemoryDecay.MinDecay, MemoryDecay.MaxDecay);
            Assert.True(profile.InitialStabilityDays > 0);
        }

        Assert.Equal(30d, MemoryDecay.InitialStabilityDays(MemoryType.Policy));
        Assert.Equal(2.3d, MemoryDecay.InitialStabilityDays(MemoryType.Trace));
    }

    // ------------------------------------------------------------------ definition property (§5.1)

    [Fact]
    public void Retention_after_one_stability_interval_is_always_target_retention()
    {
        // This is the definition of the curve (factor exists for exactly this reason), not a
        // coincidence: it must hold for every decay exponent in the supported range.
        double[] decays = [0.1, 0.1542, 0.3, 0.45, 0.6, 0.8];
        var maxDeviation = 0d;

        foreach (var decay in decays)
        {
            // t = S, so the ratio t/S is exactly 1.
            var actual = MemoryDecay.Retention(1d, 1d, decay);
            maxDeviation = Math.Max(maxDeviation, Math.Abs(actual - MemoryDecay.TargetRetention));
            Assert.True(
                Math.Abs(actual - MemoryDecay.TargetRetention) <= Tolerance,
                $"R(S,S) for d={decay} was {actual:R}, expected {MemoryDecay.TargetRetention}");

            // The same property must hold at other absolute scales (S is a divisor, not a constant).
            foreach (var stability in new[] { 0.5d, 2.3d, 14d, 90d, 1000d })
            {
                var atScale = MemoryDecay.Retention(stability, stability, decay);
                maxDeviation = Math.Max(maxDeviation, Math.Abs(atScale - MemoryDecay.TargetRetention));
                Assert.True(
                    Math.Abs(atScale - MemoryDecay.TargetRetention) <= Tolerance,
                    $"R(S,S) for d={decay} S={stability} was {atScale:R}");
            }
        }

        // Measured, not assumed: the identity is exact in real arithmetic
        // (R(S,S) = (0.9^(-1/d))^(-d) = 0.9), and in double it round-trips far below the contract
        // tolerance. Tightening this to 1e-15 is what turns "should be exact" into evidence.
        Assert.True(maxDeviation <= 1e-15, $"max |R(S,S) - 0.9| = {maxDeviation:E3}");
    }

    [Fact]
    public void Retention_at_zero_age_is_exactly_one()
    {
        foreach (var decay in new[] { 0.1, 0.1542, 0.45, 0.8 })
        {
            foreach (var stability in new[] { 2.3d, 14d, 90d })
                Assert.Equal(1d, MemoryDecay.Retention(0d, stability, decay), 12);
        }
    }

    [Fact]
    public void Factor_normalizes_the_curve_and_follows_the_documented_formula()
    {
        foreach (var decay in new[] { 0.1, 0.1542, 0.3, 0.45, 0.6, 0.8 })
        {
            var expected = Math.Pow(MemoryDecay.TargetRetention, -1d / decay) - 1d;
            AssertRelative(expected, MemoryDecay.Factor(decay));
        }

        // d = 0.1542 → factor ≈ 0.9805, which is why R(10S) is still ~0.69.
        Assert.InRange(MemoryDecay.Factor(0.1542), 0.98, 0.981);

        // The clamp bounds the factor into [~0.14, ~1.87], so the normalisation can never overflow.
        Assert.InRange(MemoryDecay.Factor(MemoryDecay.MinDecay), 1.86, 1.88);
        Assert.InRange(MemoryDecay.Factor(MemoryDecay.MaxDecay), 0.14, 0.15);

        // And the identity really is what makes R(S,S) = 0.9.
        var factor = MemoryDecay.Factor(0.1542);
        AssertRelative(0.9, Math.Pow(1d + factor, -0.1542));
    }

    // ------------------------------------------------------------------ power law, not exponential

    [Fact]
    public void The_curve_is_a_power_law_and_not_an_exponential()
    {
        // §5.1: at ten stability intervals the power law still holds ~69% while the exponential has
        // already forgotten everything. This assertion freezes the decision: replacing the curve with
        // exp(-t/S) makes it fail.
        const double decay = 0.1542;
        const double stability = 14d;

        var powerLaw = MemoryDecay.Retention(10d * stability, stability, decay);
        var exponential = Math.Exp(-10d);

        Assert.InRange(powerLaw, 0.68, 0.71);
        Assert.True(exponential < 0.001, $"exp(-10) was {exponential:R}");
        Assert.True(powerLaw > exponential * 1000d);

        // Documented reference points from §5.1's comparison table.
        Assert.InRange(MemoryDecay.Retention(100d * stability, stability, decay), 0.48, 0.50);
        Assert.InRange(MemoryDecay.Retention(1000d * stability, stability, decay), 0.33, 0.36);

        // The exponential would have collapsed long before that.
        Assert.True(MemoryDecay.Retention(10d * stability, stability, decay) > Math.Exp(-10d) * 10000d);
    }

    [Fact]
    public void Per_type_retention_at_ten_stability_intervals_matches_the_design_table()
    {
        // §5.2 table: Policy/Fact/Preference 0.693, Episodic 0.559, Trace 0.495.
        foreach (var type in new[] { MemoryType.Policy, MemoryType.Fact, MemoryType.Preference })
        {
            var profile = MemoryDecay.ProfileFor(type);
            Assert.InRange(
                MemoryDecay.Retention(10d * profile.InitialStabilityDays, profile.InitialStabilityDays, profile.DecayD),
                0.69,
                0.696);
        }

        var episodic = MemoryDecay.ProfileFor(MemoryType.Episodic);
        Assert.InRange(
            MemoryDecay.Retention(10d * episodic.InitialStabilityDays, episodic.InitialStabilityDays, episodic.DecayD),
            0.55,
            0.57);

        var trace = MemoryDecay.ProfileFor(MemoryType.Trace);
        Assert.InRange(
            MemoryDecay.Retention(10d * trace.InitialStabilityDays, trace.InitialStabilityDays, trace.DecayD),
            0.49,
            0.50);
    }

    // ------------------------------------------------------------------ monotonicity

    [Fact]
    public void Retention_strictly_decreases_with_age()
    {
        const double stability = 14d;
        const double decay = 0.1542;

        var previous = double.PositiveInfinity;
        for (var step = 0; step <= 100; step++)
        {
            var age = step * (200d / 100d);
            var current = MemoryDecay.Retention(age, stability, decay);

            Assert.True(current <= previous + Tolerance, $"retention increased at age={age}");
            Assert.True(current < previous || step == 0, $"retention was flat at age={age}");
            previous = current;
        }
    }

    [Fact]
    public void Retention_strictly_increases_with_stability()
    {
        const double age = 30d;
        const double decay = 0.1542;

        var previous = -1d;
        for (var step = 0; step <= 100; step++)
        {
            var stability = 0.5d + step * (200d / 100d);
            var current = MemoryDecay.Retention(age, stability, decay);

            Assert.True(current > previous, $"retention did not increase at stability={stability}");
            previous = current;
        }
    }

    [Fact]
    public void A_steeper_decay_exponent_fades_faster()
    {
        // Trace (0.8) must sit below Policy (0.1542) at the same age/stability: that is what makes
        // "the raw turn drops out of the way" true.
        const double age = 20d;
        const double stability = 5d;

        Assert.True(
            MemoryDecay.Retention(age, stability, 0.8) < MemoryDecay.Retention(age, stability, 0.1542));
    }

    // ------------------------------------------------------------------ stability growth (§6.1)

    [Fact]
    public void Stability_growth_is_always_above_one_and_damped()
    {
        // Deliberate deviation from FSRS-6: growth never drops below 1 (the literature's same-day
        // formula does for large S, which would mean "saying it again makes it fainter").
        foreach (var stability in new[] { 1d, 2.3d, 14d, 90d, 1000d })
        {
            var growth = MemoryDecay.StabilityGrowth(stability);
            Assert.True(growth > 1d, $"SInc({stability}) was {growth:R}");
            AssertRelative(1d + MemoryDecay.DefaultAlpha * Math.Pow(stability, -MemoryDecay.Damping), growth);
        }

        // Monotone decreasing towards 1: the more stable, the harder to grow.
        var previous = double.PositiveInfinity;
        foreach (var stability in new[] { 1d, 2.3d, 14d, 90d, 1000d })
        {
            var growth = MemoryDecay.StabilityGrowth(stability);
            Assert.True(growth < previous, $"SInc did not decrease at S={stability}");
            previous = growth;
        }

        // Documented values from §6.1.
        Assert.InRange(MemoryDecay.StabilityGrowth(2.3d), 1.85, 1.853);
        Assert.InRange(MemoryDecay.StabilityGrowth(14d), 1.75, 1.76);
        Assert.InRange(MemoryDecay.StabilityGrowth(90d), 1.66, 1.67);
        Assert.True(MemoryDecay.StabilityGrowth(1_000_000d) > 1d);
    }

    [Fact]
    public void Five_cross_session_repeats_grow_a_preference_from_fourteen_days()
    {
        // §6.1 walks "从 S=14 起，跨会话重申 5 次" through the growth formula.
        //
        // MEASURED (this implementation, faithful to the formula): 201.4 days, i.e. 14.4x.
        // The doc's prose says "≈2,300 天 ≈ 6.3 年", but its own intermediate step
        // (14 × 1.75^5 = 229.8) is ten times smaller than that, so the "2,300" is a 10x arithmetic
        // slip in the document, not a property of the formula. Reported to the lead; the assertion
        // below encodes what the formula actually produces, so a future change to the curve shows up
        // here instead of being hidden by a wrong expected value.
        const double start = 14d;

        var final = MemoryDecay.Reinforce(start, repeats: 5);

        Assert.InRange(final, 190, 215);
        Assert.InRange(final / start, 14d, 15d);

        // Step by step, for the record (§6.1's own numbers roughly match these).
        var stability = start;
        var steps = new List<double>();
        for (var index = 0; index < 5; index++)
        {
            stability *= MemoryDecay.StabilityGrowth(stability);
            steps.Add(stability);
        }

        Assert.Equal(final, stability, 6);
        Assert.InRange(steps[0], 24d, 25d);
        Assert.InRange(steps[4], 200d, 203d);

        // The doc's simplified hand-calculation (constant 1.75 per step) lands close to the real one.
        Assert.InRange(start * Math.Pow(1.75, 5), 225d, 235d);
    }

    // ------------------------------------------------------------------ boundary behaviour

    [Fact]
    public void A_decay_exponent_outside_the_supported_range_is_clamped_not_rejected()
    {
        // The value comes from user JSON: a silly number must not crash the host.
        Assert.Equal(MemoryDecay.MinDecay, MemoryDecay.ClampDecay(-5d));
        Assert.Equal(MemoryDecay.MinDecay, MemoryDecay.ClampDecay(0d));
        Assert.Equal(MemoryDecay.MinDecay, MemoryDecay.ClampDecay(double.NegativeInfinity));
        Assert.Equal(MemoryDecay.MaxDecay, MemoryDecay.ClampDecay(5d));
        Assert.Equal(MemoryDecay.MaxDecay, MemoryDecay.ClampDecay(double.PositiveInfinity));
        Assert.Equal(MemoryDecay.DefaultDecay, MemoryDecay.ClampDecay(double.NaN));
        Assert.Equal(0.3d, MemoryDecay.ClampDecay(0.3d));

        // Clamping is what the curve actually uses, so Retention stays defined at the extremes.
        MemoryDecayAssertions.Finite(MemoryDecay.Retention(10d, 5d, 99d));
        MemoryDecayAssertions.Finite(MemoryDecay.Retention(10d, 5d, -99d));
        MemoryDecayAssertions.Finite(MemoryDecay.Retention(10d, 5d, double.NaN));
        Assert.Equal(
            MemoryDecay.Retention(10d, 5d, MemoryDecay.MaxDecay),
            MemoryDecay.Retention(10d, 5d, 99d));
    }

    [Fact]
    public void An_unusable_stability_falls_back_to_the_one_day_floor()
    {
        // Choice: a missing/corrupt stability is treated as one day, not as "forgotten".
        // Rationale: archiving is capacity driven (§5.3) and never time or score driven, so a corrupt
        // field must not silently make an entry un-injectable and un-reinforceable. One day keeps it
        // retrievable and lets a later recall pull it back up.
        double[] invalid = [0d, -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        var floor = MemoryDecay.Retention(7d, MemoryDecay.MinStabilityDays, 0.1542);

        foreach (var stability in invalid)
        {
            var actual = MemoryDecay.Retention(7d, stability, 0.1542);
            MemoryDecayAssertions.Finite(actual);
            Assert.Equal(floor, actual, 12);
        }

        // The floor is a real, if short, lifetime — not zero retention.
        Assert.InRange(floor, 0.1, 0.95);
        Assert.True(floor > 0d);
    }

    [Fact]
    public void A_negative_or_nan_age_counts_as_just_reinforced()
    {
        foreach (var age in new[] { -1d, -1e9d, double.NaN })
            Assert.Equal(1d, MemoryDecay.Retention(age, 14d, 0.1542), 12);
    }

    [Fact]
    public void An_enormous_age_stays_finite()
    {
        foreach (var age in new[] { 1e9d, 1e12d, double.PositiveInfinity, double.MaxValue })
        {
            var retention = MemoryDecay.Retention(age, 30d, 0.1542);
            MemoryDecayAssertions.Finite(retention);
            Assert.InRange(retention, 0d, 1d);
        }

        // 1e9 days at S=30 is ~0.069: never exactly zero, which is why time-based archiving cannot
        // work (§5.3) and capacity is the only real trigger.
        Assert.InRange(MemoryDecay.Retention(1e9d, 30d, 0.1542), 0.06, 0.08);
        Assert.True(MemoryDecay.Retention(1e9d, 30d, 0.1542) > 0d);
    }

    [Fact]
    public void Retention_stays_a_probability_for_hostile_input()
    {
        // A small grid of nasty combinations: every result must be a usable probability.
        double[] ages = [-1d, 0d, 1e-9d, 1d, 1e6d, 1e12d, double.NaN, double.PositiveInfinity];
        double[] stabilities = [-1d, 0d, 1e-6d, 1d, 14d, 1e9d, double.NaN, double.PositiveInfinity];
        double[] decays = [-1d, 0d, 0.1d, 0.1542d, 0.8d, 1d, double.NaN, double.PositiveInfinity];

        foreach (var age in ages)
        {
            foreach (var stability in stabilities)
            {
                foreach (var decay in decays)
                {
                    var retention = MemoryDecay.Retention(age, stability, decay);
                    MemoryDecayAssertions.Finite(retention);
                    Assert.InRange(retention, 0d, 1d);
                }
            }
        }
    }

    [Fact]
    public void Stability_growth_survives_hostile_input()
    {
        foreach (var stability in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, 1e300d })
        {
            var growth = MemoryDecay.StabilityGrowth(stability);
            MemoryDecayAssertions.Finite(growth);
            Assert.True(growth >= 1d);
        }

        // A non-positive or non-finite alpha falls back to the documented default.
        foreach (var alpha in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            AssertRelative(MemoryDecay.StabilityGrowth(14d), MemoryDecay.StabilityGrowth(14d, alpha));

        // Zero repeats is the identity; a huge repeat count stays finite.
        Assert.Equal(14d, MemoryDecay.Reinforce(14d, repeats: 0), 9);
        MemoryDecayAssertions.Finite(MemoryDecay.Reinforce(14d, repeats: 10_000));
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertRelative(double expected, double actual, double tolerance = Tolerance)
    {
        var scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
        var difference = Math.Abs(expected - actual);
        Assert.True(
            scale == 0d ? difference == 0d : difference / scale <= tolerance,
            $"expected {expected:R}, actual {actual:R} (relative difference {(scale == 0d ? 0d : difference / scale):E3})");
    }
}

internal static class MemoryDecayAssertions
{
    /// <summary>Asserts the value is neither NaN nor infinite.</summary>
    public static void Finite(double value)
        => Assert.True(!double.IsNaN(value) && !double.IsInfinity(value), $"value was {value:R}");
}
