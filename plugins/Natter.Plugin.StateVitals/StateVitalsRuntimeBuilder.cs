using Natter.Plugin.Abstractions;
using Natter.Plugin.StateVitals.Definitions;
using Natter.Plugin.StateVitals.Descriptions;
using Natter.Plugin.StateVitals.Storage;

namespace Natter.Plugin.StateVitals;

/// <summary>
/// Assembles a runtime from configuration: resolves the content packs, opens the plugin's own
/// database and wires the description service. Kept separate from <see cref="StateVitalsPlugin"/>
/// so tests and the <c>states validate</c> command can build the same graph without a plugin context.
/// </summary>
public static class StateVitalsRuntimeBuilder
{
    public static StateVitalsRuntime Build(
        StateVitalsOptions options,
        string pluginDirectory,
        IPluginLog? log = null,
        TimeProvider? clock = null,
        VitalRuleRegistry? rules = null,
        IStateDescriptionGenerator? generator = null,
        IStateVitalsStore? store = null,
        string? packsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pluginDirectory);

        clock ??= TimeProvider.System;

        // Opt-in: a disabled plugin reads nothing, opens nothing and registers no fragment content.
        if (!options.Enabled)
            return StateVitalsRuntime.Disabled(options, clock, log);

        var resolvedPacks = packsDirectory ?? StateVitalsPaths.ResolvePacksDirectory(options, pluginDirectory);
        var packs = ContentPackLoader.Load(
            resolvedPacks,
            rules,
            new PackLoadOptions(
                options.MaxPacks,
                options.MaxDefinitionsPerKind,
                options.MaxDefinitionsPerKind,
                options.MaxDefinitionsPerKind,
                options.MaxEffectsPerPack,
                options.MaxDefinitionsPerKind));

        if (store is null)
        {
            var stateDirectory = StateVitalsPaths.ResolveStateDirectory(options);
            store = new SqliteStateVitalsStore(
                StateVitalsPaths.ResolveDatabasePath(stateDirectory),
                string.IsNullOrWhiteSpace(options.StateId) ? StateVitalsDefaults.StateId : options.StateId,
                BaselineValues(packs),
                packs.DefinitionVersion,
                packs.PackVersions,
                clock.GetUtcNow());
        }

        // First version ships no host AI port for `ai.complete`, so generation degrades to the
        // pack's template. The seam exists so wiring a model later changes one line.
        generator ??= new TemplateStateDescriptionGenerator();

        var descriptions = new StateDescriptionService(
            store,
            generator,
            clock,
            TimeSpan.FromMilliseconds(Math.Max(0, options.DescriptionTimeoutMilliseconds)),
            log);

        return new StateVitalsRuntime(options, packs, store, descriptions, clock, rules, log);
    }

    /// <summary>Seed snapshot: every vital starts at its declared baseline.</summary>
    public static IReadOnlyDictionary<string, double> BaselineValues(ContentPackSet packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var vital in packs.Vitals)
            values[vital.Id] = vital.Range.Clamp(vital.Baseline);

        return values;
    }
}
