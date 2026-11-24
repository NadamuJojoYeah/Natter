using System.Text;
using System.Text.Json;
using Natter.Cli.Abstractions;
using Natter.Plugin.StateVitals.Definitions;
using Natter.Plugin.StateVitals.Kernel;
using Natter.Plugin.StateVitals.Rendering;

namespace Natter.Plugin.StateVitals.Cli;

/// <summary>
/// <c>natter states validate [--json]</c>: loads every pack and reports all configuration errors
/// plus the definitions skipped because a soft dependency is missing (design §10.2). No hot reload —
/// this is the cheap alternative that lets an author see the result immediately.
/// </summary>
public sealed class StatesValidateCommand(StateVitalsRuntime runtime, string packsDirectory) : ICliCommandHandler
{
    private readonly StateVitalsRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly string _packsDirectory = packsDirectory ?? throw new ArgumentNullException(nameof(packsDirectory));

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var options = new PackLoadOptions(
            _runtime.Options.MaxPacks,
            _runtime.Options.MaxDefinitionsPerKind,
            _runtime.Options.MaxDefinitionsPerKind,
            _runtime.Options.MaxDefinitionsPerKind,
            _runtime.Options.MaxEffectsPerPack,
            _runtime.Options.MaxDefinitionsPerKind);

        var result = ContentPackLoader.TryLoad(_packsDirectory, rules: null, options);

        if (invocation.HasFlag("json"))
        {
            var payload = new
            {
                ok = result.Succeeded,
                packsDirectory = _packsDirectory,
                definitionVersion = result.Set?.DefinitionVersion,
                packs = result.Set?.Packs.Select(pack => new { pack.PackId, pack.Version }).ToArray() ?? [],
                issueCount = result.Issues.Count,
                issues = result.Issues.Select(issue => new
                {
                    issue.Source,
                    issue.Pointer,
                    issue.Code,
                    issue.Message,
                    issue.Suggestion,
                }).ToArray(),
                skipped = result.Skipped.Select(skip => new { skip.PackId, skip.DefinitionId, skip.Reason }).ToArray(),
            };

            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return ValueTask.FromResult(result.Succeeded ? CliExitCodes.Success : CliExitCodes.Failure);
        }

        var output = new StringBuilder();
        output.AppendLine($"packs: {_packsDirectory}");

        if (result.Set is not null)
        {
            output.AppendLine($"definitionVersion: {result.Set.DefinitionVersion}");
            output.AppendLine(
                $"vitals={result.Set.Vitals.Count} assertedStates={result.Set.AssertedStates.Count} "
                + $"derivedStates={result.Set.DerivedStates.Count} effects={result.Set.Effects.Count} triggers={result.Set.Triggers.Count}");
        }

        if (result.Skipped.Count > 0)
        {
            output.AppendLine("skipped (soft dependency or disabled):");
            foreach (var skip in result.Skipped)
                output.AppendLine($"  {skip.PackId} {skip.DefinitionId}: {skip.Reason}");
        }

        if (result.Issues.Count > 0)
        {
            output.AppendLine($"errors: {result.Issues.Count}");
            foreach (var issue in result.Issues)
                output.AppendLine(issue.ToString());
        }
        else
        {
            output.AppendLine("errors: 0");
        }

        invocation.Console.Out.WriteLine(output.ToString().TrimEnd());
        return ValueTask.FromResult(result.Succeeded ? CliExitCodes.Success : CliExitCodes.Failure);
    }
}

