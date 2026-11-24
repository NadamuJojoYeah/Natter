using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// <c>/api/logs</c> filtering/paging and <c>/api/logs/export</c>: everything is bounded, redacted and
/// validated before it reaches the browser, and a malformed filter never returns partial data.
/// </summary>
public sealed class LogsFilterExportTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static FakeAdminLogReader Reader() => new()
    {
        Entries =
        [
            new(Base.AddMinutes(-3), "Debug", "Natter.Host", "startup probe", 1),
            new(Base.AddMinutes(-2), "Information", "Natter.Admin.Config", "config saved token=sk-abcdefghijkl", 2),
            new(Base.AddMinutes(-1), "Warning", "Natter.Upstream.Discord", "reconnect scheduled", 3),
            new(Base, "Error", "Natter.Host", "upstream failed", 4),
        ],
    };

    private static WebConsoleOptions Options() => new()
    {
        Port = 5090,
        ReadOnly = false,
        AllowConfigWrite = true,
        AllowCommandConsole = true,
        CommandAllowlist = ["health"],
    };

    private static string Code(WebConsoleHttpResponse response)
        => LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    private static WebConsoleHttpResponse Body(WebConsoleHttpResponse response) => response;

    [Fact]
    public async Task Default_logs_response_reports_paging_metadata_and_keeps_the_section_shape()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "limit=2"));

        Assert.Equal(200, response.StatusCode);
        var json = LoginResult.Json(response);
        Assert.Equal(4, json.GetProperty("total").GetInt32());
        Assert.Equal(2, json.GetProperty("returned").GetInt32());
        Assert.Equal(2, json.GetProperty("limit").GetInt32());
        Assert.True(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, json.GetProperty("nextOffset").GetInt32());
        Assert.True(json.GetProperty("hostLogs").GetProperty("available").GetBoolean());
        Assert.Equal(2, json.GetProperty("hostLogs").GetProperty("data").GetArrayLength());
        Assert.Equal("desc", json.GetProperty("query").GetProperty("order").GetString());
        Assert.NotEmpty(json.GetProperty("query").GetProperty("levels").EnumerateArray());
    }

    [Fact]
    public async Task Level_filter_keeps_entries_at_or_above_the_requested_severity()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var warning = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "level=warning"));
        var json = LoginResult.Json(warning);
        Assert.Equal(2, json.GetProperty("total").GetInt32());
        var levels = json.GetProperty("hostLogs").GetProperty("data").EnumerateArray()
            .Select(entry => entry.GetProperty("level").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["Error", "Warning"], levels);
    }

    [Fact]
    public async Task Source_and_free_text_filters_are_case_insensitive()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var source = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "source=discord"));
        Assert.Equal(1, LoginResult.Json(source).GetProperty("total").GetInt32());

        var search = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "q=RECONNECT"));
        Assert.Equal(1, LoginResult.Json(search).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Time_window_and_ascending_order_change_the_slice()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var since = Uri.EscapeDataString(Base.AddMinutes(-2).ToString("O"));
        var window = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: $"since={since}&order=asc&limit=10"));
        var json = LoginResult.Json(window);
        Assert.Equal(3, json.GetProperty("total").GetInt32());
        Assert.Equal("asc", json.GetProperty("query").GetProperty("order").GetString());
        var first = json.GetProperty("hostLogs").GetProperty("data").EnumerateArray().First();
        Assert.Equal("Information", first.GetProperty("level").GetString());
    }

    [Theory]
    [InlineData("level=verbose", "level must be")]
    [InlineData("limit=0", "limit must be between")]
    [InlineData("limit=1000", "limit must be between")]
    [InlineData("offset=-1", "offset must be between")]
    [InlineData("order=sideways", "order must be")]
    [InlineData("since=not-a-date", "since must be an ISO-8601")]
    [InlineData("audit=maybe", "audit must be")]
    [InlineData("q=1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789", "q must be at most")]
    public async Task Malformed_log_queries_are_rejected(string query, string expectedFragment)
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: query));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("logs.invalid_query", Code(response));
        Assert.Contains(expectedFragment, response.Body.AsSpan().Length > 0
            ? System.Text.Encoding.UTF8.GetString(response.Body)
            : string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_can_be_excluded_and_is_filtered_by_text()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var withoutAudit = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "audit=false"));
        // Null properties are omitted by the shared JSON options, so "no audit" is an absent key.
        Assert.False(LoginResult.Json(withoutAudit).TryGetProperty("audit", out _));

        var withAudit = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "q=auth.login"));
        var audit = LoginResult.Json(withAudit).GetProperty("audit");
        Assert.Equal(JsonValueKind.Array, audit.ValueKind);
        Assert.All(
            audit.EnumerateArray(),
            entry => Assert.Contains("auth.login", entry.GetProperty("action").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Text_export_is_an_attachment_with_redacted_content()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs/export", cookie: login.Cookie, query: "format=text&level=information"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.ContentType);
        Assert.Contains("attachment; filename=\"webconsole-logs-", response.Headers["Content-Disposition"]);
        Assert.EndsWith(".log\"", response.Headers["Content-Disposition"]);

        var body = System.Text.Encoding.UTF8.GetString(response.Body);
        Assert.StartsWith("# Natter WebConsole log export (redacted)", body, StringComparison.Ordinal);
        Assert.Contains("[redacted]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijkl", body, StringComparison.Ordinal);
        Assert.Equal("3", response.Headers["X-WebConsole-Export-Lines"]);
        Assert.Equal("false", response.Headers["X-WebConsole-Export-Truncated"]);
        Assert.Contains(
            harness.Audit.Read(50),
            entry => entry.Action == "logs.export" && entry.Result == "ok");
    }

    [Fact]
    public async Task Jsonl_export_emits_one_redacted_object_per_line()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs/export", cookie: login.Cookie, query: "format=jsonl"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/x-ndjson; charset=utf-8", response.ContentType);
        Assert.EndsWith(".ndjson\"", response.Headers["Content-Disposition"]);

        var lines = System.Text.Encoding.UTF8.GetString(response.Body)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty("message", out _));
        }

        Assert.DoesNotContain("sk-abcdefghijkl", response.Body.Length > 0
            ? System.Text.Encoding.UTF8.GetString(response.Body)
            : string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rejects_unknown_formats()
    {
        using var harness = WebConsoleHarness.Create(Options(), logReader: Reader());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs/export", cookie: login.Cookie, query: "format=csv"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("logs.invalid_format", Code(response));
    }

    [Fact]
    public async Task Export_is_bounded_by_line_count()
    {
        var bulky = new FakeAdminLogReader
        {
            Entries = Enumerable.Range(0, WebConsoleLogsPolicy.MaxExportLines + 250)
                .Select(index => new AdminLogEntry(Base.AddSeconds(index), "Information", "Natter.Host", "line " + index, index))
                .ToArray(),
        };

        using var harness = WebConsoleHarness.Create(Options(), logReader: bulky);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs/export", cookie: login.Cookie, query: "format=jsonl&limit=500"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("true", response.Headers["X-WebConsole-Export-Truncated"]);
        Assert.Equal(
            WebConsoleLogsPolicy.MaxExportLines.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Headers["X-WebConsole-Export-Lines"]);
    }

    [Fact]
    public async Task Logs_endpoint_reports_the_host_gap_without_a_reader()
    {
        using var harness = WebConsoleHarness.Create(Options());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "level=error"));

        Assert.Equal(200, response.StatusCode);
        var json = LoginResult.Json(response);
        Assert.False(json.GetProperty("hostLogs").GetProperty("available").GetBoolean());
        Assert.Equal("host.logs.unavailable", json.GetProperty("hostLogs").GetProperty("gapCode").GetString());
        Assert.Equal(0, json.GetProperty("total").GetInt32());
    }
}
