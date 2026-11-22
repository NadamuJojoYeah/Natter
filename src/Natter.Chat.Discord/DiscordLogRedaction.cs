using System.Text.RegularExpressions;

namespace Natter.Chat.Discord;

/// <summary>
/// Redaction rules shared by Discord transport/upstream logs and by the disconnect reason that
/// reaches <c>/health</c>. Discord.Net exception text can echo request URLs, websocket endpoints
/// and bot authorization headers, so every string that leaves this assembly runs through
/// <see cref="Redact"/> first. The bot token itself is also scrubbed by literal match.
/// </summary>
public static class DiscordLogRedaction
{
    private static readonly Regex UrlCredentials = new(
        @"\b(https?://)([^/@\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BotAuthorization = new(
        @"\bBot\s+[A-Za-z0-9._\-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SecretAssignment = new(
        @"(?<key>(?:token|authorization|bearer|api[_-]?key|secret|password|passwd)\s*[=:]\s*)(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes credentials from a diagnostic string. <paramref name="secret"/> is an optional
    /// known secret (the bot token) replaced literally so it can never leak through a template.
    /// </summary>
    public static string Redact(string? value, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        var result = UrlCredentials.Replace(value, "$1***@");
        result = BotAuthorization.Replace(result, "Bot ***");
        result = SecretAssignment.Replace(result, "${key}***");

        if (!string.IsNullOrEmpty(secret))
            result = result.Replace(secret, "***", StringComparison.Ordinal);

        return result;
    }
}
