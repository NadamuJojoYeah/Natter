using System.Security.Cryptography;
using System.Text;

namespace Natter.Plugin.StateVitals.Kernel;

/// <summary>
/// Seeded randomness (design §12.1). A bare <c>Random.Shared</c> would break reproducibility: the
/// same conversation could not be replayed, a platform redelivery could roll a second outcome, and
/// no probabilistic pack could be tested. The seed is derived from
/// <c>(packId, triggerEventId, sequence)</c> so replay is exact.
/// </summary>
public static class TriggerRandom
{
    /// <summary>Deterministic seed; SHA-256 keeps it stable across processes, platforms and runs.</summary>
    public static ulong Seed(string packId, string triggerEventId, long sequence)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(triggerEventId);

        var payload = $"{packId}\n{triggerEventId}\n{sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return BitConverter.ToUInt64(hash, 0);
    }

    /// <summary>Uniform value in <c>[0, 1)</c> for one (pack, event, sequence) triple.</summary>
    public static double UnitInterval(string packId, string triggerEventId, long sequence)
    {
        var state = Seed(packId, triggerEventId, sequence) | 1UL;

        // xorshift64*: deterministic, no allocation, good enough for content dice.
        state ^= state >> 12;
        state ^= state << 25;
        state ^= state >> 27;
        var result = state * 0x2545F4914F6CDD1DUL;

        // 53 significant bits → exactly representable in [0, 1).
        return (result >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Rolls one trigger: true when the deterministic value falls below the probability.</summary>
    public static bool Roll(string packId, string triggerEventId, long sequence, double probability)
        => probability >= 1d || (probability > 0d && UnitInterval(packId, triggerEventId, sequence) < probability);
}
