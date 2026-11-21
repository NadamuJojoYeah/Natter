using System.Text;
using System.Text.RegularExpressions;

namespace Natter.Application.Admin;

/// <summary>
/// Host redaction rules for admin output. Mirrors the CLI <c>RuntimeHealth.Sanitize</c> rules and
/// the WebConsole plugin rules, and never expands <c>${secret:NAME}</c> references.
/// </summary>
public sealed class AdminRedactor : IAdminRedactor
{
    /// <summary>Upper bound for one redaction pass; longer values are truncated before scanning.</summary>
    public const int MaxScanLength = 64 * 1024;

    public const string Replacement = "[redacted]";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SecretLiteralPattern = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex PrivateKeyPattern = new(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.Singleline,
        RegexTimeout);

    private static readonly Regex SecretPairPattern = new(
        @"(?<prefix>""?(?<key>token|secret|password|passwd|api[-_]?key|apikey|authorization|cookie|credential|credentials|private[-_]?key|connection[-_]?string)""?\s*[:=]\s*""?)(?<value>[^""\s,;}]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex SecretReferencePattern = new(
        @"^\$\{secret:[A-Za-z0-9._\-]+\}$",
        RegexOptions.Compiled,
        RegexTimeout);

    public static AdminRedactor Default { get; } = new();

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var working = value;
        if (working.Length > MaxScanLength)
            working = working[..MaxScanLength] + "…(truncated)";

        working = PrivateKeyPattern.Replace(working, Replacement);
        working = SecretLiteralPattern.Replace(working, Replacement);
        working = SecretPairPattern.Replace(working, match =>
        {
            var candidate = match.Groups["value"].Value;
            // Keep ${secret:NAME} references verbatim: the console shows references, never values.
            return SecretReferencePattern.IsMatch(candidate)
                ? match.Value
                : match.Groups["prefix"].Value + Replacement;
        });

        return working;
    }

    /// <summary>Redacts and truncates a value to <paramref name="maxLength"/> characters.</summary>
    public string RedactAndTruncate(string? value, int maxLength)
    {
        var redacted = Redact(value);
        if (maxLength <= 0 || redacted.Length <= maxLength)
            return redacted;

        var builder = new StringBuilder(Math.Min(redacted.Length, maxLength) + 1);
        builder.Append(redacted.AsSpan(0, maxLength));
        builder.Append('…');
        return builder.ToString();
    }
}
