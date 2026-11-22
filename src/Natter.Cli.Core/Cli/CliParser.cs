using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Parsed command line for one command.</summary>
public sealed record CliParseResult(
    IReadOnlyDictionary<string, string?> Options,
    IReadOnlyList<string> Positionals,
    bool ShowHelp,
    string? Error);

/// <summary>
/// Small dependency-free parser. Supports <c>--name value</c>, <c>--name=value</c>,
/// <c>-s value</c>, boolean flags and <c>--</c> to stop option parsing.
/// </summary>
public static class CliParser
{
    public static CliParseResult Parse(CliCommandDescriptor command, IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(tokens);

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var showHelp = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token is "--help" or "-h" or "-?")
            {
                showHelp = true;
                continue;
            }

            if (token == "--")
            {
                positionals.AddRange(tokens.Skip(index + 1));
                break;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var body = token[2..];
                var separator = body.IndexOf('=');
                var name = separator >= 0 ? body[..separator] : body;
                var value = separator >= 0 ? body[(separator + 1)..] : null;

                var option = command.Options.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

                if (option is null)
                {
                    // S2-E5: an undeclared option used to be accepted (and it swallowed the next
                    // positional argument), so a typo changed behaviour silently.
                    return Failure($"Unknown option '--{name}' for command '{command.Name}'.");
                }

                if (option.TakesValue && value is null)
                {
                    if (index + 1 >= tokens.Count)
                        return Failure($"Option '--{option.Name}' requires a value.");

                    value = tokens[++index];
                }

                options[option.Name] = option.TakesValue ? value : value ?? "true";
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal) && token.Length > 1)
            {
                var body = token[1..];
                var separator = body.IndexOf('=');
                var name = separator >= 0 ? body[..separator] : body;
                var value = separator >= 0 ? body[(separator + 1)..] : null;

                var option = command.Options.FirstOrDefault(candidate =>
                    string.Equals(candidate.ShortName, name, StringComparison.OrdinalIgnoreCase));

                if (option is null)
                {
                    options[name] = value ?? "true";
                    continue;
                }

                if (option.TakesValue && value is null)
                {
                    if (index + 1 >= tokens.Count)
                        return Failure($"Option '-{option.ShortName}' requires a value.");

                    value = tokens[++index];
                }

                options[option.Name] = option.TakesValue ? value : value ?? "true";
                continue;
            }

            positionals.Add(token);
        }

        if (showHelp)
            return new CliParseResult(options, positionals, true, null);

        foreach (var option in command.Options)
        {
            if (!options.ContainsKey(option.Name) && option.DefaultValue is not null)
                options[option.Name] = option.DefaultValue;
        }

        for (var argumentIndex = 0; argumentIndex < command.Arguments.Count; argumentIndex++)
        {
            var argument = command.Arguments[argumentIndex];
            if (argument.Required && argumentIndex >= positionals.Count)
                return Failure($"Missing required argument '<{argument.Name}>'.");
        }

        return new CliParseResult(options, positionals, showHelp, null);

        static CliParseResult Failure(string error)
            => new(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), [], false, error);
    }
}
