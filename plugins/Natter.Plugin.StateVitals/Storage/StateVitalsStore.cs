using Natter.Plugin.StateVitals.Kernel;

namespace Natter.Plugin.StateVitals.Storage;

/// <summary>
/// A live asserted state instance (design §3.1): writable, persisted, with an optional expiry.
/// Derived states never appear here — they are a view and are not stored.
/// </summary>
public sealed record AssertedStateInstance(string StateId, DateTimeOffset Since, DateTimeOffset? ExpiresAt)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;
}

/// <summary>
/// The single global character state row (§2 早定格 1). <see cref="Version"/> is the
/// optimistic-concurrency token; <see cref="Values"/> is quantized to 1/1000 (§12.2).
/// </summary>
public sealed record StateSnapshot(
    string StateId,
    long Version,
    string DefinitionVersion,
    IReadOnlyDictionary<string, string> PackVersions,
    DateTimeOffset UpdatedAt,
    long NextTransitionSeq,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyList<AssertedStateInstance> AssertedStates);

/// <summary>One transition to persist, before the store assigns its sequence number.</summary>
public sealed record PendingTransition(
    string Kind,
    string DefinitionId,
    string? LevelId,
    string? PreviousLevelId,
    double Value);

/// <summary>A transition together with the sequence number the store allocated for it.</summary>
public sealed record SequencedTransition(long Seq, PendingTransition Transition);

/// <summary>Result of a successful compare-and-set commit.</summary>
public sealed record StateCommitResult(StateSnapshot Snapshot, IReadOnlyList<SequencedTransition> Transitions);

/// <summary>One attempt to move the snapshot forward from <see cref="Expected"/>.</summary>
public sealed record StateCommit(
    StateSnapshot Expected,
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyList<AssertedStateInstance> AssertedStates,
    string DefinitionVersion,
    IReadOnlyDictionary<string, string> PackVersions,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PendingTransition> Transitions);

/// <summary>
/// A description generated once for one state entry (design §6.1). Keyed by
/// <c>(stateId, transitionSeq)</c> so a replayed transition can never generate twice.
/// </summary>
public sealed record StoredDescription(
    string StateId,
    long TransitionSeq,
    string DefinitionId,
    string LevelId,
    string Text,
    string Source,
    string TextHash,
    DateTimeOffset CreatedAt);

/// <summary>Append-only change log row (design §8.2 / §11).</summary>
public sealed record StateEventRecord(
    long Seq,
    string Kind,
    string DefinitionId,
    string? LevelId,
    string? PreviousLevelId,
    double Value,
    DateTimeOffset At);

/// <summary>Persistence port of the plugin's own database. Never <c>natter.db</c> (design §8.1).</summary>
public interface IStateVitalsStore : IAsyncDisposable
{
    ValueTask<StateSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Versioned compare-and-set. Returns <c>null</c> when another writer won (the caller re-reads
    /// and recomputes, §7.2) and never overwrites a newer version.
    /// </summary>
    ValueTask<StateCommitResult?> TryCommitAsync(StateCommit commit, CancellationToken cancellationToken = default);

    ValueTask<StoredDescription?> GetDescriptionAsync(
        string stateId,
        long transitionSeq,
        CancellationToken cancellationToken = default);

    /// <summary>Latest description written for a still-active state.</summary>
    ValueTask<StoredDescription?> FindDescriptionAsync(
        string stateId,
        string definitionId,
        string levelId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts the first description for a transition; a duplicate keeps the first text.</summary>
    ValueTask<StoredDescription> SaveDescriptionAsync(
        StoredDescription description,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StateEventRecord>> ReadTransitionsAsync(
        string stateId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Text-hash helper shared by the store, the description service and the tests.</summary>
public static class StateVitalsHashing
{
    /// <summary>Stable short hash of the stored description text (design §2 早定格 5).</summary>
    public static string TextHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}

/// <summary>Quantization helpers at the storage boundary.</summary>
public static class StateVitalsQuantization
{
    public static long ToStored(double value) => VitalFixedPoint.ToFixed(value);

    public static double FromStored(long value) => VitalFixedPoint.FromFixed(value);
}
