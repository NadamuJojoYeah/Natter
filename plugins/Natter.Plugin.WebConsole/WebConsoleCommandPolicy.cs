using System.Text;
using System.Text.Json;

namespace Natter.Plugin.WebConsole;

public sealed record WebConsoleCommandRequest(string Command, IReadOnlyList<string> Args)
{
    public static WebConsoleCommandRequest Empty { get; } = new(string.Empty, []);
}

public sealed record WebConsoleCommandDecision(
    bool Allowed,
    string Code,
    string Message,
    string Command,
    IReadOnlyList<string> Args);

public sealed record WebConsoleCommandOutput(
    string Stdout,
    string Stderr,
    bool Truncated,
    int OriginalBytes);

/// <summary>
/// Pure allowlist/argument/output policy for the restricted command console. Execution stays behind
/// the host-side <c>IAdminCommandRunner</c> (gap G6); this class has no process/shell access.
/// </summary>
public static class WebConsoleCommandPolicy
{
    public const int MaxCommandLength = 128;
    public const int MaxArgs = 8;
    public const int MaxArgLength = 128;
    public const int MaxOutputBytes = 64 * 1024;

    public static WebConsoleCommandDecision Evaluate(
        WebConsoleCommandRequest request,
        IReadOnlyList<string> allowlist)
    {
        if (!TryNormalize(request, out var command, out var args, out var error))
            return new WebConsoleCommandDecision(false, "command.invalid", error, command, args);

        var normalizedAllowlist = NormalizeAllowlist(allowlist);
        if (normalizedAllowlist.Count == 0)
        {
            return new WebConsoleCommandDecision(
                false,
                "command.allowlist_empty",
                "commandAllowlist is empty; no command can run.",
                command,
                args);
        }

        if (!normalizedAllowlist.Contains(command, StringComparer.Ordinal))
        {
            return new WebConsoleCommandDecision(
                false,
                "command.not_allowed",
                $"Command '{command}' is not in commandAllowlist.",
                command,
                args);
        }

        return new WebConsoleCommandDecision(true, "command.allowed", "Command is allowed.", command, args);
    }

    public static bool TryParseRequest(string body, out WebConsoleCommandRequest request, out string? error)
    {
        request = WebConsoleCommandRequest.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            error = "Command body must be a JSON object with a command field.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("command", out var commandElement)
                || commandElement.ValueKind != JsonValueKind.String)
            {
                error = "Command body must be a JSON object with a command field.";
                return false;
            }

            var command = commandElement.GetString() ?? string.Empty;
            var args = new List<string>();

            if (root.TryGetProperty("args", out var argsElement))
            {
                if (argsElement.ValueKind != JsonValueKind.Array)
                {
                    error = "args must be an array of strings.";
                    return false;
                }

                foreach (var item in argsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        error = "args must be an array of strings.";
                        return false;
                    }

                    args.Add(item.GetString() ?? string.Empty);
                }
            }

            request = new WebConsoleCommandRequest(command, args);
            return true;
        }
        catch (JsonException exception)
        {
            error = $"Command body is not valid JSON: {exception.Message}";
            return false;
        }
    }

    public static WebConsoleCommandOutput NormalizeOutput(string? stdout, string? stderr)
    {
        var safeOut = WebConsoleRedaction.RedactSecrets(stdout ?? string.Empty);
        var safeErr = WebConsoleRedaction.RedactSecrets(stderr ?? string.Empty);
        var originalBytes = Encoding.UTF8.GetByteCount(safeOut) + Encoding.UTF8.GetByteCount(safeErr);

        var remaining = MaxOutputBytes;
        var truncated = false;

        var output = TruncateUtf8(safeOut, ref remaining, ref truncated);
        var error = TruncateUtf8(safeErr, ref remaining, ref truncated);

        return new WebConsoleCommandOutput(output, error, truncated, originalBytes);
    }

    public static string NormalizeCommand(string? command)
        => string.Join(' ', (command ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool TryNormalize(
        WebConsoleCommandRequest request,
        out string command,
        out IReadOnlyList<string> args,
        out string error)
    {
        var rawCommand = request.Command ?? string.Empty;
        command = NormalizeCommand(rawCommand);
        args = request.Args ?? [];
        error = string.Empty;

        if (ContainsControlCharacter(rawCommand))
        {
            error = "command contains control characters.";
            return false;
        }

        if (command.Length == 0)
        {
            error = "command must not be empty.";
            return false;
        }

        if (command.Length > MaxCommandLength)
        {
            error = $"command exceeds {MaxCommandLength} characters.";
            return false;
        }

        if (ContainsControlCharacter(command))
        {
            error = "command contains control characters.";
            return false;
        }

        if (args.Count > MaxArgs)
        {
            error = $"args exceeds {MaxArgs} items.";
            return false;
        }

        foreach (var argument in args)
        {
            if (argument is null || argument.Length > MaxArgLength)
            {
                error = $"each arg must be at most {MaxArgLength} characters.";
                return false;
            }

            if (ContainsControlCharacter(argument))
            {
                error = "args contain control characters.";
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> NormalizeAllowlist(IReadOnlyList<string>? allowlist)
    {
        if (allowlist is null)
            return [];

        return allowlist
            .Select(NormalizeCommand)
            .Where(command => command.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ContainsControlCharacter(string value)
        => value.Any(character => char.IsControl(character));

    private static string TruncateUtf8(string value, ref int remainingBytes, ref bool truncated)
    {
        if (value.Length == 0)
            return value;

        if (remainingBytes <= 0)
        {
            truncated = true;
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes <= remainingBytes)
        {
            remainingBytes -= bytes;
            return value;
        }

        truncated = true;
        const int ellipsisBytes = 3;
        var budget = remainingBytes - ellipsisBytes;
        if (budget <= 0)
        {
            remainingBytes = 0;
            return string.Empty;
        }

        var builder = new StringBuilder();
        var used = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (used + runeBytes > budget)
                break;

            builder.Append(rune.ToString());
            used += runeBytes;
        }

        remainingBytes = Math.Max(0, remainingBytes - used - ellipsisBytes);
        return builder.ToString() + "…";
    }
}
