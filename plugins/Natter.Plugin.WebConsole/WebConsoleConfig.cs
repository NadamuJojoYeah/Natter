using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Natter.Plugin.WebConsole;

/// <summary>Sanitized view of the plugin's own configuration (design §7/§8).</summary>
public sealed record WebConsoleConfigPreview(
    string Source,
    string? SchemaPath,
    bool WriteEnabled,
    string? WriteGapCode,
    JsonElement Values,
    IReadOnlyList<string> Redacted);

/// <summary>
/// Builds the config preview. Never expands <c>${secret:NAME}</c> references and never reads files
/// outside the plugin directory.
/// </summary>
public static class WebConsoleConfig
{
    public static WebConsoleConfigPreview Build(
        JsonElement raw,
        string source,
        string? schemaPath,
        WebConsoleOptions options)
    {
        var (sanitized, redacted) = WebConsoleRedaction.Sanitize(raw);
        var writeEnabled = !options.ReadOnly && options.AllowConfigWrite;

        // OBS-WC-WRITEGAP: the static preview only reports the plugin switch. Whether the host
        // admin config store is bridged is a live property (WebConsoleDiagnostics.ConfigPreview /
        // WebConsoleAdminStatus.ConfigGapCode), and the UI enables editing from
        // admin.configStoreAvailable rather than from this field alone.
        return new WebConsoleConfigPreview(
            source,
            schemaPath,
            writeEnabled,
            writeEnabled ? null : "webconsole.write_disabled",
            sanitized,
            redacted);
    }
}

/// <summary>
/// Single redaction entry point (S2-D5). When the host bridges <c>IAdminRedactor</c> its rules win, so
/// the console and the host cannot drift apart; otherwise the local rules below are used. Both
/// cover the same shapes: PEM private-key blocks, <c>sk-*</c>/<c>ak-*</c>/<c>Bearer</c> values and
/// <c>key=value</c>/<c>"key": "value"</c> assignment pairs, while <c>${secret:NAME}</c> references stay
/// readable.
/// </summary>
public sealed class WebConsoleRedactionPolicy
{
    public static WebConsoleRedactionPolicy Local { get; } = new(null);

    public WebConsoleRedactionPolicy(Natter.Application.Admin.IAdminRedactor? hostRedactor)
        => HostRedactor = hostRedactor;

    public Natter.Application.Admin.IAdminRedactor? HostRedactor { get; }

    /// <summary>True when the host redactor is in use (surfaced in <c>web status</c>/health).</summary>
    public bool UsesHostRedactor => HostRedactor is not null;

    public string Redact(string? value)
        => HostRedactor?.Redact(value) ?? WebConsoleRedaction.RedactSecrets(value);

    public string SanitizeText(string? value, int maxLength = 300)
    {
        var redacted = Redact(value);
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }
}

/// <summary>
/// Local redaction (gap G9: the host has no public redactor contract yet).
/// Rules are intentionally conservative and covered by unit tests.
/// </summary>
public static class WebConsoleRedaction
{
    private const int MaxStringLength = 2000;

