using Natter.Plugin.Abstractions;
using Natter.Plugin.StateVitals.Definitions;
using Natter.Plugin.StateVitals.Descriptions;
using Natter.Plugin.StateVitals.Kernel;
using Natter.Plugin.StateVitals.Rendering;
using Natter.Plugin.StateVitals.Storage;

namespace Natter.Plugin.StateVitals;

/// <summary>Outcome of one trigger evaluation (seeded, reproducible).</summary>
public sealed record TriggerOutcome(string TriggerId, string StateId, bool Fired, double Roll);

/// <summary>
/// Orchestrates the one global character state: read the snapshot, purge expired asserted states,
/// advance vitals from the effects of the active states, commit under optimistic concurrency, write
/// one description per state entry, and expose a bucketed view for rendering.
///
/// <para>
/// Dependency direction is enforced structurally: asserted states are the only writable state,
/// vitals are the only numbers, and derived states are recomputed on read and never stored
/// (design §3.1). All timing comes from the injected <see cref="TimeProvider"/> (§3.5).
/// </para>
/// </summary>
public sealed class StateVitalsRuntime : IAsyncDisposable
{
    private readonly ContentPackSet? _packs;
    private readonly IStateVitalsStore? _store;
    private readonly StateDescriptionService? _descriptions;
    private readonly VitalRuleRegistry? _rules;
    private readonly IPluginLog? _log;
    private readonly StateFragmentRenderer _renderer;
    private readonly SemaphoreSlim _advanceGate = new(1, 1);
    private readonly VitalAdvanceOptions _advanceOptions;

    private bool _disposed;

    /// <summary>Creates the disabled runtime: it owns nothing and contributes nothing.</summary>
    public static StateVitalsRuntime Disabled(StateVitalsOptions options, TimeProvider? clock = null, IPluginLog? log = null)
        => new(options, packs: null, store: null, descriptions: null, rules: null, log, clock ?? TimeProvider.System);

    /// <summary>Creates an enabled runtime over an already resolved pack set and store.</summary>
    public StateVitalsRuntime(
        StateVitalsOptions options,
        ContentPackSet packs,
        IStateVitalsStore store,
        StateDescriptionService descriptions,
        TimeProvider clock,
        VitalRuleRegistry? rules = null,
        IPluginLog? log = null,
        StateFragmentRenderer? renderer = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _descriptions = descriptions ?? throw new ArgumentNullException(nameof(descriptions));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _rules = rules;
        _log = log;
        _renderer = renderer ?? new StateFragmentRenderer();
        Enabled = true;
        _advanceOptions = new VitalAdvanceOptions(options.MaxStep, options.MaxDeltaHours, options.MaxStepPerTickFraction);
    }

