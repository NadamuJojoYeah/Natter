using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Natter.Plugin.WebConsole;

public sealed record WebConsoleConfigEditIssue(string Pointer, string Code, string Message);

public sealed record WebConsoleConfigValidation(
    bool Valid,
    string ETag,
    int SizeBytes,
    bool ContainsSecretLiteral,
    IReadOnlyList<WebConsoleConfigEditIssue> Issues);

public sealed record WebConsoleConfigDiff(
    int Added,
    int Removed,
    int Changed,
    bool Truncated,
    IReadOnlyList<string> Lines);

/// <summary>
/// Pure validation and diff logic for the config editor. It never reads or writes files:
/// the host-side <c>IAdminConfigStore</c> remains the only write path (gap G5).
/// </summary>
public static class WebConsoleConfigEditPolicy
{
    public const int MaxContentBytes = 256 * 1024;

    private const int MaxDiffLines = 200;
    private const int MaxDiffBytes = 32 * 1024;
    private const long MaxLcsCells = 250_000;

    private static readonly Regex SecretValuePattern = new(
        @"(sk-[A-Za-z0-9_\-]{8,}|ak-[A-Za-z0-9_\-]{8,}|Bearer\s+[A-Za-z0-9._\-]{8,}|-----BEGIN [A-Z ]{0,32}PRIVATE KEY-----)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PlainSecretFieldPattern = new(
        "\\\"(token|secret|password|passwd|api[-_]?key|client[-_]?secret)\\\"\\s*:\\s*\\\"(?!\\$\\{secret:)[^\\\"]{4,}\\\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static bool IsAllowedId(string? id)
    {
        if (string.Equals(id, "profiles", StringComparison.Ordinal))
            return true;

        if (id is null || !id.StartsWith("plugin:", StringComparison.Ordinal))
            return false;

        var pluginId = id["plugin:".Length..];
        return pluginId.Length is > 0 and <= 128
               && pluginId.All(character => char.IsLetterOrDigit(character)
                                            || character is '.' or '_' or '-');
    }

    public static WebConsoleConfigValidation Validate(string documentId, string? content)
    {
        var issues = new List<WebConsoleConfigEditIssue>();
        content ??= string.Empty;

        if (!IsAllowedId(documentId))
        {
            issues.Add(new WebConsoleConfigEditIssue(
                "/id",
                "config.id_invalid",
                "Config id must be 'profiles' or 'plugin:<plugin-id>'."));
        }

        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        if (sizeBytes > MaxContentBytes)
        {
            issues.Add(new WebConsoleConfigEditIssue(
                "/content",
                "config.too_large",
                $"Config content exceeds {MaxContentBytes} bytes."));
        }

        if (content.Contains('\0', StringComparison.Ordinal))
        {
            issues.Add(new WebConsoleConfigEditIssue(
                "/content",
                "config.control_chars",
                "Config content contains NUL characters."));
        }

        var containsSecret = ContainsSecretLiteral(content);
        if (containsSecret)
        {
            issues.Add(new WebConsoleConfigEditIssue(
                "/content",
                "config.secret_literal",
                "Config content appears to contain a plaintext secret; use '${secret:NAME}' references."));
        }

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                issues.Add(new WebConsoleConfigEditIssue(
                    "$",
                    "config.root_type",
                    "Config root must be a JSON object or array."));
            }
        }
        catch (JsonException exception)
        {
            issues.Add(new WebConsoleConfigEditIssue(
                "$",
                "config.invalid_json",
                $"Config content is not valid JSON: {exception.Message}"));
        }

        return new WebConsoleConfigValidation(
            issues.Count == 0,
            ComputeETag(content),
            sizeBytes,
            containsSecret,
            issues);
    }

    public static string ComputeETag(string content)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    public static bool ContainsSecretLiteral(string content)
        => SecretValuePattern.IsMatch(content) || PlainSecretFieldPattern.IsMatch(content);

    /// <summary>Line diff with a bounded LCS matrix; large inputs fall back to prefix/suffix counts.</summary>
    public static WebConsoleConfigDiff Diff(string before, string after)
    {
        before ??= string.Empty;
        after ??= string.Empty;

        var left = SplitLines(before);
        var right = SplitLines(after);

        if ((long)left.Length * right.Length > MaxLcsCells
            || before.Length + after.Length > 512 * 1024)
        {
            return CoarseDiff(left, right);
        }

        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var added = 0;
        var removed = 0;
        var lines = new List<string>();
        var bytes = 0;
        var truncated = false;

        void Emit(string line)
        {
            if (truncated)
                return;

            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (lines.Count >= MaxDiffLines || bytes + lineBytes > MaxDiffBytes)
            {
                truncated = true;
                return;
            }

            lines.Add(line);
            bytes += lineBytes;
        }

        var x = 0;
        var y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                Emit(" " + left[x]);
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                Emit("-" + left[x]);
                removed++;
                x++;
            }
            else
            {
                Emit("+" + right[y]);
                added++;
                y++;
            }
        }

        while (x < left.Length)
        {
            Emit("-" + left[x]);
            removed++;
            x++;
        }

        while (y < right.Length)
        {
            Emit("+" + right[y]);
            added++;
            y++;
        }

        return new WebConsoleConfigDiff(
            added,
            removed,
            Math.Min(added, removed),
            truncated,
            lines);
    }

    private static WebConsoleConfigDiff CoarseDiff(string[] left, string[] right)
    {
        var prefix = 0;
        var maxPrefix = Math.Min(left.Length, right.Length);
        while (prefix < maxPrefix && string.Equals(left[prefix], right[prefix], StringComparison.Ordinal))
            prefix++;

        var suffix = 0;
        var maxSuffix = Math.Min(left.Length, right.Length) - prefix;
        while (suffix < maxSuffix
               && string.Equals(left[^(suffix + 1)], right[^(suffix + 1)], StringComparison.Ordinal))
        {
            suffix++;
        }

        var removed = left.Length - prefix - suffix;
        var added = right.Length - prefix - suffix;

        return new WebConsoleConfigDiff(
            added,
            removed,
            Math.Min(added, removed),
            Truncated: true,
            Lines: []);
    }

    private static string[] SplitLines(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0)
            return lines[..^1];

        return lines;
    }
}
