using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Natter.Application.Admin;

/// <summary>
/// Document ETags shared by host and WebConsole. The format is exactly the plugin policy format:
/// <c>sha256:&lt;lowercase hex of the UTF-8 content&gt;</c>.
/// </summary>
public static class AdminETag
{
    public const string Prefix = "sha256:";

    public static string Compute(string? content)
        => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)))
            .ToLowerInvariant();

    public static bool Matches(string? content, string? etag)
        => !string.IsNullOrEmpty(etag) && string.Equals(Compute(content), etag, StringComparison.Ordinal);
}

/// <summary>
/// Content-level validation shared with the WebConsole plugin policy: size, NUL characters,
/// plaintext secret literals, JSON well-formedness and root type. Schema-level checks run after
/// this and are documented separately.
/// </summary>
public static class AdminConfigContentValidator
{
    public const int MaxContentBytes = 256 * 1024;

    public const int MaxSecretScanLength = 256 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SecretValuePattern = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,}|-----BEGIN [A-Z ]{0,32}PRIVATE KEY-----)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex PlainSecretFieldPattern = new(
        "\\\"(token|secret|password|passwd|api[-_]?key|client[-_]?secret)\\\"\\s*:\\s*\\\"(?!\\$\\{secret:)[^\\\"]{4,}\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    public static bool ContainsSecretLiteral(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        var scanned = content.Length > MaxSecretScanLength ? content[..MaxSecretScanLength] : content;
        return SecretValuePattern.IsMatch(scanned) || PlainSecretFieldPattern.IsMatch(scanned);
    }

    /// <summary>Content checks only; returns every issue in the same order the plugin reports.</summary>
    public static IReadOnlyList<AdminConfigIssue> Validate(string? content)
    {
        content ??= string.Empty;
        var issues = new List<AdminConfigIssue>();

        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        if (sizeBytes > MaxContentBytes)
        {
            issues.Add(new AdminConfigIssue(
                "/content",
                AdminCodes.ConfigTooLarge,
                $"Config content exceeds {MaxContentBytes} bytes.",
                $"<= {MaxContentBytes} bytes",
                sizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (content.Contains('\0', StringComparison.Ordinal))
        {
            issues.Add(new AdminConfigIssue(
                "/content",
                AdminCodes.ConfigControlChars,
                "Config content contains NUL characters."));
        }

        if (ContainsSecretLiteral(content))
        {
            issues.Add(new AdminConfigIssue(
                "/content",
                AdminCodes.ConfigSecretLiteral,
                "Config content appears to contain a plaintext secret; use '${secret:NAME}' references."));
        }

        try
        {
            using var document = JsonDocument.Parse(content, ProfilesConfigValidator.ParseOptions);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                issues.Add(new AdminConfigIssue(
                    "$",
                    AdminCodes.ConfigRootType,
                    "Config root must be a JSON object or array.",
                    "object|array",
                    document.RootElement.ValueKind.ToString()));
            }
        }
        catch (JsonException exception)
        {
            issues.Add(new AdminConfigIssue(
                "$",
                AdminCodes.ConfigInvalidJson,
                $"Config content is not valid JSON: {exception.Message}"));
        }

        return issues;
    }
}
