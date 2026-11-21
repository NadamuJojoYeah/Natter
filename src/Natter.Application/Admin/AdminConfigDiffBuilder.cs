namespace Natter.Application.Admin;

/// <summary>
/// Bounded line diff used by the config editor. Mirrors the WebConsole plugin policy
/// (<c>WebConsoleConfigEditPolicy.Diff</c>) so host and plugin render the same preview:
/// prefix <c>" "</c> context, <c>"-"</c> removed, <c>"+"</c> added, bounded LCS with a coarse
/// prefix/suffix fallback for large documents.
/// </summary>
public static class AdminConfigDiffBuilder
{
    public const int MaxDiffLines = 200;

    public const int MaxDiffBytes = 32 * 1024;

    public const long MaxLcsCells = 250_000;

    public const int MaxTotalCharacters = 512 * 1024;

    /// <summary>Builds a preview diff. Returns null when there is no baseline content.</summary>
    public static AdminConfigDiff? Build(string? baseline, string? content)
    {
        if (baseline is null)
            return null;

        var before = baseline;
        var after = content ?? string.Empty;
        var left = SplitLines(before);
        var right = SplitLines(after);

        if ((long)left.Length * right.Length > MaxLcsCells
            || before.Length + after.Length > MaxTotalCharacters)
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

            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line) + 1;
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

        return new AdminConfigDiff(added, removed, Math.Min(added, removed), truncated, lines);
    }

    private static AdminConfigDiff CoarseDiff(string[] left, string[] right)
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

        return new AdminConfigDiff(added, removed, Math.Min(added, removed), Truncated: true, Lines: []);
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