    private StateVitalsRuntime(
        StateVitalsOptions options,
        ContentPackSet? packs,
        IStateVitalsStore? store,
        StateDescriptionService? descriptions,
        VitalRuleRegistry? rules,
        IPluginLog? log,
        TimeProvider clock)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _packs = packs;
        _store = store;
        _descriptions = descriptions;
        _rules = rules;
        _log = log;
        Clock = clock;
        _renderer = new StateFragmentRenderer();
        Enabled = false;
        _advanceOptions = new VitalAdvanceOptions(options.MaxStep, options.MaxDeltaHours, options.MaxStepPerTickFraction);
    }

    /// <summary>False when the plugin is not opted in; every operation then returns nothing.</summary>
    public bool Enabled { get; }

    public StateVitalsOptions Options { get; }

    public TimeProvider Clock { get; }

    public ContentPackSet? Packs => _packs;

    /// <summary>Description service of an enabled runtime, exposed for "generate once" assertions.</summary>
    public StateDescriptionService? Descriptions => _descriptions;

    /// <summary>Opens the database and materializes the first snapshot. No-op when disabled.</summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || _store is null)
            return;

        await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances the state by the configured elapsed time plus the supplied forces, committing under
    /// the version compare-and-set. A losing writer re-reads and recomputes, so no update is lost.
    /// </summary>
    public async ValueTask<VitalAdvanceResult> AdvanceAsync(
        IReadOnlyList<VitalForce>? forces = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled || _packs is null || _store is null)
            return new VitalAdvanceResult(new Dictionary<string, double>(StringComparer.Ordinal));

        await _advanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempts = Math.Max(1, Options.MaxAdvanceAttempts);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                var now = CurrentTime(snapshot);

                var activeAsserted = ActiveAsserted(snapshot, now);
                var derivedBefore = StateResolver.Derived(_packs, snapshot.Values);

                var collected = new List<VitalForce>();
                collected.AddRange(StateResolver.Forces(
                    _packs,
                    activeAsserted.Select(state => state.StateId).ToHashSet(StringComparer.Ordinal)));
                collected.AddRange(StateResolver.Forces(
                    _packs,
                    derivedBefore.Select(state => state.Definition.Id).ToHashSet(StringComparer.Ordinal)));

                if (_rules is not null && _packs.Rules.Count > 0)
                    collected.AddRange(await RuleForcesAsync(snapshot, now, cancellationToken).ConfigureAwait(false));

                if (forces is { Count: > 0 })
                    collected.AddRange(forces);

                var result = VitalKernel.Advance(
                    _packs.Vitals,
                    snapshot.Values,
                    snapshot.UpdatedAt,
                    now,
                    collected,
                    _advanceOptions);

                var derivedAfter = StateResolver.Derived(_packs, result.Values);
                var transitions = new List<PendingTransition>();
                foreach (var entered in StateResolver.Entered(derivedBefore, derivedAfter))
                {
                    transitions.Add(new PendingTransition(
                        "derived",
                        entered.Definition.Id,
                        entered.LevelId,
                        null,
                        result.Values.TryGetValue(entered.Definition.VitalId, out var value) ? value : 0));
                }

                var commit = new StateCommit(
                    snapshot,
                    result.Values,
                    activeAsserted,
                    _packs.DefinitionVersion,
                    _packs.PackVersions,
                    now,
                    transitions);

                var committed = await _store.TryCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                if (committed is null)
                {
                    // Another writer won: re-read, recompute, retry (design §7.2).
                    _log?.Debug(
                        "state advance version conflict state={0} expectedVersion={1} attempt={2}",
                        snapshot.StateId,
                        snapshot.Version,
                        attempt + 1);
                    continue;
                }

                await DescribeAsync(committed, cancellationToken).ConfigureAwait(false);
                return result;
            }

            throw new StateVitalsContentionException(
                $"State '{Options.StateId}' could not be advanced after {attempts} version conflicts.");
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    /// <summary>
    /// Pushes one vital by an absolute amount (one-shot delta, not scaled by elapsed time). Two
    /// concurrent pushes of +10 must end at +20: a writer that loses the compare-and-set re-reads and
    /// re-applies its own delta on top of the winner's value.
    /// </summary>
    public ValueTask<VitalAdvanceResult> PushAsync(
        string vitalId,
        double amount,
        int sourcePriority = 0,
        CancellationToken cancellationToken = default)
        => AdvanceAsync(
            [new VitalForce(vitalId, sourcePriority, VitalForceKind.Delta, amount)],
            cancellationToken);

    /// <summary>
    /// The only writable state path (design §3.1): asserts a state, optionally for a duration.
    /// Returns true when the state was not already active.
    /// </summary>
    public async ValueTask<bool> AssertAsync(
        string stateId,
        TimeSpan? duration = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enabled || _packs is null || _store is null)
            return false;

        var definition = _packs.FindAsserted(stateId)
                         ?? throw new StateVitalsStartupException(
                             StateVitalsCodes.TriggerStateNotAsserted,
                             $"'{stateId}' is not an asserted state.");

        await _advanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempts = Math.Max(1, Options.MaxAdvanceAttempts);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                var now = CurrentTime(snapshot);
                var active = ActiveAsserted(snapshot, now).ToList();

                var existing = active.FirstOrDefault(state => string.Equals(state.StateId, stateId, StringComparison.Ordinal));
                var instance = new AssertedStateInstance(
                    stateId,
                    existing?.Since ?? now,
                    ResolveExpiry(now, duration ?? definition.Duration));

                var transitions = new List<PendingTransition>();
                if (existing is null)
                {
                    active.Add(instance);
                    transitions.Add(new PendingTransition("asserted", stateId, "active", null, 1));
                }
                else
                {
                    active.Remove(existing);
                    active.Add(instance);
                }

                var commit = new StateCommit(
                    snapshot,
                    snapshot.Values,
                    active.OrderBy(state => state.StateId, StringComparer.Ordinal).ToArray(),
                    _packs.DefinitionVersion,
                    _packs.PackVersions,
                    now,
                    transitions);

                var committed = await _store.TryCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                if (committed is null)
                    continue;

                await DescribeAsync(committed, cancellationToken).ConfigureAwait(false);
                return existing is null;
            }

            throw new StateVitalsContentionException(
                $"State '{Options.StateId}' could not be asserted after {attempts} version conflicts.");
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    /// <summary>Clears an asserted state. Returns true when it was active.</summary>
    public async ValueTask<bool> ClearAsync(string stateId, CancellationToken cancellationToken = default)
    {
        if (!Enabled || _packs is null || _store is null)
            return false;

        await _advanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempts = Math.Max(1, Options.MaxAdvanceAttempts);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                var now = CurrentTime(snapshot);
                var active = ActiveAsserted(snapshot, now).ToList();
                var existing = active.FirstOrDefault(state => string.Equals(state.StateId, stateId, StringComparison.Ordinal));
                if (existing is null)
                    return false;

                active.Remove(existing);

                var commit = new StateCommit(
                    snapshot,
                    snapshot.Values,
                    active.OrderBy(state => state.StateId, StringComparer.Ordinal).ToArray(),
                    _packs.DefinitionVersion,
                    _packs.PackVersions,
                    now,
                    [new PendingTransition("asserted", stateId, "cleared", "active", 0)]);

                var committed = await _store.TryCommitAsync(commit, cancellationToken).ConfigureAwait(false);
                if (committed is null)
                    continue;

                await DescribeAsync(committed, cancellationToken).ConfigureAwait(false);
                return true;
            }

            throw new StateVitalsContentionException(
                $"State '{Options.StateId}' could not be cleared after {attempts} version conflicts.");
        }
        finally
        {
            _advanceGate.Release();
        }
    }

    /// <summary>
    /// Evaluates every trigger bound to <paramref name="triggerEventId"/>. The roll is seeded by
    /// <c>(packId, triggerEventId, sequence)</c>, so a replay yields the same outcome (§12.1).
    /// </summary>
    public async ValueTask<IReadOnlyList<TriggerOutcome>> TriggerAsync(
        string triggerEventId,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerEventId);
        if (!Enabled || _packs is null || _store is null)
            return [];

        var outcomes = new List<TriggerOutcome>();
        var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = CurrentTime(snapshot);
        var activeIds = ActiveAsserted(snapshot, now).Select(state => state.StateId).ToHashSet(StringComparer.Ordinal);

        foreach (var trigger in _packs.Triggers.Where(candidate =>
                     string.Equals(candidate.Event, triggerEventId, StringComparison.Ordinal)))
        {
            if (activeIds.Contains(trigger.StateId))
            {
                outcomes.Add(new TriggerOutcome(trigger.Id, trigger.StateId, false, 1d));
                continue;
            }

            var roll = TriggerRandom.UnitInterval(trigger.PackId, $"{triggerEventId}:{trigger.Id}", sequence);
            var fired = TriggerRandom.Roll(trigger.PackId, $"{triggerEventId}:{trigger.Id}", sequence, trigger.Probability);
            outcomes.Add(new TriggerOutcome(trigger.Id, trigger.StateId, fired, roll));

            if (fired)
                await AssertAsync(trigger.StateId, trigger.Duration, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }

    /// <summary>Reads the current snapshot (seeded on first use).</summary>
    public async ValueTask<StateSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || _store is null)
            return null;

        return await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Current bucketed view. When an advance is due it is committed first; otherwise the decay is
    /// projected read-only so the prompt still reflects reality without writing on every turn.
    /// </summary>
    public async ValueTask<StateView?> GetViewAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || _packs is null || _store is null)
            return null;

        var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = CurrentTime(snapshot);
        var due = (now - snapshot.UpdatedAt).TotalSeconds >= Math.Max(0, Options.MinAdvanceIntervalSeconds);

        if (due)
        {
            try
            {
                await AdvanceAsync(forces: null, cancellationToken).ConfigureAwait(false);
                snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (StateVitalsContentionException exception)
            {
                // A busy snapshot is not a reason to fail a turn: render the last committed values.
                _log?.Warning(
                    "state view advance failed code={0} error={1}",
                    StateVitalsCodes.AdvanceContended,
                    exception.Message);
            }
        }
        else
        {
            snapshot = Project(snapshot, now);
        }

        return await BuildViewAsync(snapshot, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renders the fragment body from the current view.</summary>
    public async ValueTask<string> RenderAsync(CancellationToken cancellationToken = default)
    {
        var view = await GetViewAsync(cancellationToken).ConfigureAwait(false);
        return view is null ? string.Empty : _renderer.Render(view, Options.IncludeDescriptions, Options.MaxFragmentCharacters);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _advanceGate.Dispose();

        if (_store is not null)
            await _store.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Wall-clock "now", never earlier than the stored timestamp: a backwards clock must not run the
    /// decay backwards.
    /// </summary>
    private DateTimeOffset CurrentTime(StateSnapshot snapshot)
    {
        var now = Clock.GetUtcNow();
        return now < snapshot.UpdatedAt ? snapshot.UpdatedAt : now;
    }

    private static DateTimeOffset? ResolveExpiry(DateTimeOffset now, TimeSpan? duration)
        => duration is { } value && value > TimeSpan.Zero ? now + value : null;

    /// <summary>Asserted states that are still live; expired ones are dropped on the next commit.</summary>
    private static IReadOnlyList<AssertedStateInstance> ActiveAsserted(StateSnapshot snapshot, DateTimeOffset now)
        => snapshot.AssertedStates.Where(state => !state.IsExpired(now)).ToArray();

    /// <summary>Read-only decay projection for throttled reads (no commit, no transitions).</summary>
    private StateSnapshot Project(StateSnapshot snapshot, DateTimeOffset now)
    {
        if (_packs is null)
            return snapshot;

        var active = ActiveAsserted(snapshot, now);
        var derived = StateResolver.Derived(_packs, snapshot.Values);
        var forces = StateResolver.Forces(
            _packs,
            active.Select(state => state.StateId)
                .Concat(derived.Select(state => state.Definition.Id))
                .ToHashSet(StringComparer.Ordinal));

        var result = VitalKernel.Advance(
            _packs.Vitals,
            snapshot.Values,
            snapshot.UpdatedAt,
            now,
            forces,
            _advanceOptions);

        return snapshot with { Values = result.Values, UpdatedAt = now };
    }

    /// <summary>Runs the code-hook rules declared by the packs (validated at load time).</summary>
    private async ValueTask<IReadOnlyList<VitalForce>> RuleForcesAsync(
        StateSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_packs is null || _rules is null)
            return [];

        var elapsed = now - snapshot.UpdatedAt;
        var context = new VitalRuleContext(snapshot.StateId, now, snapshot.UpdatedAt, elapsed, snapshot.Values);
        var forces = new List<VitalForce>();

        foreach (var name in _packs.Rules)
        {
            if (!_rules.TryGet(name, out var rule))
            {
                throw new StateVitalsStartupException(
                    StateVitalsCodes.RuleUnknown,
                    $"Rule '{name}' was declared but is not registered.");
            }

            var produced = await rule.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            if (produced is { Count: > 0 })
                forces.AddRange(produced);
        }

        return forces;
    }

    /// <summary>Writes one description per entry; idempotent by transition sequence.</summary>
    private async ValueTask DescribeAsync(StateCommitResult committed, CancellationToken cancellationToken)
    {
        if (_packs is null || _descriptions is null || committed.Transitions.Count == 0)
            return;

        foreach (var sequenced in committed.Transitions)
        {
            var request = BuildDescriptionRequest(committed.Snapshot.StateId, sequenced);
            if (request is null)
                continue;

            // No conversation/actor identity is passed in on purpose: global state text must stay
            // identity-free (design §7.2).
            await _descriptions.GetOrCreateAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private StateDescriptionRequest? BuildDescriptionRequest(string stateId, SequencedTransition sequenced)
    {
        if (_packs is null)
            return null;

        var transition = sequenced.Transition;
        if (string.Equals(transition.Kind, "asserted", StringComparison.Ordinal))
        {
            var definition = _packs.FindAsserted(transition.DefinitionId);
            if (definition is null || string.Equals(transition.LevelId, "cleared", StringComparison.Ordinal))
                return null;

            return new StateDescriptionRequest(
                stateId,
                sequenced.Seq,
                "asserted",
                definition.Id,
                "active",
                definition.Label,
                definition.Semantic,
                definition.Template,
                null,
                null,
                VoiceOf(definition.PackId));
        }

        var derived = _packs.FindDerived(transition.DefinitionId);
        if (derived is null)
            return null;

        var vital = _packs.FindVital(derived.VitalId);
        var level = vital?.Levels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, transition.LevelId, StringComparison.Ordinal));

        return new StateDescriptionRequest(
            stateId,
            sequenced.Seq,
            "derived",
            derived.Id,
            transition.LevelId ?? string.Empty,
            derived.Label,
            derived.Semantic ?? level?.Semantic,
            derived.Template ?? level?.Template,
            transition.PreviousLevelId,
            transition.Value,
            VoiceOf(derived.PackId));
    }

    /// <summary>Pack-supplied voice; the core plugin never authors tone of its own (§12.3).</summary>
    private DescriptionVoice VoiceOf(string packId)
        => _packs?.Packs.FirstOrDefault(pack => string.Equals(pack.PackId, packId, StringComparison.Ordinal))?.Voice
           ?? DescriptionVoice.Empty;

    private async ValueTask<StateView> BuildViewAsync(
        StateSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var vitals = new List<VitalViewEntry>();
        var states = new List<StateViewEntry>();

        if (_packs is null)
            return new StateView(snapshot.StateId, snapshot.Version, snapshot.NextTransitionSeq, snapshot.UpdatedAt, vitals, states);

        var derived = StateResolver.Derived(_packs, snapshot.Values);

        foreach (var vital in _packs.Vitals)
        {
            var value = snapshot.Values.TryGetValue(vital.Id, out var stored)
                ? vital.Range.Clamp(stored)
                : vital.Baseline;
            var level = vital.ResolveLevel(value);

            string? description = null;
            if (Options.IncludeDescriptions)
            {
                var derivedState = derived.FirstOrDefault(state =>
                    string.Equals(state.Definition.VitalId, vital.Id, StringComparison.Ordinal)
                    && string.Equals(state.LevelId, level.Id, StringComparison.Ordinal));

                var record = derivedState is null || _store is null
                    ? null
                    : await _store.FindDescriptionAsync(snapshot.StateId, derivedState.Definition.Id, level.Id, cancellationToken)
                        .ConfigureAwait(false);

                description = record?.Text
                              ?? StateDescriptionService.Sanitize(level.Template ?? level.Semantic ?? level.Label);
            }

            vitals.Add(new VitalViewEntry(vital.Id, vital.LocalId, vital.Label, level.Id, level.Label, value, description));
        }

        foreach (var asserted in ActiveAsserted(snapshot, now))
        {
            var definition = _packs.FindAsserted(asserted.StateId);
            if (definition is null)
                continue;

            string? description = null;
            if (Options.IncludeDescriptions)
            {
                var record = _store is null
                    ? null
                    : await _store.FindDescriptionAsync(snapshot.StateId, definition.Id, "active", cancellationToken)
                        .ConfigureAwait(false);

                description = record?.Text ?? StateDescriptionService.Sanitize(definition.Template ?? definition.Semantic ?? definition.Label);
            }

            states.Add(new StateViewEntry(definition.Id, definition.LocalId, definition.Label, "asserted", description));
        }

        return new StateView(snapshot.StateId, snapshot.Version, snapshot.NextTransitionSeq, snapshot.UpdatedAt, vitals, states);
    }
}

/// <summary>Raised when the version compare-and-set never succeeded within the retry budget.</summary>
public sealed class StateVitalsContentionException : Exception
{
    public StateVitalsContentionException(string message)
        : base(message)
    {
    }
}
