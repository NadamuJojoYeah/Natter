using System.Text;
using System.Text.RegularExpressions;

namespace Natter.Application.Redaction;

/// <summary>
/// One shared redaction core for every place that must not print a credential (C-03).
///
/// Why a core instead of one function: the call sites keep <em>different</em> specialised rules and
/// <em>different</em> replacement markers (audit/admin uses <c>[redacted]</c>, Discord logs use
/// <c>***</c>), and both are observable behaviour. Merging them into a single fixed function would
/// either change log formats or silently drop a rule. So the core owns the shared matching
/// (assignment pairs, literal keys, PEM blocks, known-secret literals) plus the shared safety
/// envelope (scan cap and regex timeout), and each caller opts into its specialised rules and its
/// own marker.
///
/// S2 lesson (OBS-WC-P3-REDACT-KV): a rule that exists in one copy and not the other is a plaintext
/// leak, so every behavioural rule lives here once and is exercised by one shared fixture.
/// </summary>
public sealed record SecretRedactionOptions
{
    /// <summary>Text written in place of a matched secret.</summary>
    public string Replacement { get; init; } = "[redacted]";

    /// <summary>Upper bound for one pass; longer values are truncated before any regex runs.</summary>
    public int MaxScanLength { get; init; } = 64 * 1024;

    /// <summary>Suffix appended when the input is truncated by <see cref="MaxScanLength"/>.</summary>
    public string TruncationMarker { get; init; } = "…(truncated)";

    /// <summary>Timeout for every regex in this class; a pathological line must not hang a thread.</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Keeps <c>${secret:NAME}</c> references verbatim (a reference is not a value).</summary>
    public bool PreserveSecretReferences { get; init; } = true;

    /// <summary>
    /// Redacts credentials embedded in URLs (<c>https://user:pass@host</c>). Shared on purpose:
    /// admin output used to leak the password part while Discord scrubbed it (fixture finding).
    /// </summary>
    public bool RedactUrlCredentials { get; init; } = true;

    /// <summary>
    /// Redacts <c>Authorization: Bot &lt;token&gt;</c> headers. Shared on purpose: the admin pair
    /// rule used to consume the word "Bot" only and leave the token behind (fixture finding).
    /// </summary>
    public bool RedactBotAuthorization { get; init; } = true;

    /// <summary>Also replaces an exact known secret (for example the bot token) literally.</summary>
    public string? KnownSecret { get; init; }

    /// <summary>Assignment keys whose values are always sensitive.</summary>
    public IReadOnlySet<string> SensitiveKeys { get; init; } = SecretRedaction.DefaultSensitiveKeys;

    /// <summary>Admin/audit flavour: shared rules only, <c>[redacted]</c> marker.</summary>
    public static SecretRedactionOptions Admin { get; } = new();

    /// <summary>Discord log flavour: same shared rules, <c>***</c> marker (existing log format).</summary>
    public static SecretRedactionOptions Discord { get; } = new()
    {
        Replacement = "***",
    };
}

/// <summary>Single implementation of the redaction rules shared by admin, Discord logs and WebConsole.</summary>
public static class SecretRedaction
{
    public const int DefaultMaxScanLength = 64 * 1024;

    private static readonly string[] DefaultKeyNames =
    [
        "token",
        "secret",
        "password",
        "passwd",
        "pwd",
        "api[-_]?key",
        "apikey",
        "api_key",
        "authorization",
        "bearer",
        "cookie",
        "credentials?",
        "private[-_]?key",
        "connection[-_]?string",
        "client[-_]?secret",
        "app[-_]?secret",
    ];

    /// <summary>Default sensitive assignment keys (word shapes, not regexes).</summary>
    public static IReadOnlySet<string> DefaultSensitiveKeys { get; } = new HashSet<string>(
        [
            "token", "secret", "password", "passwd", "pwd", "apikey", "api_key", "api-key",
            "authorization", "bearer", "cookie", "credential", "credentials", "private_key",
            "private-key", "connection_string", "connection-string", "client_secret",
            "client-secret", "app_secret", "app-secret",
        ],
        StringComparer.OrdinalIgnoreCase);

    // Every pattern is created per call: the timeout is configuration, and a static Regex cannot
    // carry a per-caller value. Compilation cost is irrelevant next to the I/O these callers do.
    private static Regex Timeout(string pattern, TimeSpan timeout, RegexOptions options = RegexOptions.None)
        => new(pattern, options | RegexOptions.CultureInvariant, timeout);

    /// <summary>
    /// Applies the shared rules (and any specialised rule enabled by <paramref name="options"/>).
    /// Never throws on oversized or hostile input: the scan is capped and every regex has a timeout.
    /// </summary>
    public static string Apply(string? value, SecretRedactionOptions? options = null)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var settings = options ?? SecretRedactionOptions.Admin;
        var timeout = settings.RegexTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : settings.RegexTimeout;
        var replacement = string.IsNullOrEmpty(settings.Replacement) ? "[redacted]" : settings.Replacement;
        var limit = settings.MaxScanLength <= 0 ? DefaultMaxScanLength : settings.MaxScanLength;

