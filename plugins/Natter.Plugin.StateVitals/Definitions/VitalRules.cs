using Natter.Plugin.StateVitals.Kernel;

namespace Natter.Plugin.StateVitals.Definitions;

/// <summary>Facts handed to a code-hook rule (design §9 layer 3).</summary>
public sealed record VitalRuleContext(
    string StateId,
    DateTimeOffset Now,
    DateTimeOffset Since,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, double> Values);

/// <summary>
/// Layer-3 extension point: JSON cannot express "compute a value from the last ten messages".
/// Rules are referenced by name from the definition file, and an unregistered name is a startup
/// error rather than a silent runtime skip.
/// </summary>
public interface IVitalRule
{
    string Name { get; }

    ValueTask<IReadOnlyList<VitalForce>> EvaluateAsync(
        VitalRuleContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Named rules available to a definition set. Empty by default: v1 ships no code hooks.</summary>
public sealed class VitalRuleRegistry
{
    private readonly Dictionary<string, IVitalRule> _rules = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _rules.Keys;

    public VitalRuleRegistry Register(IVitalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (string.IsNullOrWhiteSpace(rule.Name))
            throw new ArgumentException("A vital rule must have a name.", nameof(rule));

        _rules[rule.Name] = rule;
        return this;
    }

    public bool Contains(string name) => _rules.ContainsKey(name);

    public bool TryGet(string name, out IVitalRule rule) => _rules.TryGetValue(name, out rule!);
}
