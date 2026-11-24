using Natter.Plugin.WebConsole;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>Pure policy tests for log paging/export bounds (no HTTP involved).</summary>
public sealed class LogsPolicyTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static WebConsoleLogEntry[] Entries() =>
    [
        new(Base.AddSeconds(1), "Debug", "Natter.Host", "debug line"),
        new(Base.AddSeconds(2), "Information", "Natter.Admin.Config", "info line"),
        new(Base.AddSeconds(3), "Warning", "Natter.Upstream", "warn line"),
        new(Base.AddSeconds(4), "Error", "Natter.Host", "error line"),
    ];

    [Theory]
    [InlineData("debug", "Debug", true)]
    [InlineData("warning", "Information", false)]
    [InlineData("information", "Warning", true)]
    [InlineData("error", "Critical", true)]
    [InlineData("critical", "Error", false)]
    [InlineData(null, "Information", true)]
    [InlineData("error", null, false)]
    public void Level_matching_uses_minimum_severity(string? minimum, string? entryLevel, bool expected)
        => Assert.Equal(expected, WebConsoleLogsPolicy.MatchesLevel(entryLevel, minimum!));

    [Fact]
    public void Paging_is_bounded_and_reports_the_next_offset()
    {
        var query = WebConsoleLogQuery.Default with { Limit = 2, Offset = 1, Descending = false };

        var page = WebConsoleLogsPolicy.Page(Entries(), query);

        Assert.Equal(4, page.Total);
        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.HasMore);
        Assert.Equal(3, page.NextOffset);
        Assert.Equal("Information", page.Entries[0].Level);
        Assert.False(WebConsoleLogsPolicy.Page(Entries(), query with { Offset = 3 }).HasMore);
    }

    [Fact]
    public void Filters_combine()
    {
        var query = WebConsoleLogQuery.Default with { Source = "host", Level = "warning", Descending = false };

        var page = WebConsoleLogsPolicy.Page(Entries(), query);

        Assert.Equal(1, page.Total);
        Assert.Equal("Error", page.Entries[0].Level);
    }

    [Fact]
    public void Export_escapes_jsonl_content_and_marks_truncation()
    {
        var entries = new[]
        {
            new WebConsoleLogEntry(Base, "Information", "Natter.Host", "quote \" and \\ and \n newline"),
            new WebConsoleLogEntry(Base, "Error", "Natter.Host", "tab\there"),
        };

        var export = WebConsoleLogsPolicy.Export(entries, "jsonl");

        Assert.Equal("application/x-ndjson; charset=utf-8", export.ContentType);
        var lines = export.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\\\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\\\\", lines[0], StringComparison.Ordinal);
        Assert.Contains("\\n", lines[0], StringComparison.Ordinal);
        Assert.False(export.Truncated);
    }

    [Fact]
    public void Export_text_format_has_a_header_and_is_byte_bounded()
    {
        var huge = new WebConsoleLogEntry(Base, "Information", "Natter.Host", new string('x', 4096));
        var entries = Enumerable.Repeat(huge, 1000).ToArray();

        var export = WebConsoleLogsPolicy.Export(entries, "text");

        Assert.Equal("text/plain; charset=utf-8", export.ContentType);
        Assert.StartsWith("# Natter WebConsole log export", export.Body, StringComparison.Ordinal);
        Assert.True(export.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(export.Body) <= WebConsoleLogsPolicy.MaxExportBytes);
        Assert.EndsWith("... (truncated)\n", export.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("offset=10001")]
    [InlineData("level=noise")]
    [InlineData("since=2026-13-01")]
    [InlineData("since=2026-11-21T10:00:00Z&until=2026-11-21T09:00:00Z")]
    [InlineData("source=" + "a")]
    public void Invalid_queries_are_rejected_with_a_reason(string queryString)
    {
        var query = Parse(queryString);

        if (queryString == "source=a")
        {
            Assert.True(WebConsoleLogsPolicy.TryParse(query, 500, out var ok, out var noError));
            Assert.Null(noError);
            Assert.Equal("a", ok.Source);
            return;
        }

        Assert.False(WebConsoleLogsPolicy.TryParse(query, 500, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Control_characters_in_text_filters_are_rejected()
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["q"] = "bad\u0001text" };

        Assert.False(WebConsoleLogsPolicy.TryParse(query, 500, out _, out var error));
        Assert.Contains("control characters", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Limit_is_clamped_to_the_console_maximum()
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["limit"] = "500" };

        Assert.False(WebConsoleLogsPolicy.TryParse(query, maxLimit: 50, out _, out var error));
        Assert.NotNull(error); // 500 > resolved maximum 50
        Assert.Contains("limit must be between 1 and 50", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_without_a_timestamp_are_not_filtered_out()
    {
        var entry = new WebConsoleLogEntry(default, "Information", "Natter.Host", "no timestamp");
        var query = WebConsoleLogQuery.Default with { Since = Base };

        Assert.True(WebConsoleLogsPolicy.Matches(entry, query));
    }

    private static Dictionary<string, string> Parse(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;

            result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return result;
    }
}
