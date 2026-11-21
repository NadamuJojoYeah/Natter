namespace Natter.Application.Admin;

/// <summary>
/// Pure allowlist/argument policy for the restricted command console. The host runner re-checks
/// every rule before executing anything; this class has no process, shell or IO access.
/// </summary>
public static class AdminCommandPolicy
{
    public const int MaxCommandLength = 128;

    public const int MaxArgs = 8;

    public const int MaxArgLength = 128;

    public const int MaxOutputBytes = 64 * 1024;

    public const int DefaultTimeoutSeconds = 10;

    /// <summary>Collapses runs of whitespace so <c>"plugins  list"</c> matches <c>"plugins list"</c>.</summary>
    public static string NormalizeCommand(string? command)
        => string.Join(' ', (command ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Normalizes and validates one command invocation.</summary>
    public static bool TryValidate(
        string? command,
        IReadOnlyList<string>? args,
        out string normalizedCommand,
        out IReadOnlyList<string> normalizedArgs,
        out string code,
        out string message)
    {
        normalizedCommand = NormalizeCommand(command);
        normalizedArgs = args ?? [];
        code = AdminCodes.CommandInvalid;
        message = string.Empty;

        if (ContainsControlCharacter(command))
        {
            message = "command contains control characters.";
            return false;
        }

        if (normalizedCommand.Length == 0)
        {
            message = "command must not be empty.";
            return false;
        }

        if (normalizedCommand.Length > MaxCommandLength)
        {
            message = $"command exceeds {MaxCommandLength} characters.";
            return false;
        }

        if (normalizedArgs.Count > MaxArgs)
        {
            message = $"args exceeds {MaxArgs} items.";
            return false;
        }

        foreach (var argument in normalizedArgs)
        {
            if (argument is null || argument.Length > MaxArgLength)
            {
                message = $"each arg must be at most {MaxArgLength} characters.";
                return false;
            }

            if (ContainsControlCharacter(argument))
            {
                message = "args contain control characters.";
                return false;
            }
        }

        code = AdminCodes.CommandAllowed;
        return true;
    }

    /// <summary>True when the normalized command is present in the allowlist (exact match, ordinal).</summary>
    public static bool IsAllowed(string normalizedCommand, IEnumerable<string>? allowlist)
        => NormalizeAllowlist(allowlist).Contains(normalizedCommand, StringComparer.Ordinal);

    public static IReadOnlyList<string> NormalizeAllowlist(IEnumerable<string>? allowlist)
    {
        if (allowlist is null)
            return [];

        return allowlist
            .Select(NormalizeCommand)
            .Where(command => command.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(command => command, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Read-only diagnostics commands exposed by default; mutating commands stay opt-in.</summary>
    public static IReadOnlyList<string> DefaultAllowlist { get; } =
    [
        "doctor",
        "health",
        "memory list",
        "plugins contributions",
        "plugins doctor",
        "plugins graph",
        "plugins list",
        "plugins permissions",
        "ports",
        "upstreams",
        "version",
    ];

    private static bool ContainsControlCharacter(string? value)
        => value is not null && value.Any(char.IsControl);
}