        var working = value.Length > limit
            ? value[..limit] + settings.TruncationMarker
            : value;

        // 1. PEM blocks first: the body carries no key name, so no assignment rule can see it.
        working = Timeout("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
                timeout, RegexOptions.Singleline)
            .Replace(working, replacement);
        // 1b. A truncated PEM (no END marker) still must not leak the body.
        working = Timeout("-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*$", timeout)
            .Replace(working, replacement);

        // 2. Protect ${secret:NAME} references so the assignment rule cannot rewrite the reference
        //    itself into ${secret:[redacted]} (S1-04b).
        var references = new List<string>();
        const char placeholder = '\u0001';
        if (settings.PreserveSecretReferences)
        {
            working = Timeout(@"\$\{secret:[A-Za-z0-9_.\-]+\}", timeout).Replace(working, match =>
            {
                references.Add(match.Value);
                return placeholder + (references.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + placeholder;
            });
        }

        // 3. Literal key shapes (sk-…, ak-…, Bearer …).
        working = Timeout(@"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,})",
                timeout, RegexOptions.IgnoreCase)
            .Replace(working, replacement);

        // 4. Specialised: credentials embedded in URLs (Discord).
        if (settings.RedactUrlCredentials)
            working = Timeout(@"\b(https?://)([^/@\s]+)@", timeout, RegexOptions.IgnoreCase)
                .Replace(working, "$1" + replacement + "@");

        // 5. Bot authorization headers: keep the header prefix, replace only the token.
        if (settings.RedactBotAuthorization)
            working = Timeout(@"(?<prefix>\b(?:authorization\s*:\s*)?bot\s+)(?<token>[A-Za-z0-9._\-]{12,})",
                    timeout, RegexOptions.IgnoreCase)
                .Replace(working, "${prefix}" + replacement);

        // 6. Assignment pairs: keep the key (and any quote/space prefix), replace the value.
        var keys = BuildKeyPattern(settings.SensitiveKeys);
        working = Timeout(
                $"(?<prefix>\"?(?<key>{keys})\"?\\s*[:=]\\s*\"?)(?<value>[^\"\\s,;]+)",
                timeout,
                RegexOptions.IgnoreCase)
            .Replace(working, match =>
            {
                var candidate = match.Groups["value"].Value;
                // A protected reference placeholder, a bare ${secret:NAME} value, or a value the
                // earlier rules already masked stays verbatim (otherwise "Bot ***" became "*** ***").
                if (candidate.Contains(placeholder, StringComparison.Ordinal))
                    return match.Value;
                if (string.Equals(candidate, replacement, StringComparison.Ordinal))
                    return match.Value;
                // "Authorization: Bot <token>" is handled by the dedicated Bot rule above; the pair
                // rule must not eat the scheme word and leave a second marker behind.
                if (candidate.Equals("bot", StringComparison.OrdinalIgnoreCase)
                    || candidate.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }
                if (settings.PreserveSecretReferences
                    && Timeout(@"^\$\{secret:[^}]+\}$", timeout).IsMatch(candidate))
                {
                    return match.Value;
                }

                return match.Groups["prefix"].Value + replacement;
            });

        // 7. Exact known secret (for example the live bot token) as a last-resort literal scrub.
        if (!string.IsNullOrEmpty(settings.KnownSecret))
            working = working.Replace(settings.KnownSecret, replacement, StringComparison.Ordinal);

        // 8. Restore the protected references.
        for (var index = 0; index < references.Count; index++)
        {
            working = working.Replace(
                placeholder + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + placeholder,
                references[index],
                StringComparison.Ordinal);
        }

        return working;
    }

    /// <summary>Redacts, then bounds the result (used by log/UI lines).</summary>
    public static string ApplyAndTruncate(string? value, int maxLength, SecretRedactionOptions? options = null)
    {
        var redacted = Apply(value, options);
        if (maxLength <= 0 || redacted.Length <= maxLength)
            return redacted;

        var builder = new StringBuilder(maxLength + 1);
        builder.Append(redacted.AsSpan(0, maxLength));
        builder.Append('…');
        return builder.ToString();
    }

    private static string BuildKeyPattern(IReadOnlySet<string> keys)
    {
        var alternatives = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(NormalizeKey)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return alternatives.Length == 0
            ? string.Join('|', DefaultKeyNames)
            : string.Join('|', alternatives);
    }

    /// <summary>Turns <c>api_key</c>/<c>api-key</c>/<c>api key</c> into one tolerant regex shape.</summary>
    private static string NormalizeKey(string key)
    {
        var parts = key
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        return string.Join("[-_ ]?", parts.Select(Regex.Escape));
    }
}
