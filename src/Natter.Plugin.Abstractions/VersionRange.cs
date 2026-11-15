using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Natter.Plugin.Abstractions;

/// <summary>
/// SemVer range subset used by NPS v1: exact versions, comparators, AND within a clause
/// and <c>||</c> unions between clauses. A prerelease candidate only matches a clause
/// that explicitly mentions a prerelease version.
/// </summary>
public sealed class VersionRange
{
    private static readonly Regex ComparatorPattern = new(
        @"^(?<op>>=|<=|>|<|=)?\s*(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private readonly IReadOnlyList<IReadOnlyList<Comparator>> _clauses;

    private VersionRange(string text, IReadOnlyList<IReadOnlyList<Comparator>> clauses)
    {
        Text = text;
        _clauses = clauses;
    }

    public string Text { get; }

    public static bool TryParse(string? value, out VersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var clauses = new List<IReadOnlyList<Comparator>>();

        foreach (var rawClause in text.Split("||", StringSplitOptions.TrimEntries))
        {
            if (rawClause.Length == 0)
                return false;

            var comparators = new List<Comparator>();
            if (rawClause is "*")
            {
                clauses.Add(comparators);
                continue;
            }

            var tokens = rawClause.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index];
                if (token is ">=" or "<=" or ">" or "<" or "=")
                {
                    if (index + 1 >= tokens.Length)
                        return false;
                    token += tokens[++index];
                }

                var match = ComparatorPattern.Match(token);
                if (!match.Success)
                    return false;

                if (!SemanticVersion.TryParse(match.Groups["version"].Value, out var version))
                    return false;

                comparators.Add(new Comparator(match.Groups["op"].Success ? match.Groups["op"].Value : "=", version!));
            }

            if (comparators.Count == 0)
                return false;

            clauses.Add(comparators);
        }

        if (clauses.Count == 0)
            return false;

        range = new VersionRange(text, clauses);
        return true;
    }

    public static VersionRange Parse(string value)
        => TryParse(value, out var range)
            ? range!
            : throw new FormatException($"'{value}' is not a valid SemVer range.");

    public bool IsSatisfiedBy(SemanticVersion? version)
    {
        if (version is null)
            return false;

        foreach (var clause in _clauses)
        {
            if (clause.Count == 0)
                return true;

            var clauseMatchesPrerelease = clause.Any(comparator => comparator.Version.IsPrerelease);
            if (version.IsPrerelease && !clauseMatchesPrerelease)
                continue;

            if (clause.All(comparator => comparator.Matches(version)))
                return true;
        }

        return false;
    }

    public override string ToString() => Text;

    private sealed record Comparator(string Operator, SemanticVersion Version)
    {
        public bool Matches(SemanticVersion candidate)
        {
            var comparison = candidate.CompareTo(Version);
            return Operator switch
            {
                "=" => comparison == 0,
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => false,
            };
        }
    }
}