/// <summary>
/// <c>natter states explain &lt;id&gt; [--json]</c>: the resolved definition, the current value and
/// bucket, and the fragment body it renders to (design §10.2). This is an author tool, so the
/// rendered text is printed on purpose; it never enters the host log.
/// </summary>
public sealed class StatesExplainCommand(StateVitalsRuntime runtime) : ICliCommandHandler
{
    private readonly StateVitalsRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var requested = invocation.Positionals.Count > 0 ? invocation.Positionals[0] : null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            invocation.Console.Error.WriteLine("usage: natter states explain <id>");
            return CliExitCodes.Usage;
        }

        var packs = _runtime.Packs;
        if (packs is null)
        {
            invocation.Console.Out.WriteLine(
                "state-vitals is not enabled (config enabled=false), so no definition is loaded.");
            return CliExitCodes.Failure;
        }

        var vital = packs.FindVital(requested) ?? packs.FindVital($"core#{requested}");
        var asserted = packs.FindAsserted(requested) ?? packs.FindAsserted($"core#{requested}");
        var derived = packs.FindDerived(requested) ?? packs.FindDerived($"core#{requested}");

        if (vital is null && asserted is null && derived is null)
        {
            invocation.Console.Error.WriteLine($"unknown definition '{requested}'");
            return CliExitCodes.Failure;
        }

        var snapshot = await _runtime.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var view = await _runtime.GetViewAsync(cancellationToken).ConfigureAwait(false);
        var fragment = await _runtime.RenderAsync(cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            var payload = new
            {
                definitionVersion = packs.DefinitionVersion,
                packVersions = packs.PackVersions,
                vitalId = vital?.Id,
                vitalLevels = vital?.Levels.Select(level => new { level.Id, level.At }).ToArray(),
                vitalRange = vital is null ? null : new { vital.Range.Min, vital.Range.Max },
                vitalDecay = vital is null
                    ? null
                    : new { ratePerHour = vital.Decay.RatePerHour, mode = vital.Decay.Mode.ToString() },
                assertedStateId = asserted?.Id,
                derivedStateId = derived?.Id,
                derivedFrom = derived?.VitalId,
                derivedLevels = derived?.Levels,
                effects = packs.Effects
                    .Where(effect => (vital is not null && effect.ToVitalId == vital.Id)
                                     || (asserted is not null && effect.FromStateId == asserted.Id)
                                     || (derived is not null && effect.FromStateId == derived.Id))
                    .Select(effect => new { effect.Id, effect.FromStateId, effect.ToVitalId, effect.Amount, stacking = effect.Rule.ToString() })
                    .ToArray(),
                currentValue = vital is null || snapshot is null ? (double?)null : snapshot.Values[vital.Id],
                currentLevel = vital is null || view is null ? null : view.Vitals.FirstOrDefault(entry => entry.VitalId == vital.Id)?.LevelId,
                assertedActive = asserted is null || snapshot is null
                    ? (bool?)null
                    : snapshot.AssertedStates.Any(state => state.StateId == asserted.Id),
                fragment,
            };

            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return CliExitCodes.Success;
        }

        var output = new StringBuilder();
        output.AppendLine($"definitionVersion: {packs.DefinitionVersion}");

        if (vital is not null)
        {
            output.AppendLine($"vital {vital.Id}");
            output.AppendLine($"  range [{vital.Range.Min}, {vital.Range.Max}] baseline={vital.Baseline} ratePerHour={vital.Decay.RatePerHour}");
            output.AppendLine($"  levels: {string.Join(", ", vital.Levels.Select(level => $"{level.Id}@{level.At}"))}");
        }

        if (asserted is not null)
        {
            output.AppendLine($"assertedState {asserted.Id} duration={asserted.Duration?.ToString() ?? "permanent"}");
            if (snapshot is not null)
            {
                var instance = snapshot.AssertedStates.FirstOrDefault(state => state.StateId == asserted.Id);
                output.AppendLine($"  active={instance is not null} expiresAt={instance?.ExpiresAt?.ToString("O") ?? "-"}");
            }
        }

        if (derived is not null)
            output.AppendLine($"derivedState {derived.Id} from {derived.VitalId} levels={string.Join(",", derived.Levels)}");

        if (vital is not null && snapshot is not null && snapshot.Values.TryGetValue(vital.Id, out var value))
        {
            var level = view?.Vitals.FirstOrDefault(entry => entry.VitalId == vital.Id);
            output.AppendLine($"  current {value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ({level?.LevelId ?? "?"})");
        }

        output.AppendLine("fragment:");
        output.AppendLine(string.IsNullOrWhiteSpace(fragment) ? "  (empty)" : fragment);

        invocation.Console.Out.WriteLine(output.ToString().TrimEnd());
        return CliExitCodes.Success;
    }
}
