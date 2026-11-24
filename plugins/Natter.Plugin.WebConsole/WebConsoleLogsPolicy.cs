using System.Globalization;
using System.Text;

namespace Natter.Plugin.WebConsole;

/// <summary>Validated filter/paging request for <c>/api/logs</c> and the log export.</summary>
public sealed record WebConsoleLogQuery(
    int Limit,
    int Offset,
    string? Level,
    string? Source,
    string? Search,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    bool Descending,
    bool IncludeAudit)
{
    public static WebConsoleLogQuery Default { get; } = new(200, 0, null, null, null, null, null, true, true);
}

public sealed record WebConsoleLogPage(
    IReadOnlyList<WebConsoleLogEntry> Entries,
    int Total,
    int Offset,
    int Limit,
    bool HasMore,
    int? NextOffset);

public sealed record WebConsoleLogExport(string ContentType, string Body, int Lines, bool Truncated);

/// <summary>
/// Pure log filtering/paging/export policy. It never touches IO: the handler feeds it the already
/// redacted host tail (and plugin audit entries stay separate). Everything is bounded so a console
/// cannot be used to amplify or dump unbounded host memory.
/// </summary>
public static class WebConsoleLogsPolicy
{
    public const int MaxLimit = 500;
    public const int DefaultLimit = 200;
    public const int MaxOffset = 10_000;
    public const int MaxSearchLength = 128;
    public const int MaxSourceLength = 128;

    public const int MaxExportLines = 5_000;
    public const int MaxExportBytes = 1024 * 1024;

    public const string NdjsonContentType = "application/x-ndjson; charset=utf-8";
    public const string TextContentType = "text/plain; charset=utf-8";

