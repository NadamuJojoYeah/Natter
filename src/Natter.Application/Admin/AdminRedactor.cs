using Natter.Application.Redaction;

namespace Natter.Application.Admin;

/// <summary>
/// Host redaction rules for admin output. Mirrors the CLI <c>RuntimeHealth.Sanitize</c> rules and
/// the WebConsole plugin rules, and never expands <c>${secret:NAME}</c> references.
/// </summary>
public sealed class AdminRedactor : IAdminRedactor
{
    /// <summary>Upper bound for one redaction pass; longer values are truncated before scanning.</summary>
    public const int MaxScanLength = SecretRedaction.DefaultMaxScanLength;

    public const string Replacement = "[redacted]";

    private static readonly SecretRedactionOptions Options = SecretRedactionOptions.Admin;

    public static AdminRedactor Default { get; } = new();

    /// <summary>
    /// C-03: the rules live in <see cref="SecretRedaction"/> so admin, Discord logs and WebConsole
    /// cannot drift apart again (S2 leak: a `key=value` rule that existed in one copy only).
    /// Behaviour kept identical: <c>[redacted]</c> marker, no URL/Bot rules, `${secret:NAME}` kept.
    /// </summary>
    public string Redact(string? value) => SecretRedaction.Apply(value, Options);

    /// <summary>Redacts and truncates a value to <paramref name="maxLength"/> characters.</summary>
    public string RedactAndTruncate(string? value, int maxLength)
        => SecretRedaction.ApplyAndTruncate(value, maxLength, Options);
}
