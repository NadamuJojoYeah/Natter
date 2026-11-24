using System.Globalization;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.StateVitals;
using Natter.Plugin.StateVitals.Definitions;
using Natter.Plugin.StateVitals.Descriptions;
using Natter.Plugin.StateVitals.Kernel;
using Natter.Plugin.StateVitals.Storage;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Kernel tests for State &amp; Vitals (docs/STATE_VITALS_DESIGN.md §15): multiplicative saturation
/// with double clamping, bucketed rendering with a stable plan hash, asserted/derived separation,
/// fixed-point persistence, versioned concurrency, one-shot idempotent descriptions, seeded
/// randomness and identity-free state text.
/// </summary>
public sealed class StateVitalsTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- host gap (§13.1)

    [Fact]
    public void Prompt_fragment_context_carries_the_conversation_scope()
    {
        var context = PromptFragmentContexts.FromPromptRequest(
            Request(actor: "user-1", conversation: "conv-7", guild: "guild-3"),
            PromptCompilerDefaults.StaticPresetId);

        var scope = Assert.IsType<ConversationScope>(context.Conversation);
        Assert.Equal("discord:discord:bot", scope.SourceKey);
        Assert.Equal("conv-7", scope.ConversationId);
        Assert.Equal("guild-3", scope.GuildId);
        Assert.Equal("user-1", scope.ActorId);

        Assert.Equal("discord", context.Platform);
        Assert.Equal("discord", context.Protocol);
        Assert.Equal(ConversationKind.Direct, context.ConversationKind);
        Assert.Equal(PromptCompilerDefaults.StaticPresetId, context.PresetId);
    }

    [Fact]
    public void Conversation_scope_is_an_init_property_so_the_positional_signature_is_unchanged()
    {
        var context = new PromptFragmentContext(
            new RuntimeProfile { Id = "p" },
            PromptCompilerDefaults.StaticPresetId,
            "zh-CN",
            "discord",
            "discord",
            ConversationKind.Direct,
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(context.Conversation);

        var withScope = context with { Conversation = new ConversationScope("s", "c", "g", "a") };
        Assert.Equal("c", withScope.Conversation!.ConversationId);
        Assert.Equal(context.Platform, withScope.Platform);
        Assert.Equal(context.Variables, withScope.Variables);
    }

    [Fact]
    public void Conversation_scope_projects_missing_user_ids_as_null()
    {
        var request = new PromptRequest(
            new RuntimeProfile { Id = "p" },
            Source.Create("discord", "discord", "bot"),
            new Conversation("conv", ConversationKind.Guild),
            new User(string.Empty, "anonymous"),
            Locale: null,
            Variables: new Dictionary<string, string>());

        var scope = Assert.IsType<ConversationScope>(PromptFragmentContexts.FromPromptRequest(request, null).Conversation);
        Assert.Null(scope.ActorId);
        Assert.Null(scope.GuildId);
    }

    // ---------------------------------------------------------------- numeric model (§3.2)

    [Fact]
    public void Multiplicative_decay_moves_a_fraction_of_the_remaining_space()
    {
        var packs = Packs(HungerPack(baseline: 50, target: 100, ratePerHour: 0.08));

        var result = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 50)), Epoch, Epoch.AddHours(1));

        // 50 + (100 - 50) * 0.08 = 54: a proportion of the gap, not a fixed step.
        Assert.Equal(54d, result.Values["core#hunger"], 6);
    }

    [Fact]
    public void Decay_slows_down_as_it_approaches_the_target()
    {
        var packs = Packs(HungerPack(baseline: 0, target: 100, ratePerHour: 0.5));

        var first = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 0)), Epoch, Epoch.AddHours(1));
        var second = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 50)), Epoch, Epoch.AddHours(1));

        Assert.Equal(50d, first.Values["core#hunger"], 6);
        Assert.Equal(75d, second.Values["core#hunger"], 6);
        Assert.True(second.Values["core#hunger"] - 50d < first.Values["core#hunger"]);
    }

    [Fact]
    public void An_offline_week_is_clamped_and_never_overshoots()
    {
        var packs = Packs(HungerPack(baseline: 50, target: 100, ratePerHour: 0.08));

        var result = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 50)), Epoch, Epoch.AddDays(7));

        // k*dt would be 13.44; clamped to MAX_STEP=0.5 → 50 + 50*0.5 = 75, still inside the range.
        Assert.Equal(75d, result.Values["core#hunger"], 6);
        Assert.InRange(result.Values["core#hunger"], 0, 100);
    }

    [Fact]
    public void A_target_below_the_value_approaches_the_floor_and_never_goes_negative()
    {
        var packs = Packs(HungerPack(baseline: 20, target: 0, ratePerHour: 0.05));

        var result = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 20)), Epoch, Epoch.AddDays(7));

        Assert.Equal(10d, result.Values["core#hunger"], 6);
        Assert.True(result.Values["core#hunger"] >= 0);
    }

    [Fact]
    public void Elapsed_time_is_clamped_and_a_backwards_clock_cannot_invert_decay()
    {
        var options = new VitalAdvanceOptions(MaxStep: 1, MaxDeltaHours: 24, MaxStepPerTickFraction: 1);

        Assert.Equal(24d, VitalKernel.ElapsedHours(Epoch, Epoch.AddDays(30), options), 6);
        Assert.Equal(0d, VitalKernel.ElapsedHours(Epoch, Epoch.AddHours(-5), options), 6);
    }

    [Fact]
    public void The_total_change_of_one_tick_is_clamped_after_all_forces_are_summed()
    {
        var packs = Packs(HungerPack(baseline: 50, target: 50, ratePerHour: 0.1));
        var options = new VitalAdvanceOptions(MaxStep: 0.5, MaxDeltaHours: 720, MaxStepPerTickFraction: 0.5);

        var up = VitalKernel.Advance(
            packs.Vitals,
            Values(("core#hunger", 50)),
            Epoch,
            Epoch,
            [new VitalForce("core#hunger", 0, VitalForceKind.Delta, 200)],
            options);

        var down = VitalKernel.Advance(
            packs.Vitals,
            Values(("core#hunger", 50)),
            Epoch,
            Epoch,
            [new VitalForce("core#hunger", 0, VitalForceKind.Delta, -500)],
            options);

        // Range span 100 × 0.5 = 50 per tick.
        Assert.Equal(100d, up.Values["core#hunger"], 6);
        Assert.Equal(0d, down.Values["core#hunger"], 6);
    }

    [Fact]
    public void Multiple_forces_are_applied_once_and_independent_of_insertion_order()
    {
        var packs = Packs(NumericPack());

        var forces = new[]
        {
            new VitalForce("core#hunger", 3, VitalForceKind.Delta, 7),
            new VitalForce("core#hunger", 1, VitalForceKind.Delta, 2.5),
            new VitalForce("core#hunger", 2, VitalForceKind.RateMultiplier, 1.5),
            new VitalForce("core#energy", 1, VitalForceKind.Bias, -2),
        };

        var forward = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 40), ("core#energy", 60)), Epoch, Epoch.AddHours(2), forces);
        var reversed = VitalKernel.Advance(
            packs.Vitals,
            Values(("core#hunger", 40), ("core#energy", 60)),
            Epoch,
            Epoch.AddHours(2),
            forces.Reverse());

        Assert.Equal(forward.Values["core#hunger"], reversed.Values["core#hunger"]);
        Assert.Equal(forward.Values["core#energy"], reversed.Values["core#energy"]);

        // 40 + (100-40)*clamp(0.1*2*1.5=0.3) + (2.5+7)
        Assert.Equal(40d + 60d * 0.3d + 9.5d, forward.Values["core#hunger"], 6);
        // 60 + (0-60)*clamp(0.05*2=0.1) + (-2*2h)
        Assert.Equal(60d - 60d * 0.1d - 4d, forward.Values["core#energy"], 6);
    }

    [Fact]
    public void A_cyclic_target_follows_the_injected_wall_clock()
    {
        var packs = Packs(SleepinessPack());
        var vital = packs.FindVital("core#sleepiness")!;

        var night = vital.Decay.Target.Resolve(new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero));
        var noon = vital.Decay.Target.Resolve(new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal(90d, night, 6);
        Assert.Equal(10d, noon, 6);
    }

    [Fact]
    public async Task Everything_is_driven_by_the_injected_clock()
    {
        var packs = Packs(HungerPack(baseline: 10, target: 100, ratePerHour: 0.5));
        var clock = new ControlledClock(Epoch);
        await using var runtime = Runtime(packs, new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch), clock);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(55d, await ReadVitalAsync(runtime, "core#hunger"), 6);

        // No time passed: nothing moves even though the real UTC clock keeps running.
        Assert.Equal(55d, await ReadVitalAsync(runtime, "core#hunger"), 6);
    }

    // ---------------------------------------------------------------- fixed point (§12.2)

    [Fact]
    public void Fixed_point_quantization_is_stable_and_lossless()
    {
        Assert.Equal(12500L, VitalFixedPoint.ToFixed(12.5));
        Assert.Equal(12.5, VitalFixedPoint.FromFixed(12500), 9);
        Assert.Equal(-12500L, VitalFixedPoint.ToFixed(-12.5));

        // Quantizing again changes nothing: the stored representation is a fixed point of the map.
        foreach (var value in new[] { 0d, 0.001d, 12.3456d, -99.9995d, 100d })
        {
            var once = VitalFixedPoint.Quantize(value);
            Assert.Equal(once, VitalFixedPoint.Quantize(once));
            Assert.Equal(VitalFixedPoint.ToFixed(value), VitalFixedPoint.ToFixed(once));
        }
    }

    [Fact]
    public async Task Persisted_values_round_trip_through_the_fixed_point_representation()
    {
        using var temp = new TempStateDirectory();
        var packs = Packs(HungerPack(baseline: 12.3456, target: 100, ratePerHour: 0.08));
        var clock = new ControlledClock(Epoch);
        var databasePath = Path.Combine(temp.Root, "state.db");

        var store = new SqliteStateVitalsStore(
            databasePath,
            "character",
            SeedValues(packs),
            packs.DefinitionVersion,
            packs.PackVersions,
            Epoch);

        await using var runtime = Runtime(packs, store, clock);
        clock.Advance(TimeSpan.FromHours(1));
        await runtime.AdvanceAsync();

        var stored = await store.ReadAsync();
        var value = stored.Values["core#hunger"];

        // The value is a quantized (1/1000) number, and the file round-trips it exactly.
        Assert.Equal(VitalFixedPoint.Quantize(value), value);
        Assert.Equal(VitalFixedPoint.ToFixed(value) / 1000d, value, 9);

        // Re-opening the same file yields the identical value (no float drift in the payload).
        var reopened = new SqliteStateVitalsStore(
            databasePath,
            "character",
            SeedValues(packs),
            packs.DefinitionVersion,
            packs.PackVersions,
            Epoch);
        var again = await reopened.ReadAsync();
        Assert.Equal(value, again.Values["core#hunger"]);
        await reopened.DisposeAsync();
    }

    [Fact]
    public void Kernel_output_is_always_quantized_to_a_thousandth()
    {
        var packs = Packs(HungerPack(baseline: 0, target: 100, ratePerHour: 1d / 3d));

        var result = VitalKernel.Advance(packs.Vitals, Values(("core#hunger", 0)), Epoch, Epoch.AddHours(1));
        var value = result.Values["core#hunger"];

        Assert.Equal(VitalFixedPoint.Quantize(value), value);
        // ratePerHour 0.3333 (formatted with 4 decimals) → 33.33 after quantization.
        Assert.Equal(33.33d, value, 6);
    }

    // ---------------------------------------------------------------- asserted vs derived (§3.1)

    [Fact]
    public void Derived_states_are_a_read_only_view_of_vital_buckets()
    {
        var packs = Packs(CorePack());

        var low = StateResolver.Derived(packs, Values(("core#hunger", 10), ("core#energy", 70)));
        var high = StateResolver.Derived(packs, Values(("core#hunger", 90), ("core#energy", 70)));

        Assert.Empty(low);
        var starving = Assert.Single(high);
        Assert.Equal("core#starving", starving.Definition.Id);
        Assert.Equal("starving", starving.LevelId);
    }

    [Fact]
    public async Task Derived_states_are_never_persisted_but_asserted_states_are()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore(
            "character",
            Values(("core#hunger", 80), ("core#energy", 70)),
            packs.DefinitionVersion,
            Epoch,
            packs.PackVersions);

        await using var runtime = Runtime(packs, store, clock);

        // Moving the numbers produces derived transitions, not stored state rows.
        clock.Advance(TimeSpan.FromHours(40));
        await runtime.AdvanceAsync();

        var afterDerived = await store.ReadAsync();
        Assert.Empty(afterDerived.AssertedStates);

        var transitions = await store.ReadTransitionsAsync("character", 20);
        Assert.Contains(transitions, record => record.Kind == "derived");

        // Asserting a state is the only writable path and it is persisted with an expiry.
        await runtime.AssertAsync("core#injured", TimeSpan.FromHours(2));
        var afterAssert = await store.ReadAsync();
        var instance = Assert.Single(afterAssert.AssertedStates);
        Assert.Equal("core#injured", instance.StateId);
        Assert.NotNull(instance.ExpiresAt);
        // Asserted at Epoch+40h for 2h.
        Assert.True(instance.IsExpired(Epoch.AddHours(43)));
        Assert.False(instance.IsExpired(Epoch.AddHours(41)));
    }

    [Fact]
    public async Task Expired_asserted_states_are_dropped_on_the_next_commit()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);

        await runtime.AssertAsync("core#injured", TimeSpan.FromHours(1));
        clock.Advance(TimeSpan.FromHours(2));
        await runtime.AdvanceAsync();

        var snapshot = await store.ReadAsync();
        Assert.Empty(snapshot.AssertedStates);
    }

    [Fact]
    public void An_effect_cannot_point_from_a_vital_or_into_a_state()
    {
        // vital → vital is rejected: only state → vital arrows exist (§3.1).
        var fromVital = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [
                { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                  "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                  "levels": [ { "at": 0, "id": "a" } ] },
                { "id": "energy", "range": { "min": 0, "max": 100 }, "baseline": 0,
                  "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 0 },
                  "levels": [ { "at": 0, "id": "a" } ] } ],
              "effects": [ { "id": "bad", "from": "core#hunger", "to": "core#energy", "bias": 1, "stacking": "add" } ] }
            """);

        Assert.False(fromVital.Succeeded);
        Assert.Contains(fromVital.Issues, issue => issue.Code == StateVitalsCodes.EffectSourceMustBeState);

        // state → state is rejected: a derived state can never be written.
        var intoState = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" }, { "at": 50, "id": "b" } ] } ],
              "assertedStates": [ { "id": "injured" } ],
              "derivedStates": [ { "id": "starving", "vital": "hunger", "levels": [ "b" ] } ],
              "effects": [ { "id": "bad", "from": "injured", "to": "core#starving", "bias": 1, "stacking": "add" } ] }
            """);

        Assert.False(intoState.Succeeded);
        Assert.Contains(intoState.Issues, issue => issue.Code == StateVitalsCodes.EffectTargetMustBeVital);
    }

    // ---------------------------------------------------------------- packs (§9)

    [Fact]
    public void Inheritance_merges_the_parent_and_the_child_wins()
    {
        var packs = Packs(
            """
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [
                { "abstract": true, "id": "baseNeed", "range": { "min": 0, "max": 100 }, "baseline": 5,
                  "levels": [ { "at": 0, "id": "sated" }, { "at": 60, "id": "urgent" } ] },
                { "id": "hunger", "parent": "baseNeed", "label": "饥饿",
                  "decay": { "mode": "multiplicative", "ratePerHour": 0.08, "target": 100 } }
              ] }
            """);

        // The abstract parent is not instantiated; the child inherits range + levels.
        Assert.Single(packs.Vitals);
        var hunger = packs.FindVital("core#hunger")!;
        Assert.Equal(100, hunger.Range.Max);
        Assert.Equal(5, hunger.Baseline);
        Assert.Equal(["sated", "urgent"], hunger.Levels.Select(level => level.Id));
        Assert.Equal(0.08, hunger.Decay.RatePerHour, 6);
    }

    [Fact]
    public void A_parent_cycle_is_rejected_with_an_actionable_issue()
    {
        var result = Load(
            """
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [
                { "abstract": true, "id": "a", "parent": "b" },
                { "abstract": true, "id": "b", "parent": "a" },
                { "id": "c", "parent": "a", "range": { "min": 0, "max": 100 },
                  "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                  "levels": [ { "at": 0, "id": "x" } ] }
              ] }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.ParentCycle);
        Assert.Contains(result.Issues, issue => issue.Suggestion is { Length: > 0 });
    }

    [Fact]
    public void A_missing_soft_dependency_is_skipped_and_reported()
    {
        var result = LoadMulti(
            """
            { "schemaVersion": 1, "packId": "menstrual", "version": "1.0.0", "mayRequire": [ "disease" ],
              "vitals": [ { "id": "cycle", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """,
            """
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """);

        // Missing soft dependency is not an error (§9.5), but it must be visible in validate output.
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Issues));
        Assert.Null(result.Set!.FindVital("menstrual#cycle"));
        Assert.Contains(result.Skipped, skip => skip.PackId == "menstrual" && skip.Reason.Contains("disease", StringComparison.Ordinal));

        // ... and with the dependency installed the same pack loads.
        var withDependency = Packs(
            """
            { "schemaVersion": 1, "packId": "disease", "version": "1.0.0",
              "vitals": [ { "id": "fever", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 0 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """,
            """
            { "schemaVersion": 1, "packId": "menstrual", "version": "1.0.0", "mayRequire": [ "disease" ],
              "vitals": [ { "id": "cycle", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """);

        Assert.NotNull(withDependency.FindVital("menstrual#cycle"));
    }

    [Fact]
    public void Pack_quotas_are_enforced_with_the_offending_pack_named()
    {
        var levels = string.Join(",", Enumerable.Range(0, 6).Select(index => $$"""{ "at": {{index * 10}}, "id": "l{{index}}" }"""));
        var vitals = string.Join(",", Enumerable.Range(0, 3).Select(index => $$"""
            { "id": "v{{index}}", "range": { "min": 0, "max": 100 }, "baseline": 0,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
              "levels": [ { "at": 0, "id": "l0" } ] }
            """));

        var result = ContentPackLoader.TryLoad(
            WritePacks($$"""
                { "schemaVersion": 1, "packId": "big", "version": "1.0.0", "vitals": [ {{vitals}} ] }
                """),
            rules: null,
            new PackLoadOptions(MaxVitalsPerPack: 2));

        Assert.False(result.Succeeded);
        var issue = Assert.Single(result.Issues.Where(candidate => candidate.Code == StateVitalsCodes.DefinitionQuota));
        Assert.Contains("big", issue.Message, StringComparison.Ordinal);
        Assert.Contains("vitals", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_core_pack_uses_all_five_primitives()
    {
        var directory = Path.Combine(
            RepositoryRoot(),
            "plugins",
            "Natter.Plugin.StateVitals",
            "definitions");

        var packs = ContentPackLoader.Load(directory, new VitalRuleRegistry());

        // ids are pack-scoped from day one (§2 早定格 8).
        Assert.NotNull(packs.FindVital("core#hunger"));
        Assert.NotNull(packs.FindAsserted("core#injured"));
        Assert.NotNull(packs.FindDerived("core#starving"));
        Assert.NotEmpty(packs.Effects);
        Assert.NotEmpty(packs.Triggers);
        Assert.Equal("core", Assert.Single(packs.Packs).PackId);
        Assert.Contains("core@1.0.0", packs.DefinitionVersion, StringComparison.Ordinal);

        // Inheritance applied: hunger inherits the abstract baseNeed levels.
        var hunger = packs.FindVital("core#hunger")!;
        Assert.Equal(["sated", "peckish", "hungry", "starving"], hunger.Levels.Select(level => level.Id));
        Assert.Equal("饥饿", hunger.Label);

        // Every effect obeys the one-way rule (state → vital).
        foreach (var effect in packs.Effects)
        {
            Assert.True(packs.IsKnownState(effect.FromStateId), effect.FromStateId);
            Assert.NotNull(packs.FindVital(effect.ToVitalId));
        }

        // Voice comes from the pack, not the core plugin (§12.3).
        Assert.False(string.IsNullOrWhiteSpace(packs.Packs[0].Voice.Prompt));
    }

    // ---------------------------------------------------------------- seeded randomness (§12.1)

    [Fact]
    public void Randomness_is_seeded_by_pack_event_and_sequence()
    {
        var first = TriggerRandom.UnitInterval("core", "combat.hit", 7);
        var again = TriggerRandom.UnitInterval("core", "combat.hit", 7);
        var other = TriggerRandom.UnitInterval("core", "combat.hit", 8);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.InRange(first, 0d, 1d);
        Assert.Equal(TriggerRandom.Seed("core", "combat.hit", 7), TriggerRandom.Seed("core", "combat.hit", 7));
        Assert.NotEqual(TriggerRandom.Seed("core", "combat.hit", 7), TriggerRandom.Seed("other", "combat.hit", 7));
    }

    [Fact]
    public async Task Replaying_the_same_trigger_event_yields_the_same_outcome()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);

        var play = await runtime.TriggerAsync("combat.hit", sequence: 42);
        var replay = await runtime.TriggerAsync("combat.hit", sequence: 42);

        Assert.Equal(
            play.Select(outcome => (outcome.TriggerId, outcome.Fired, outcome.Roll)),
            replay.Select(outcome => (outcome.TriggerId, outcome.Fired, outcome.Roll)));

        // A different sequence is allowed to differ, but it must still be deterministic.
        var other = await runtime.TriggerAsync("combat.hit", sequence: 43);
        Assert.Equal(
            other.Select(outcome => outcome.Roll),
            (await runtime.TriggerAsync("combat.hit", sequence: 43)).Select(outcome => outcome.Roll));
    }

    // ---------------------------------------------------------------- buckets and hash (§4)

    [Fact]
    public void Buckets_are_monotonic_cover_the_range_and_have_no_holes()
    {
        var packs = Packs(CorePack());

        foreach (var vital in packs.Vitals)
        {
            Assert.Equal(vital.Range.Min, vital.Levels[0].At, 6);

            var previous = 0;
            for (var value = vital.Range.Min; value <= vital.Range.Max; value += 0.5)
            {
                var index = vital.Levels.ToList().IndexOf(vital.ResolveLevel(value));
                Assert.True(index >= previous, $"bucket index went backwards at {value} for {vital.Id}");
                previous = index;
            }

            Assert.Equal(vital.Levels[^1].Id, vital.ResolveLevel(vital.Range.Max).Id);
            Assert.Equal(vital.Levels[0].Id, vital.ResolveLevel(vital.Range.Min).Id);
        }
    }

    [Fact]
    public async Task The_plan_hash_is_stable_while_the_bucket_does_not_change()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);
        var provider = new StateVitalsFragmentProvider(runtime, runtime.Options);

        var first = await provider.GetFragmentsAsync(Context());
        Assert.Single(first);

        clock.Advance(TimeSpan.FromMinutes(3));
        var second = await provider.GetFragmentsAsync(Context());
        clock.Advance(TimeSpan.FromMinutes(3));
        var third = await provider.GetFragmentsAsync(Context());

        Assert.Equal(first[0].Text, second[0].Text);
        Assert.Equal(first[0].Text, third[0].Text);
        Assert.Equal(PlanHash(first), PlanHash(second));
        Assert.Equal(PlanHash(first), PlanHash(third));
    }

    [Fact]
    public async Task Crossing_a_bucket_changes_the_plan_hash()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);
        var provider = new StateVitalsFragmentProvider(runtime, runtime.Options);

        var before = await provider.GetFragmentsAsync(Context());
        var beforeHash = PlanHash(before);

        clock.Advance(TimeSpan.FromHours(40));
        var after = await provider.GetFragmentsAsync(Context());

        Assert.NotEqual(beforeHash, PlanHash(after));
        Assert.NotEqual(before[0].Text, after[0].Text);
    }

    [Fact]
    public async Task The_fragment_never_contains_a_float()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);
        var provider = new StateVitalsFragmentProvider(runtime, runtime.Options);

        clock.Advance(TimeSpan.FromHours(7));
        var fragments = await provider.GetFragmentsAsync(Context());

        Assert.DoesNotContain(fragments[0].Text, character => char.IsDigit(character));
        Assert.Contains("[hunger: ", fragments[0].Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- descriptions (§6)

    [Fact]
    public async Task A_description_is_generated_once_per_transition_and_replayed_idempotently()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        var generator = new CountingDescriptionGenerator();
        await using var runtime = Runtime(packs, store, clock, generator);

        clock.Advance(TimeSpan.FromHours(40));
        await runtime.AdvanceAsync();

        var transitions = await store.ReadTransitionsAsync("character", 20);
        var starving = Assert.Single(transitions.Where(record => record.Kind == "derived"));
        var attempts = generator.Calls;
        Assert.True(attempts >= 1);

        var description = await runtime.Descriptions!.GetOrCreateAsync(new StateDescriptionRequest(
            "character",
            starving.Seq,
            "derived",
            starving.DefinitionId,
            starving.LevelId ?? "starving",
            "饿坏了",
            null,
            null,
            null,
            null,
            DescriptionVoice.Empty));

        Assert.Equal(attempts, generator.Calls);
        Assert.Equal(StateDescriptionSources.Generated, description.Source);
        Assert.Equal(StateVitalsHashing.TextHash(description.Text), description.TextHash);
    }

    [Fact]
    public async Task A_failing_generator_degrades_to_the_pack_template()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock, new ThrowingDescriptionGenerator());

        clock.Advance(TimeSpan.FromHours(40));
        await runtime.AdvanceAsync();

        var transitions = await store.ReadTransitionsAsync("character", 20);
        var starving = transitions.First(record => record.Kind == "derived");
        var stored = await store.GetDescriptionAsync("character", starving.Seq);

        var description = Assert.IsType<StoredDescription>(stored);
        Assert.Equal(StateDescriptionSources.Template, description.Source);
        Assert.False(string.IsNullOrWhiteSpace(description.Text));
    }

    [Fact]
    public async Task A_slow_generator_is_cut_off_by_the_description_timeout()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        var options = new StateVitalsOptions { Enabled = true, DescriptionTimeoutMilliseconds = 50 };
        await using var runtime = Runtime(packs, store, clock, new SlowDescriptionGenerator(), options);

        clock.Advance(TimeSpan.FromHours(40));
        var started = DateTimeOffset.UtcNow;
        await runtime.AdvanceAsync();
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));

        var transitions = await store.ReadTransitionsAsync("character", 20);
        var starving = transitions.First(record => record.Kind == "derived");
        var stored = await store.GetDescriptionAsync("character", starving.Seq);
        Assert.Equal(StateDescriptionSources.Template, stored!.Source);
    }

    [Fact]
    public async Task A_generated_line_containing_a_pack_banned_word_falls_back_to_the_template()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock, new BannedWordDescriptionGenerator());

        clock.Advance(TimeSpan.FromHours(40));
        await runtime.AdvanceAsync();

        var transitions = await store.ReadTransitionsAsync("character", 20);
        var starving = transitions.First(record => record.Kind == "derived");
        var stored = await store.GetDescriptionAsync("character", starving.Seq);

        Assert.Equal(StateDescriptionSources.Template, stored!.Source);
        Assert.DoesNotContain("系统", stored.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stored_text_is_stable_while_the_state_holds()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock, new CountingDescriptionGenerator());

        clock.Advance(TimeSpan.FromHours(40));
        await runtime.AdvanceAsync();
        var first = await runtime.RenderAsync();

        clock.Advance(TimeSpan.FromMinutes(30));
        await runtime.AdvanceAsync();
        var second = await runtime.RenderAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_changed_stored_description_changes_the_plan_hash()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 95), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);
        var provider = new StateVitalsFragmentProvider(runtime, runtime.Options);

        clock.Advance(TimeSpan.FromHours(1));
        var before = await provider.GetFragmentsAsync(Context());
        var beforeHash = PlanHash(before);

        await store.SaveDescriptionAsync(new StoredDescription(
            "character",
            9_000,
            "core#starving",
            "starving",
            "完全不同的描述文本。",
            StateDescriptionSources.Generated,
            StateVitalsHashing.TextHash("完全不同的描述文本。"),
            clock.GetUtcNow()));

        var after = await provider.GetFragmentsAsync(Context());
        Assert.NotEqual(beforeHash, PlanHash(after));
        Assert.Contains("完全不同的描述文本。", after[0].Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- concurrency (§7.2)

    [Fact]
    public async Task A_stale_version_is_rejected_instead_of_overwriting()
    {
        var packs = Packs(HungerPack(baseline: 50, target: 50, ratePerHour: 0.1));
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 50)), packs.DefinitionVersion, Epoch, packs.PackVersions);
        var stale = await store.ReadAsync();

        var first = await store.TryCommitAsync(Commit(stale, packs, ("core#hunger", 60)));
        var second = await store.TryCommitAsync(Commit(stale, packs, ("core#hunger", 70)));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, store.ConflictCount);

        var current = await store.ReadAsync();
        Assert.Equal(60d, current.Values["core#hunger"], 6);
        Assert.Equal(1, current.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task Concurrent_pushes_against_one_database_accumulate_to_plus_twenty()
    {
        using var temp = new TempStateDirectory();
        var packs = Packs(HungerPack(baseline: 50, target: 50, ratePerHour: 0.1));
        var clock = new ControlledClock(Epoch);
        var databasePath = Path.Combine(temp.Root, "state.db");

        IStateVitalsStore NewStore() => new SqliteStateVitalsStore(
            databasePath,
            "character",
            Values(("core#hunger", 50)),
            packs.DefinitionVersion,
            packs.PackVersions,
            Epoch);

        await using var runtimeA = Runtime(packs, NewStore(), clock);
        await using var runtimeB = Runtime(packs, NewStore(), clock);

        await Task.WhenAll(
                runtimeA.PushAsync("core#hunger", 10).AsTask(),
                runtimeB.PushAsync("core#hunger", 10).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = await runtimeA.ReadSnapshotAsync();
        Assert.Equal(70d, snapshot!.Values["core#hunger"], 6);
        Assert.Equal(2, snapshot.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_forced_interleaving_retries_and_still_accumulates_to_plus_twenty()
    {
        var packs = Packs(HungerPack(baseline: 50, target: 50, ratePerHour: 0.1));
        var clock = new ControlledClock(Epoch);
        var inner = new InMemoryStateVitalsStore("character", Values(("core#hunger", 50)), packs.DefinitionVersion, Epoch, packs.PackVersions);
        var gate = new CommitGate();

        await using var runtimeA = Runtime(packs, inner, clock);
        await using var runtimeB = Runtime(packs, new GatedStore(inner, gate), clock);

        var pushB = runtimeB.PushAsync("core#hunger", 10).AsTask();
        await Task.WhenAny(gate.Waiting, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(gate.IsWaiting, "runtime B never reached its first commit");

        await runtimeA.PushAsync("core#hunger", 10).AsTask();

        gate.Release();
        await pushB.WaitAsync(TimeSpan.FromSeconds(30));

        var snapshot = await inner.ReadAsync();
        Assert.True(inner.ConflictCount >= 1, "the stale commit was not rejected");
        Assert.Equal(70d, snapshot.Values["core#hunger"], 6);
        Assert.Equal(2, snapshot.Version);
    }

    [Fact]
    public async Task The_snapshot_keeps_the_definition_and_pack_versions()
    {
        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", SeedValues(packs), "old-version", Epoch);
        await using var runtime = Runtime(packs, store, clock);

        await runtime.PushAsync("core#hunger", 5);
        var snapshot = await store.ReadAsync();

        Assert.Equal(packs.DefinitionVersion, snapshot.DefinitionVersion);
        Assert.Equal("1.0.0", snapshot.PackVersions["core"]);
    }

    // ---------------------------------------------------------------- identity (§7.2)

    [Fact]
    public async Task The_state_text_never_contains_a_user_or_conversation_identifier()
    {
        const string actor = "SENTINEL-ACTOR-9f2b";
        const string conversation = "SENTINEL-CONV-33c1";
        const string guild = "SENTINEL-GUILD-77aa";

        var packs = Packs(CorePack());
        var clock = new ControlledClock(Epoch);
        var store = new InMemoryStateVitalsStore("character", Values(("core#hunger", 80), ("core#energy", 70)), packs.DefinitionVersion, Epoch);
        await using var runtime = Runtime(packs, store, clock);
        var provider = new StateVitalsFragmentProvider(runtime, runtime.Options);

        await runtime.AssertAsync("core#injured", TimeSpan.FromHours(4));

        var context = PromptFragmentContexts.FromPromptRequest(
            Request(actor: actor, conversation: conversation, guild: guild),
            PromptCompilerDefaults.StaticPresetId);

        clock.Advance(TimeSpan.FromHours(40));
        var first = await provider.GetFragmentsAsync(context);
        clock.Advance(TimeSpan.FromHours(40));
        var second = await provider.GetFragmentsAsync(context);

        foreach (var fragment in first.Concat(second))
        {
            Assert.DoesNotContain(actor, fragment.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(conversation, fragment.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(guild, fragment.Text, StringComparison.Ordinal);
        }

        foreach (var transition in await store.ReadTransitionsAsync("character", 50))
        {
            var description = await store.GetDescriptionAsync("character", transition.Seq);
            if (description is null)
                continue;

            Assert.DoesNotContain(actor, description.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(conversation, description.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(guild, description.Text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- validation (§10.1)

    [Fact]
    public void Levels_must_start_at_the_range_minimum()
    {
        var result = Load(VitalPack("""[ { "at": 20, "id": "a" }, { "at": 50, "id": "b" } ]"""));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DefinitionLevelGap);
        Assert.Contains(result.Issues, issue => issue.Suggestion is { Length: > 0 });
        Assert.Contains(result.Issues, issue => issue.Pointer.Contains("/levels/0", StringComparison.Ordinal));
    }

    [Fact]
    public void Levels_must_be_strictly_increasing()
    {
        var result = Load(VitalPack("""[ { "at": 0, "id": "a" }, { "at": 60, "id": "b" }, { "at": 40, "id": "c" } ]"""));

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DefinitionLevelNotMonotonic);
    }

    [Fact]
    public void Duplicate_level_ids_are_rejected()
    {
        var result = Load(VitalPack("""[ { "at": 0, "id": "a" }, { "at": 50, "id": "a" } ]"""));

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DefinitionLevelDuplicate);
    }

    [Fact]
    public void An_unknown_rule_name_fails_at_load_time()
    {
        var json = """
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0", "rules": [ "moonPhase" ],
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 10,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.08, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """;

        var failed = ContentPackLoader.TryLoad(WritePacks(json), new VitalRuleRegistry());
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Issues, issue => issue.Code == StateVitalsCodes.RuleUnknown);

        // Registering the rule makes the same pack valid: the check is real, not a ban.
        var registry = new VitalRuleRegistry().Register(new ConstantVitalRule("moonPhase", "core#hunger", 1));
        var loaded = ContentPackLoader.Load(WritePacks(json), registry);
        Assert.Equal(["moonPhase"], loaded.Rules);
    }

    [Fact]
    public void Effects_must_target_an_existing_vital()
    {
        var result = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ],
              "assertedStates": [ { "id": "injured" } ],
              "effects": [ { "id": "bad", "from": "injured", "to": "energy", "bias": -1, "stacking": "add" } ] }
            """);

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.EffectTargetUnknown);
    }

    [Fact]
    public void Every_effect_must_declare_its_own_stacking_rule()
    {
        var result = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ],
              "assertedStates": [ { "id": "injured" } ],
              "effects": [ { "id": "bad", "from": "injured", "to": "hunger", "bias": -1 } ] }
            """);

        var issue = Assert.Single(result.Issues.Where(candidate => candidate.Code == StateVitalsCodes.DefinitionEffectStackingMissing));
        Assert.Contains("stacking", issue.Suggestion!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multiplier_cannot_use_additive_stacking()
    {
        var result = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ],
              "assertedStates": [ { "id": "injured" } ],
              "effects": [ { "id": "bad", "from": "injured", "to": "hunger", "rateMultiplier": 1.4, "stacking": "add" } ] }
            """);

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DefinitionEffectStackingIncompatible);
    }

    [Fact]
    public void The_decay_rate_has_an_upper_bound()
    {
        var result = Load(VitalPack(
            """[ { "at": 0, "id": "a" } ]""",
            """{ "mode": "multiplicative", "ratePerHour": 99, "target": 100 }"""));

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DefinitionDecayRateOutOfRange);
    }

    [Fact]
    public void A_derived_state_must_reference_an_existing_vital_and_level()
    {
        var result = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" }, { "at": 50, "id": "b" } ] } ],
              "derivedStates": [
                { "id": "ghost", "vital": "core#nope", "levels": [ "a" ] },
                { "id": "wrongLevel", "vital": "core#hunger", "levels": [ "nope" ] } ] }
            """);

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DerivedVitalUnknown);
        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.DerivedLevelUnknown);
    }

    [Fact]
    public void A_trigger_may_only_assert_an_asserted_state()
    {
        var result = Load("""
            { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ],
              "triggers": [ { "id": "bad", "event": "x", "state": "core#hunger", "probability": 0.5 } ] }
            """);

        Assert.Contains(result.Issues, issue => issue.Code == StateVitalsCodes.TriggerStateNotAsserted);
    }

    [Fact]
    public void The_pack_version_and_schema_version_are_required()
    {
        var missingVersion = Load("""
            { "schemaVersion": 1, "packId": "core",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """);

        Assert.Contains(missingVersion.Issues, issue => issue.Code == StateVitalsCodes.PackVersionMissing);

        var futureSchema = Load("""
            { "schemaVersion": 9, "packId": "core", "version": "1.0.0",
              "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 0,
                            "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
                            "levels": [ { "at": 0, "id": "a" } ] } ] }
            """);

        Assert.Contains(futureSchema.Issues, issue => issue.Code == StateVitalsCodes.DefinitionSchemaVersionUnsupported);
    }

    // ---------------------------------------------------------------- persistence (§8)

    [Fact]
    public async Task The_plugin_writes_its_own_database_and_never_natter_db()
    {
        using var temp = new TempStateDirectory();
        var packsDirectory = Path.Combine(temp.Root, "packs");
        Directory.CreateDirectory(packsDirectory);
        await File.WriteAllTextAsync(Path.Combine(packsDirectory, "core.json"), CorePack());

        var clock = new ControlledClock(Epoch);
        var options = new StateVitalsOptions
        {
            Enabled = true,
            Packs = packsDirectory,
            StateDirectory = temp.StateDirectory,
            MinAdvanceIntervalSeconds = 0,
        };

        await using var runtime = StateVitalsRuntimeBuilder.Build(options, temp.Root, log: null, clock: clock);
        await runtime.InitializeAsync();
        await runtime.AdvanceAsync();
        await runtime.RenderAsync();

        var databasePath = Path.Combine(temp.StateDirectory, "state.db");
        Assert.True(File.Exists(databasePath), $"expected {databasePath}");
        Assert.False(File.Exists(Path.Combine(temp.StateDirectory, "natter.db")));

        var snapshot = await runtime.ReadSnapshotAsync();
        Assert.Equal(runtime.Packs!.Vitals.Count, snapshot!.Values.Count);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<double> ReadVitalAsync(StateVitalsRuntime runtime, string vitalId)
    {
        await runtime.AdvanceAsync();
        var snapshot = await runtime.ReadSnapshotAsync();
        return snapshot!.Values[vitalId];
    }

    private static StateCommit Commit(StateSnapshot expected, ContentPackSet packs, (string Id, double Value) value)
        => new(
            expected,
            Values(value),
            [],
            packs.DefinitionVersion,
            packs.PackVersions,
            expected.UpdatedAt,
            []);

    private static StateVitalsRuntime Runtime(
        ContentPackSet packs,
        IStateVitalsStore store,
        TimeProvider clock,
        IStateDescriptionGenerator? generator = null,
        StateVitalsOptions? options = null)
    {
        options ??= new StateVitalsOptions { Enabled = true };
        var descriptions = new StateDescriptionService(
            store,
            generator ?? new TemplateStateDescriptionGenerator(),
            clock,
            TimeSpan.FromMilliseconds(Math.Max(1, options.DescriptionTimeoutMilliseconds)));

        return new StateVitalsRuntime(options, packs, store, descriptions, clock);
    }

    private static IReadOnlyDictionary<string, double> Values(params (string Id, double Value)[] pairs)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in pairs)
            values[pair.Id] = pair.Value;

        return values;
    }

    private static IReadOnlyDictionary<string, double> SeedValues(ContentPackSet packs)
        => StateVitalsRuntimeBuilder.BaselineValues(packs);

    private static PromptRequest Request(
        string actor = "user-1",
        string conversation = "conv-1",
        string? guild = null,
        IReadOnlyDictionary<string, string>? variables = null)
        => new(
            new RuntimeProfile { Id = "tier1-default" },
            Source.Create("discord", "discord", "bot"),
            new Conversation(conversation, ConversationKind.Direct, GuildId: guild),
            new User(actor, "Alice"),
            Locale: null,
            Variables: variables ?? new Dictionary<string, string>());

    private static PromptFragmentContext Context()
        => PromptFragmentContexts.FromPromptRequest(Request(), PromptCompilerDefaults.StaticPresetId);

    /// <summary>
    /// Compiles a real plan so the assertion is about the host hash, not a local imitation. Plugin
    /// ids are namespaced here exactly like <c>HostPromptFragmentSource</c> does, because the
    /// provider (correctly) returns the plugin-local id.
    /// </summary>
    private static string PlanHash(IReadOnlyList<PromptFragment> pluginFragments)
    {
        var profile = new RuntimeProfile { Id = "tier1-default" };
        var preset = new PromptPreset(
            PromptCompilerDefaults.StaticPresetId,
            "1",
            [new PromptSectionRef("persona")]);
        var hostFragments = new[]
        {
            new PromptFragment("persona", "你是 Natter。", PromptFragmentSource.HostBuiltin, 0),
        };

        var namespaced = pluginFragments
            .Select(fragment => fragment with { Id = $"{StateVitalsDefaults.PluginId}#{fragment.Id}" })
            .ToArray();

        var plan = new PromptComposer().Compose(new PromptComposeRequest(
            profile,
            [preset],
            hostFragments,
            PresetId: PromptCompilerDefaults.StaticPresetId,
            PluginFragments: namespaced));

        return plan.Hash;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    /// <summary>Loads one pack file.</summary>
    private static PackLoadResult Load(string pack)
        => ContentPackLoader.TryLoad(WritePacks(pack), new VitalRuleRegistry());

    /// <summary>Loads several pack files (soft dependencies, cross-pack references).</summary>
    private static PackLoadResult LoadMulti(params string[] packs)
        => ContentPackLoader.TryLoad(WritePacks(packs), new VitalRuleRegistry());

    private static ContentPackSet Packs(params string[] packs)
    {
        var result = LoadMulti(packs);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString())));
        return result.Set!;
    }

    private static string WritePacks(params string[] packs)
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-state-vitals-packs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var index = 0;
        foreach (var pack in packs)
            File.WriteAllText(Path.Combine(directory, $"pack{index++}.json"), pack);

        TempDirectories.Add(directory);
        return directory;
    }

    private const string VitalBody =
        "\"range\": { \"min\": 0, \"max\": 100 }, \"baseline\": 10, "
        + "\"decay\": { \"mode\": \"multiplicative\", \"ratePerHour\": 0.08, \"target\": 100 }, ";

    private static string VitalPack(string levels, string? decay = null) => $$"""
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "vitals": [ { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 10,
                        "decay": {{decay ?? """{ "mode": "multiplicative", "ratePerHour": 0.08, "target": 100 }"""}},
                        "levels": {{levels}} } ] }
        """;

    private static string SimpleVital(string id, double min, double max) => $$"""
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "vitals": [ { "id": "{{id}}", "range": { "min": {{min.ToString(CultureInfo.InvariantCulture)}}, "max": {{max.ToString(CultureInfo.InvariantCulture)}} },
                        "baseline": 0, "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": {{max.ToString(CultureInfo.InvariantCulture)}} },
                        "levels": [ { "at": 0, "id": "a" }, { "at": {{(min + 50).ToString(CultureInfo.InvariantCulture)}}, "id": "b" } ] } ] }
        """;

    private static string HungerPack(double baseline, double target, double ratePerHour) => $$"""
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "vitals": [
            { "id": "hunger", "label": "饥饿", "range": { "min": 0, "max": 100 },
              "baseline": {{baseline.ToString("0.####", CultureInfo.InvariantCulture)}},
              "decay": { "mode": "multiplicative", "ratePerHour": {{ratePerHour.ToString("0.####", CultureInfo.InvariantCulture)}},
                         "target": {{target.ToString("0.####", CultureInfo.InvariantCulture)}} },
              "levels": [
                { "at": 0, "id": "sated", "template": "胃里是踏实的。" },
                { "at": 30, "id": "peckish", "template": "胃里轻轻响了一下。" },
                { "at": 60, "id": "hungry", "template": "肚子叫得有点明显。" },
                { "at": 85, "id": "starving", "semantic": "极度饥饿", "template": "饿得发慌。" } ] } ] }
        """;

    private static string NumericPack() => """
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "vitals": [
            { "id": "hunger", "range": { "min": 0, "max": 100 }, "baseline": 40,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.1, "target": 100 },
              "levels": [ { "at": 0, "id": "sated" }, { "at": 50, "id": "hungry" } ] },
            { "id": "energy", "range": { "min": 0, "max": 100 }, "baseline": 60,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 0 },
              "levels": [ { "at": 0, "id": "spent" }, { "at": 50, "id": "steady" } ] } ] }
        """;

    private static string SleepinessPack() => """
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "vitals": [
            { "id": "sleepiness", "range": { "min": 0, "max": 100 }, "baseline": 20,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.12,
                         "target": { "kind": "cycle", "min": 10, "max": 90, "periodHours": 24, "peakHourUtc": 3 } },
              "levels": [ { "at": 0, "id": "awake" }, { "at": 40, "id": "drowsy" } ] } ] }
        """;

    private static string CorePack() => """
        { "schemaVersion": 1, "packId": "core", "version": "1.0.0",
          "voice": { "prompt": "用第一人称短句描述。", "tone": "克制", "examples": [ "胃里空得发慌。" ], "banned": [ "系统" ] },
          "vitals": [
            { "abstract": true, "id": "baseNeed", "range": { "min": 0, "max": 100 }, "baseline": 0,
              "levels": [ { "at": 0, "id": "sated", "template": "胃里是踏实的。" },
                          { "at": 30, "id": "peckish", "template": "胃里轻轻响了一下。" },
                          { "at": 60, "id": "hungry", "template": "肚子叫得有点明显。" },
                          { "at": 85, "id": "starving", "semantic": "极度饥饿", "template": "饿得发慌。" } ] },
            { "id": "hunger", "parent": "baseNeed", "label": "饥饿", "baseline": 10,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.08, "target": 100 } },
            { "id": "energy", "label": "精力", "range": { "min": 0, "max": 100 }, "baseline": 70,
              "decay": { "mode": "multiplicative", "ratePerHour": 0.05, "target": 0 },
              "levels": [ { "at": 0, "id": "spent", "template": "整个人是空的。" },
                          { "at": 50, "id": "steady", "template": "状态还行。" } ] } ],
          "assertedStates": [ { "id": "injured", "label": "受伤", "template": "伤处一动就疼。", "durationHours": 72 } ],
          "derivedStates": [ { "id": "starving", "vital": "core#hunger", "levels": [ "starving" ], "label": "饿坏了" } ],
          "effects": [
            { "id": "starvingDrainsEnergy", "from": "starving", "to": "energy", "rateMultiplier": 1.4, "stacking": "mul" },
            { "id": "injurySlowsRecovery", "from": "injured", "to": "energy", "rateMultiplier": 0.6, "stacking": "mul", "priority": -10 } ],
          "triggers": [ { "id": "combatHit", "event": "combat.hit", "state": "injured", "probability": 0.35 } ] }
        """;

    private sealed class ControlledClock : TimeProvider
    {
        private DateTimeOffset _now;

        public ControlledClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class CountingDescriptionGenerator : IStateDescriptionGenerator
    {
        public int Calls { get; private set; }

        public ValueTask<StateDescriptionResult> GenerateAsync(
            StateDescriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new StateDescriptionResult(
                $"生成文本第{Calls}版：{request.Label}",
                StateDescriptionSources.Generated));
        }
    }

    private sealed class ThrowingDescriptionGenerator : IStateDescriptionGenerator
    {
        public ValueTask<StateDescriptionResult> GenerateAsync(
            StateDescriptionRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("model call failed");
    }

    private sealed class SlowDescriptionGenerator : IStateDescriptionGenerator
    {
        public async ValueTask<StateDescriptionResult> GenerateAsync(
            StateDescriptionRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new StateDescriptionResult("太慢了", StateDescriptionSources.Generated);
        }
    }

    private sealed class BannedWordDescriptionGenerator : IStateDescriptionGenerator
    {
        public ValueTask<StateDescriptionResult> GenerateAsync(
            StateDescriptionRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StateDescriptionResult(
                "系统提示：你的饥饿度已经很高了。",
                StateDescriptionSources.Generated));
    }

    private sealed class ConstantVitalRule(string name, string vitalId, double amount) : IVitalRule
    {
        public string Name => name;

        public ValueTask<IReadOnlyList<VitalForce>> EvaluateAsync(
            VitalRuleContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<VitalForce>>(
                [new VitalForce(vitalId, 0, VitalForceKind.Delta, amount)]);
    }

    private sealed class GatedStore(IStateVitalsStore inner, CommitGate gate) : IStateVitalsStore
    {
        public ValueTask<StateSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => inner.ReadAsync(cancellationToken);

        public async ValueTask<StateCommitResult?> TryCommitAsync(StateCommit commit, CancellationToken cancellationToken = default)
        {
            await gate.BlockOnceAsync(cancellationToken);
            return await inner.TryCommitAsync(commit, cancellationToken);
        }

        public ValueTask<StoredDescription?> GetDescriptionAsync(string stateId, long transitionSeq, CancellationToken cancellationToken = default)
            => inner.GetDescriptionAsync(stateId, transitionSeq, cancellationToken);

        public ValueTask<StoredDescription?> FindDescriptionAsync(string stateId, string definitionId, string levelId, CancellationToken cancellationToken = default)
            => inner.FindDescriptionAsync(stateId, definitionId, levelId, cancellationToken);

        public ValueTask<StoredDescription> SaveDescriptionAsync(StoredDescription description, CancellationToken cancellationToken = default)
            => inner.SaveDescriptionAsync(description, cancellationToken);

        public ValueTask<IReadOnlyList<StateEventRecord>> ReadTransitionsAsync(string stateId, int limit, CancellationToken cancellationToken = default)
            => inner.ReadTransitionsAsync(stateId, limit, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CommitGate
    {
        private readonly SemaphoreSlim _release = new(0, 1);
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private int _blocked;

        public Task Waiting => _waiting.Task;

        public bool IsWaiting => _blocked == 1;

        public async Task BlockOnceAsync(CancellationToken cancellationToken)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (_blocked == 1)
                    return;

                _blocked = 1;
            }
            finally
            {
                _mutex.Release();
            }

            _waiting.TrySetResult();
            await _release.WaitAsync(cancellationToken);
        }

        public void Release() => _release.Release();
    }

    private sealed class TempStateDirectory : IDisposable
    {
        public TempStateDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "natter-state-vitals-tests", Guid.NewGuid().ToString("N"));
            StateDirectory = Path.Combine(Root, "plugins", StateVitalsDefaults.PluginId);
            Directory.CreateDirectory(StateDirectory);
        }

        public string Root { get; }

        public string StateDirectory { get; }

        public void Dispose()
        {
            TempDirectories.Delete(Root);
        }
    }

    private static class TempDirectories
    {
        private static readonly List<string> Paths = [];

        public static void Add(string path)
        {
            lock (Paths)
                Paths.Add(path);
        }

        public static void Delete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
