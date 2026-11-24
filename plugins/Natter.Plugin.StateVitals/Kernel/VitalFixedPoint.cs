namespace Natter.Plugin.StateVitals.Kernel;

/// <summary>
/// Fixed-point persistence (design §2 早定格 7 / §12.2). Values are persisted as 1/1000 scaled
/// integers because the last bit of a <see cref="double"/> differs between x64 and ARM, which makes
/// "exactly on the threshold" decisions and CI assertions jitter. Memory stays <c>double</c>; every
/// value that leaves the kernel is quantized, so a decision taken once is stable forever.
/// </summary>
public static class VitalFixedPoint
{
    public const long Scale = 1000;

    /// <summary>Rounds half away from zero so negative and positive ranges behave symmetrically.</summary>
    public static long ToFixed(double value)
    {
        if (double.IsNaN(value))
            return 0;

        var scaled = value * Scale;
        if (scaled >= long.MaxValue)
            return long.MaxValue;

        if (scaled <= long.MinValue)
            return long.MinValue;

        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    public static double FromFixed(long value) => value / (double)Scale;

    /// <summary>Quantizes to the persisted resolution; <c>Quantize(Quantize(x)) == Quantize(x)</c>.</summary>
    public static double Quantize(double value) => FromFixed(ToFixed(value));
}