    private static readonly Regex SensitiveKeyPattern = new(
        "(token|secret|password|passwd|pwd|api[-_]?key|apikey|authorization|cookie|credential|private[-_]?key|connection[-_]?string)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SensitiveValuePattern = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>OBS-WC-P3-REDACT-KV: <c>token=...</c> / <c>API_KEY: ...</c> plain text pairs.</summary>
    private static readonly Regex SensitivePairPattern = new(
        "(?<prefix>\"?(?i:token|secret|password|passwd|pwd|api[-_]?key|apikey|authorization|cookie|credential|credentials|private[-_]?key|connection[-_]?string)\"?\\s*[:=]\\s*\"?)(?<value>[^\\s,;\"']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SecretReferencePattern = new(
        @"^\$\{secret:[^}]+\}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// OBS-WC-S1-04: complete PEM private-key blocks (multi-line). Masked before any value/assignment
    /// rule because the body lines carry no key name the pair rule could match.
    /// </summary>
    private static readonly Regex PemBlockPattern = new(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// OBS-WC-S1-04b: <c>${secret:NAME}</c> tokens must survive redaction. Without protecting them
    /// first, the assignment rule matches the <c>secret:</c> inside the reference and rewrites
    /// <c>${secret:WEBCONSOLE_TOKEN}</c> to <c>${secret:[redacted]}</c>, which hides the fact that a
    /// secret reference is configured at all.
    /// </summary>
    private static readonly Regex SecretReferenceTokenPattern = new(
        @"\$\{secret:[A-Za-z0-9_.\-]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private const char ReferencePlaceholder = '\u0001';

    /// <summary>Truncated PEM (no END marker) still must not leak the key body.</summary>
    private static readonly Regex PemDanglingPattern = new(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\\s\\S]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static (JsonElement Value, IReadOnlyList<string> RedactedPointers) Sanitize(JsonElement raw)
    {
        var redacted = new List<string>();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw.GetRawText());
        }
        catch (JsonException)
        {
            return (JsonSerializer.SerializeToElement(new { error = "config-not-serializable" }), redacted);
        }

        SanitizeNode(node, "$", redacted);
        return (JsonSerializer.SerializeToElement(node), redacted);
    }

    public static string RedactSecrets(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var redacted = PemBlockPattern.Replace(value, "[redacted]");
        redacted = PemDanglingPattern.Replace(redacted, "[redacted]");

        // Protect `${secret:NAME}` references so no later rule can rewrite them.
        var references = new List<string>();
        redacted = SecretReferenceTokenPattern.Replace(redacted, match =>
        {
            references.Add(match.Value);
            return ReferencePlaceholder + (references.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                   + ReferencePlaceholder;
        });

        redacted = SensitiveValuePattern.Replace(redacted, "[redacted]");
        var result = SensitivePairPattern.Replace(
            redacted,
            match =>
            {
                var value = match.Groups["value"].Value;

                // A protected reference placeholder: keep it so the restore step can put the
                // `${secret:NAME}` token back verbatim.
                if (value.Contains(ReferencePlaceholder, StringComparison.Ordinal))
                    return match.Value;

                return SecretReferencePattern.IsMatch(value)
                    ? match.Value
                    : match.Groups["prefix"].Value + "[redacted]";
            });

        for (var index = 0; index < references.Count; index++)
        {
            result = result.Replace(
                ReferencePlaceholder + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ReferencePlaceholder,
                references[index],
                StringComparison.Ordinal);
        }

        return result;
    }

    public static string SanitizeText(string? value, int maxLength = 300)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var sanitized = RedactSecrets(value);
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "…";
    }

    private static void SanitizeNode(JsonNode? node, string pointer, List<string> redacted)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    var child = obj[key];
                    var childPointer = pointer + "." + key;

                    if (child is null)
                        continue;

                    if (child is JsonValue value
                        && value.TryGetValue<string>(out var text)
                        && !SecretReferencePattern.IsMatch(text)
                        && SensitiveKeyPattern.IsMatch(key))
                    {
                        obj[key] = "***";
                        redacted.Add(childPointer);
                        continue;
                    }

                    SanitizeNode(child, childPointer, redacted);
                }

                break;
            }

            case JsonArray array:
            {
                for (var index = 0; index < array.Count; index++)
                    SanitizeNode(array[index], $"{pointer}[{index}]", redacted);
                break;
            }

            case JsonValue value:
            {
                if (!value.TryGetValue<string>(out var text))
                    return;

                if (SecretReferencePattern.IsMatch(text))
                    return;

                var replaced = RedactSecrets(text);
                if (!string.Equals(replaced, text, StringComparison.Ordinal))
                    redacted.Add(pointer);

                if (replaced.Length > MaxStringLength)
                    replaced = replaced[..MaxStringLength] + "…(truncated)";

                if (!string.Equals(replaced, text, StringComparison.Ordinal))
                    value.ReplaceWith(replaced);

                break;
            }
        }
    }
}
