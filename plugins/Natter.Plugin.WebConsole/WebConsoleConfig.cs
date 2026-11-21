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

        var redacted = SensitiveValuePattern.Replace(value, "[redacted]");
        return SensitivePairPattern.Replace(
            redacted,
            match => SecretReferencePattern.IsMatch(match.Groups["value"].Value)
                ? match.Value
                : match.Groups["prefix"].Value + "[redacted]");
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
