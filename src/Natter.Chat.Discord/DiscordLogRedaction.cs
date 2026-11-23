using Natter.Application.Redaction;

namespace Natter.Chat.Discord;

/// <summary>
/// Redaction rules shared by Discord transport/upstream logs and by the disconnect reason that
/// reaches <c>/health</c>. Discord.Net exception text can echo request URLs, websocket endpoints
/// and bot authorization headers, so every string that leaves this assembly runs through
/// <see cref="Redact"/> first. The bot token itself is also scrubbed by literal match.
/// </summary>
public static class DiscordLogRedaction
{
    /// <summary>
    /// C-03: shared rules (assignment pairs, literal keys, PEM) + Discord-only rules (URL credentials,
    /// Bot header), with the <c>***</c> marker kept so existing log formats/tests do not change.
    /// This class used to run unbounded regexes over whole log lines; the shared core now caps the
    /// scan at 64 KiB and gives every regex a one-second timeout.
    /// </summary>
    public static string Redact(string? value, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value ?? string.Empty;

        var options = secret is { Length: > 0 }
            ? SecretRedactionOptions.Discord with { KnownSecret = secret }
            : SecretRedactionOptions.Discord;

        return SecretRedaction.Apply(value, options);
    }
}
