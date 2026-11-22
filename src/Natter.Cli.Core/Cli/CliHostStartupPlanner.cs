using Natter.Cli.Abstractions;
using Natter.Cli.Core.Commands;

namespace Natter.Cli.Core;

/// <summary>Exit codes specific to host startup decisions (kept next to <see cref="CliExitCodes"/>).</summary>
public static class CliHostStartupCodes
{
    /// <summary>
    /// A command that needs a running host was refused because another instance holds the
    /// per-state-directory lock. Distinct from <see cref="CliExitCodes.Failure"/> so scripts can
    /// tell "the host is busy" apart from "the command failed".
    /// </summary>
    public const int HostBusy = 3;

    /// <summary>The state directory cannot be created/opened (permissions, read-only rootfs, bad path).</summary>
    public const int StateDirectoryUnusable = 5;
}

/// <summary>The startup decision for one invocation.</summary>
public sealed record CliHostStartupPlan(
    string? CommandName,
    CliHostRequirement Requirement,
    bool StartHost,
    bool TakeInstanceLock,
    string? Reason,
    string? Note)
{
    /// <summary>True when the command wanted runtime state but was asked to run without a host.</summary>
    public bool Degraded => Requirement == CliHostRequirement.Runtime && !StartHost;
}

/// <summary>
/// Decides whether an invocation may start a host and take the instance lock.
///
/// The decision is data driven: the command path is matched against the built-in command catalog
/// plus the declared <see cref="CliHostRequirementCatalog"/> rules. Nothing here resolves services
/// from the container — resolving the command registry before the host starts would freeze it while
/// plugins are still inactive (plugin commands are registered during activation).
/// </summary>
public static class CliHostStartupPlanner
{
    /// <summary>Global flag that forces every command to run without a host.</summary>
    public const string NoHostFlag = "--no-host";

    /// <summary>Command-level flag (currently on `health`) with the same meaning.</summary>
    public const string OfflineFlag = "--offline";

    private static readonly string[] BuiltInCommandNames = BuiltInCommands.Create()
        .Select(descriptor => descriptor.Name)
        .ToArray();

    public static CliHostStartupPlan Plan(
        IReadOnlyList<string> args,
        CliHostRequirementCatalog catalog,
        NatterCliOptions options,
        bool noHostRequested = false)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            // Bare invocation / global help: the runner prints usage, nothing to start.
            return new CliHostStartupPlan(null, CliHostRequirement.None, false, false, null, null);
        }

        // S2: `--no-host` / `--offline` must be honoured before the unknown-command fallback below,
        // otherwise every plugin command (e.g. `web status`) bypasses it. Plugin commands are not in
        // the catalog until activation, so ResolveCommandName returns null for them and the early
        // return used to skip the offline branch entirely - which made it impossible to inspect a
        // running instance with a plugin command, the exact case the flag exists for.
        var offlineRequestedEarly = noHostRequested
                                    || options.NoHost
                                    || args.Contains(NoHostFlag, StringComparer.Ordinal)
                                    || args.Contains(OfflineFlag, StringComparer.Ordinal);

        var commandName = ResolveCommandName(args, catalog);
        if (commandName is null)
        {
            // Not built in and not declared: it can only come from a plugin, and plugin commands are
            // registered while the host activates. Start it so the command has a chance to exist; if
            // nothing provides it, the runner reports "Unknown command" right afterwards.
            if (offlineRequestedEarly)
            {
                var earlyFlag = noHostRequested
                                || options.NoHost
                                || args.Contains(NoHostFlag, StringComparer.Ordinal)
                    ? NoHostFlag
                    : OfflineFlag;
                return new CliHostStartupPlan(
                    args[0],
                    CliHostRequirement.Runtime,
                    StartHost: false,
                    TakeInstanceLock: false,
                    Reason: "command is not built in; plugin commands are registered during activation",
                    Note: $"note: '{args[0]}' normally needs a running host (it may come from a plugin); "
                          + $"running without one because {earlyFlag} was given — live state will be unavailable.");
            }

            return RuntimePlan(args[0], "command is not built in; plugin commands are registered during activation");
        }

        catalog.TryGet(commandName, out var rule);
        var requirement = rule?.Requirement ?? CliHostRequirement.None;

        // The caller strips the global flag before planning (it is not a command argument), so it
        // arrives as `noHostRequested`; `--offline` stays on the command because `health` owns it.
        var offlineRequested = noHostRequested
                               || options.NoHost
                               || args.Contains(NoHostFlag, StringComparer.Ordinal)
                               || args.Contains(OfflineFlag, StringComparer.Ordinal);

        if (requirement != CliHostRequirement.Runtime)
        {
            return new CliHostStartupPlan(
                commandName,
                requirement,
                StartHost: false,
                TakeInstanceLock: false,
                Reason: "command answers from configuration/state and never starts a host",
                Note: null);
        }

        if (offlineRequested)
        {
            var flag = noHostRequested
                       || options.NoHost
                       || args.Contains(NoHostFlag, StringComparer.Ordinal)
                ? NoHostFlag
                : OfflineFlag;
            return new CliHostStartupPlan(
                commandName,
                requirement,
                StartHost: false,
                TakeInstanceLock: false,
                Reason: rule?.Why,
                Note: $"note: '{commandName}' needs a running host ({rule?.Why}); "
                      + $"running without one because {flag} was given — live state will be unavailable.");
        }

        return RuntimePlan(commandName, rule?.Why);
    }

    /// <summary>Writes the refusal message used when another instance owns the state directory.</summary>
    public static void WriteHostBusyMessage(IConsole console, CliHostStartupPlan plan, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(plan);

        console.Error.WriteLine(
            $"error: HL-CLI-HOST-BUSY: another Natter instance is using state directory '{stateDirectory}'.");
        console.Error.WriteLine(
            $"       '{plan.CommandName}' needs a running host ({plan.Reason}), and starting a second host "
            + "would open a second gateway session (one of them would be disconnected by Discord).");
        console.Error.WriteLine("       options:");
        console.Error.WriteLine("         - inspect the running instance: curl -s http://127.0.0.1:5099/health | jq");
        console.Error.WriteLine($"         - run this command without a host: natter {plan.CommandName} {NoHostFlag}"
                                + "   (output is degraded, live state is unavailable)");
        console.Error.WriteLine("         - or stop the running instance first: sudo systemctl stop natter");
    }

    private static CliHostStartupPlan RuntimePlan(string commandName, string? why)
        => new(commandName, CliHostRequirement.Runtime, StartHost: true, TakeInstanceLock: true, Reason: why, Note: null);

    /// <summary>Longest match between the argument prefix and a known command path.</summary>
    private static string? ResolveCommandName(IReadOnlyList<string> args, CliHostRequirementCatalog catalog)
    {
        string? best = null;
        var bestTokens = 0;

        foreach (var name in BuiltInCommandNames.Concat(catalog.RuleNames))
        {
            var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > args.Count || tokens.Length <= bestTokens)
                continue;

            var matches = true;
            for (var index = 0; index < tokens.Length; index++)
            {
                if (!string.Equals(tokens[index], args[index], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
                continue;

            best = name;
            bestTokens = tokens.Length;
        }

        return best;
    }
}