    /// <summary>Severity ordering used by the <c>level</c> filter (minimum severity).</summary>
    private static readonly Dictionary<string, int> Severity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trace"] = 0,
        ["debug"] = 1,
        ["information"] = 2,
        ["info"] = 2,
        ["warning"] = 3,
        ["warn"] = 3,
        ["error"] = 4,
        ["critical"] = 5,
        ["fatal"] = 5,
    };

    private static readonly string[] KnownLevels =
        ["trace", "debug", "information", "info", "warning", "warn", "error", "critical", "fatal"];

    public static IReadOnlyList<string> Levels => KnownLevels;

    /// <summary>Parses the query string into a bounded query; returns the first validation error.</summary>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> query,
        int maxLimit,
        out WebConsoleLogQuery parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(query);
        parsed = WebConsoleLogQuery.Default;
        error = null;

        var hardMax = Math.Clamp(maxLimit, 1, MaxLimit);

        if (!TryReadInt(query, "limit", DefaultLimit, 1, hardMax, out var limit, out error))
            return false;

        if (!TryReadInt(query, "offset", 0, 0, MaxOffset, out var offset, out error))
            return false;

        string? level = null;
        if (query.TryGetValue("level", out var levelRaw) && !string.IsNullOrWhiteSpace(levelRaw))
        {
            level = levelRaw.Trim().ToLowerInvariant();
            if (!Severity.ContainsKey(level))
            {
                error = $"level must be one of {string.Join(", ", KnownLevels)}.";
                return false;
            }
        }

        var source = ReadText(query, "source", MaxSourceLength, out error);
        if (error is not null)
            return false;

        var search = ReadText(query, "q", MaxSearchLength, out error);
        if (error is not null)
            return false;

        DateTimeOffset? since = null;
        if (query.TryGetValue("since", out var sinceRaw) && !string.IsNullOrWhiteSpace(sinceRaw))
        {
            if (!DateTimeOffset.TryParse(sinceRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                error = "since must be an ISO-8601 timestamp.";
                return false;
            }

            since = value;
        }

        DateTimeOffset? until = null;
        if (query.TryGetValue("until", out var untilRaw) && !string.IsNullOrWhiteSpace(untilRaw))
        {
            if (!DateTimeOffset.TryParse(untilRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                error = "until must be an ISO-8601 timestamp.";
                return false;
            }

            until = value;
        }

        if (since is not null && until is not null && since > until)
        {
            error = "since must be before until.";
            return false;
        }

        var descending = true;
        if (query.TryGetValue("order", out var order) && !string.IsNullOrWhiteSpace(order))
        {
            if (string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase))
                descending = false;
            else if (string.Equals(order, "desc", StringComparison.OrdinalIgnoreCase))
                descending = true;
            else
            {
                error = "order must be 'asc' or 'desc'.";
                return false;
            }
        }

        var includeAudit = true;
        if (query.TryGetValue("audit", out var auditRaw) && !string.IsNullOrWhiteSpace(auditRaw))
        {
            if (bool.TryParse(auditRaw, out var include))
                includeAudit = include;
            else if (auditRaw is "1" or "0")
                includeAudit = auditRaw == "1";
            else
            {
                error = "audit must be true/false or 1/0.";
                return false;
            }
        }

        parsed = new WebConsoleLogQuery(limit, offset, level, source, search, since, until, descending, includeAudit);
        return true;
    }

    /// <summary>Applies filters, ordering and paging to an already redacted sequence.</summary>
    public static WebConsoleLogPage Page(IEnumerable<WebConsoleLogEntry> entries, WebConsoleLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);

        var filtered = entries.Where(entry => Matches(entry, query)).ToList();
        var ordered = query.Descending
            ? filtered.OrderByDescending(entry => entry.Timestamp).ToList()
            : filtered.OrderBy(entry => entry.Timestamp).ToList();

        var slice = ordered.Skip(query.Offset).Take(query.Limit).ToArray();
        var hasMore = query.Offset + slice.Length < ordered.Count;
        int? nextOffset = hasMore ? query.Offset + slice.Length : null;

        return new WebConsoleLogPage(slice, ordered.Count, query.Offset, query.Limit, hasMore, nextOffset);
    }

    /// <summary>
    /// Renders an export. The content is already redacted by the producer; this only bounds size and
    /// escapes nothing (a log line is text, not markup, and the endpoint is served as text/plain).
    /// </summary>
    public static WebConsoleLogExport Export(IEnumerable<WebConsoleLogEntry> entries, string? format)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ndjson = string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(format, "ndjson", StringComparison.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        var lines = 0;
        var bytes = 0;
        var truncated = false;

        if (!ndjson)
        {
            const string header = "# Natter WebConsole log export (redacted)\n";
            builder.Append(header);
            bytes += Encoding.UTF8.GetByteCount(header);
        }

        foreach (var entry in entries)
        {
            if (lines >= MaxExportLines)
            {
                truncated = true;
                break;
            }

            var line = ndjson
                ? "{\"time\":\"" + entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)
                  + "\",\"level\":\"" + Escape(entry.Level)
                  + "\",\"source\":\"" + Escape(entry.Source)
                  + "\",\"message\":\"" + Escape(entry.Message) + "\"}"
                : "[" + entry.Timestamp.ToString("O", CultureInfo.InvariantCulture) + "] "
                  + entry.Level + " " + entry.Source + " — " + entry.Message;

            // The budget is in UTF-8 bytes: the em dash in the text format and any non-ASCII log
            // content must be counted as bytes, not chars, or the export can exceed the cap.
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes + lineBytes + 64 > MaxExportBytes)
            {
                truncated = true;
                break;
            }

            builder.Append(line).Append('\n');
            bytes += lineBytes;
            lines++;
        }

        if (truncated)
            builder.Append(ndjson ? "{\"truncated\":true}\n" : "... (truncated)\n");

        return new WebConsoleLogExport(
            ndjson ? NdjsonContentType : TextContentType,
            builder.ToString(),
            lines,
            truncated);
    }

    public static bool Matches(WebConsoleLogEntry entry, WebConsoleLogQuery query)
    {
        if (query.Level is not null && !MatchesLevel(entry.Level, query.Level))
            return false;

        if (!string.IsNullOrEmpty(query.Source)
            && (entry.Source ?? string.Empty).IndexOf(query.Source, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(query.Search))
        {
            var haystack = (entry.Message ?? string.Empty) + " " + (entry.Source ?? string.Empty);
            if (haystack.IndexOf(query.Search, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        // A log line with no timestamp is kept: filtering it out would hide data for a display concern.
        if (entry.Timestamp == default)
            return true;

        if (query.Since is not null && entry.Timestamp < query.Since)
            return false;

        if (query.Until is not null && entry.Timestamp > query.Until)
            return false;

        return true;
    }

    /// <summary>True when the entry severity is at or above the requested minimum.</summary>
    public static bool MatchesLevel(string? entryLevel, string minLevel)
    {
        if (string.IsNullOrWhiteSpace(minLevel))
            return true;

        if (!Severity.TryGetValue(minLevel, out var minimum))
            return true;

        if (string.IsNullOrWhiteSpace(entryLevel))
            return false;

        var actual = Severity.TryGetValue(entryLevel, out var value) ? value : 2;
        return actual >= minimum;
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> query,
        string name,
        int fallback,
        int minimum,
        int maximum,
        out int value,
        out string? error)
    {
        value = fallback;
        error = null;

        if (!query.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"{name} must be an integer.";
            return false;
        }

        if (parsed < minimum || parsed > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string? ReadText(
        IReadOnlyDictionary<string, string> query,
        string name,
        int maxLength,
        out string? error)
    {
        error = null;

        if (!query.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.Length > maxLength)
        {
            error = $"{name} must be at most {maxLength} characters.";
            return null;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{name} must not contain control characters.";
            return null;
        }

        return trimmed;
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                        builder.Append(' ');
                    else
                        builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
