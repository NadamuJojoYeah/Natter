namespace Natter.Cli.Core;

/// <summary>Whether a CLI command needs a running host (started graph, activated plugins, gateways).</summary>
public enum CliHostRequirement
{
    /// <summary>Answerable from configuration/state/plugin metadata alone; never starts a host.</summary>
    None = 0,

    /// <summary>Needs live runtime state (uptime, gateway connections, event stream, in-memory journal).</summary>
    Runtime = 1,
}

/// <summary>One declarative rule: this command needs (or does not need) a running host.</summary>
public sealed record CliHostRule(string CommandName, CliHostRequirement Requirement, string Why);

/// <summary>
/// Contributes host-requirement rules. Plugins may implement this to declare that their command
/// needs live state; without a rule a command is treated as <see cref="CliHostRequirement.None"/>,
/// which is the safe default (a diagnostic command must not open a second gateway session).
/// </summary>
public interface ICliHostRequirementProvider
{
    IEnumerable<CliHostRule> GetRules();
}

/// <summary>
/// Built-in rules. Only commands that genuinely need runtime state are listed here — every other
/// command (including plugin commands) stays offline by default, so `plugins list`, `doctor`,
/// `ports`, `plugins inspect` … cannot disturb a running instance.
/// </summary>
public sealed class DefaultCliHostRequirements : ICliHostRequirementProvider
{
    private static readonly (string Command, string Why)[] RuntimeCommands =
    [
        ("serve", "it runs the long-lived host"),
        ("health", "it reports live uptime/upstream/provider state (use 'health --offline' for a static view)"),
        ("upstreams", "it reports the live connection state of the upstreams (use '--no-host' for a static view)"),
        ("events list", "it reads the in-memory event journal, which only exists while a host runs"),
        ("events watch", "it subscribes to the live domain-event stream"),
        ("events clear", "it clears the in-memory event journal of a running host"),
        ("exec", "it runs several commands inside one live host"),
        ("demo", "it exercises the running host end to end"),
        ("discord status", "it reports the Discord gateway state and therefore needs the gateway"),
        // These are internal, but they read state that only exists after plugin activation or
        // after the host opened its stores. `plugins list`/`inspect`/`config validate` stay offline.
        ("plugins graph", "it walks the activated plugin dependency graph"),
        ("plugins contributions", "it lists contributions published by activated plugins"),
        ("plugins permissions", "it reports permissions granted during activation"),
        ("plugins doctor", "it diagnoses activated plugin activation"),
        ("memory list", "it reads the memory store opened by the host"),
        ("memory export", "it reads the memory store opened by the host"),
        ("memory import", "it writes to the memory store opened by the host"),
        ("remember", "it writes an explicit memory through the running store"),
        ("forget", "it deletes a memory through the running store"),
    ];

    public IEnumerable<CliHostRule> GetRules()
        => RuntimeCommands.Select(entry => new CliHostRule(entry.Command, CliHostRequirement.Runtime, entry.Why));
}

/// <summary>Merged rule table (built-in + plugin/provider contributions), case-insensitive.</summary>
public sealed class CliHostRequirementCatalog
{
    /// <summary>Origin value used by the built-in command catalog.</summary>
    public const string FrameworkOrigin = "framework:cli";

    private readonly Dictionary<string, CliHostRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public CliHostRequirementCatalog(IEnumerable<ICliHostRequirementProvider>? providers = null)
    {
        foreach (var provider in providers ?? [])
        {
            foreach (var rule in provider.GetRules())
            {
                if (string.IsNullOrWhiteSpace(rule.CommandName))
                    continue;

                // Later providers may refine a rule (e.g. a plugin command that truly needs runtime).
                _rules[rule.CommandName.Trim()] = rule;
            }
        }
    }

    public IReadOnlyList<CliHostRule> RuntimeRules
        => _rules.Values
            .Where(rule => rule.Requirement == CliHostRequirement.Runtime)
            .OrderBy(rule => rule.CommandName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Every declared command name (runtime and explicit none rules).</summary>
    public IReadOnlyList<string> RuleNames => _rules.Keys.ToArray();

    public bool TryGet(string commandName, out CliHostRule rule)
        => _rules.TryGetValue(commandName, out rule!);

    /// <summary>
    /// Plugin-contributed commands are registered by an activated plugin, so a command coming from
    /// any other origin needs the runtime even when nobody declared a rule for it.
    /// </summary>
    public CliHostRequirement GetRequirement(string? commandName, string? origin = null)
        => GetRequirement(commandName) == CliHostRequirement.Runtime
           || (origin is not null && !string.Equals(origin, FrameworkOrigin, StringComparison.OrdinalIgnoreCase))
            ? CliHostRequirement.Runtime
            : CliHostRequirement.None;

    public CliHostRequirement GetRequirement(string? commandName)
        => commandName is not null && _rules.TryGetValue(commandName, out var rule)
            ? rule.Requirement
            : CliHostRequirement.None;
}
