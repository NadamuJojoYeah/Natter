namespace Natter.Plugin.StateVitals.Storage;

/// <summary>
/// In-memory twin of <see cref="SqliteStateVitalsStore"/> with identical compare-and-set semantics.
/// It exists so kernel, concurrency and description tests can run without touching disk; the SQLite
/// store stays the production path.
/// </summary>
public sealed class InMemoryStateVitalsStore : IStateVitalsStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, StoredDescription> _descriptions = [];
    private readonly List<StateEventRecord> _events = [];
    private StateSnapshot _snapshot;

    public InMemoryStateVitalsStore(
        string stateId,
        IReadOnlyDictionary<string, double> seedValues,
        string definitionVersion,
        DateTimeOffset seedTimestamp,
        IReadOnlyDictionary<string, string>? packVersions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateId);
        ArgumentNullException.ThrowIfNull(seedValues);

        _snapshot = new StateSnapshot(
            stateId,
            0,
            definitionVersion,
            packVersions ?? new Dictionary<string, string>(StringComparer.Ordinal),
            seedTimestamp,
            0,
            Copy(seedValues),
            []);
    }

    /// <summary>Number of accepted commits; proves the retry actually happened.</summary>
    public int CommitCount { get; private set; }

    /// <summary>Number of rejected (conflicting) commits; proves the CAS path is exercised.</summary>
    public int ConflictCount { get; private set; }

    public ValueTask<StateSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_snapshot);
    }

    public ValueTask<StateCommitResult?> TryCommitAsync(StateCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (_gate)
        {
            if (commit.Expected.Version != _snapshot.Version)
            {
                ConflictCount++;
                return ValueTask.FromResult<StateCommitResult?>(null);
            }

            var nextSeq = _snapshot.NextTransitionSeq;
            var sequenced = new List<SequencedTransition>(commit.Transitions.Count);
            foreach (var transition in commit.Transitions)
            {
                var entry = new SequencedTransition(nextSeq++, transition);
                sequenced.Add(entry);
                _events.Add(new StateEventRecord(
                    entry.Seq,
                    transition.Kind,
                    transition.DefinitionId,
                    transition.LevelId,
                    transition.PreviousLevelId,
                    transition.Value,
                    commit.UpdatedAt));
            }

            _snapshot = new StateSnapshot(
                commit.Expected.StateId,
                commit.Expected.Version + 1,
                commit.DefinitionVersion,
                commit.PackVersions,
                commit.UpdatedAt,
                nextSeq,
                Copy(commit.Values),
                commit.AssertedStates.ToArray());

            CommitCount++;
            return ValueTask.FromResult<StateCommitResult?>(new StateCommitResult(_snapshot, sequenced));
        }
    }

    public ValueTask<StoredDescription?> GetDescriptionAsync(
        string stateId,
        long transitionSeq,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(
                _descriptions.TryGetValue(transitionSeq, out var description) ? description : null);
        }
    }

    public ValueTask<StoredDescription?> FindDescriptionAsync(
        string stateId,
        string definitionId,
        string levelId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var match = _descriptions.Values
                .Where(description => string.Equals(description.DefinitionId, definitionId, StringComparison.Ordinal)
                                      && string.Equals(description.LevelId, levelId, StringComparison.Ordinal))
                .OrderByDescending(description => description.TransitionSeq)
                .FirstOrDefault();

            return ValueTask.FromResult(match);
        }
    }

    public ValueTask<StoredDescription> SaveDescriptionAsync(
        StoredDescription description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(description);

        lock (_gate)
        {
            if (_descriptions.TryGetValue(description.TransitionSeq, out var existing))
                return ValueTask.FromResult(existing);

            _descriptions[description.TransitionSeq] = description;
            return ValueTask.FromResult(description);
        }
    }

    public ValueTask<IReadOnlyList<StateEventRecord>> ReadTransitionsAsync(
        string stateId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<StateEventRecord> records = _events
                .OrderByDescending(record => record.Seq)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();

            return ValueTask.FromResult(records);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IReadOnlyDictionary<string, double> Copy(IReadOnlyDictionary<string, double> values)
    {
        var copy = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in values)
            copy[pair.Key] = pair.Value;

        return copy;
    }
}
