namespace Natter.Application.Chat;

/// <summary>P6 retrieval budget (docs/P6_MEMORY_RAG.md §8).</summary>
public sealed class MemoryRetrievalOptions
{
    public static MemoryRetrievalOptions Default { get; } = new();

    /// <summary>Retrieval candidates considered before injection.</summary>
    public int MaxCandidates { get; init; } = 20;

    /// <summary>Maximum recalled entries injected into the prompt.</summary>
    public int MaxInjected { get; init; } = 5;

    /// <summary>Approximate token budget for injected memory text.</summary>
    public int MaxTokens { get; init; } = 800;

    /// <summary>Retrieval budget; on timeout the turn continues with empty memory.</summary>
    public TimeSpan RetrievalTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Stability window for time decay; older entries are down-weighted, never deleted.
    /// The reading depends on <see cref="Mode"/>: half-life (Legacy) or 0.9-retention interval (PowerLaw).
    /// </summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Forgetting curve used by the recall path. Defaults to <see cref="DecayMode.Legacy"/>, which is
    /// the shipped behaviour; switching it is a deliberate, observable configuration change.
    /// </summary>
    public DecayMode Mode { get; init; } = DecayMode.Legacy;

    public MemoryScopeType DefaultScope { get; init; } = MemoryScopeType.User;
}

/// <summary>Counters for memory retrieval degradation (no memory text is recorded).</summary>
public sealed class MemoryRetrievalMetrics
{
    private long _timeouts;
    private long _candidateTruncations;
    private long _injectionTruncations;
    private long _tokenTruncations;

    public long TimeoutCount => Interlocked.Read(ref _timeouts);
    public long CandidateTruncations => Interlocked.Read(ref _candidateTruncations);
    public long InjectionTruncations => Interlocked.Read(ref _injectionTruncations);
    public long TokenTruncations => Interlocked.Read(ref _tokenTruncations);

    public void Timeout() => Interlocked.Increment(ref _timeouts);
    public void CandidateTruncated() => Interlocked.Increment(ref _candidateTruncations);
    public void InjectionTruncated() => Interlocked.Increment(ref _injectionTruncations);
    public void TokenTruncated() => Interlocked.Increment(ref _tokenTruncations);
}

/// <summary>Rough token estimate used for the memory budget (CJK ~1.5 chars/token, Latin ~4).</summary>
public static class MemoryTokens
{
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var cjk = 0;
        var other = 0;
        foreach (var character in text)
        {
            if (character >= 0x2E80)
                cjk++;
            else
                other++;
        }

        return (int)Math.Ceiling(cjk / 1.5d + other / 4d);
    }
}
