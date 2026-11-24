using System.Text.Json;
using Natter.Application.Chat;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

/// <summary>
/// Structured trace endpoint (task-65): the console reads persisted task events instead of parsing
/// <c>/status</c> text. These tests pin the contract — availability, empty data, detail, not-found
/// and the role boundary.
/// </summary>
public sealed class TraceEndpointTests
{
    private static AgentTaskTrace SampleTrace(string taskId = "task_abc") => new(
        new AgentTaskTraceHeader(
            taskId,
            "session-1",
            "discord:discord:11",
            "Failed",
            Version: 4,
            CreatedAt: DateTimeOffset.UnixEpoch,
            UpdatedAt: DateTimeOffset.UnixEpoch.AddSeconds(12),
            ElapsedMs: 12000,
            ErrorCode: "agent.timeout",
            ErrorMessage: "tool exceeded 10s",
            ResultText: "partial result",
            ResultTruncated: false),
        [
            new AgentTaskTraceEntry("started", DateTimeOffset.UnixEpoch, Status: "Running"),
            new AgentTaskTraceEntry("step", DateTimeOffset.UnixEpoch.AddMilliseconds(20), Step: 1, Name: "native"),
            new AgentTaskTraceEntry("tool", DateTimeOffset.UnixEpoch.AddMilliseconds(120), Name: "clock", Status: "ok"),
            new AgentTaskTraceEntry("final", DateTimeOffset.UnixEpoch.AddMilliseconds(12000), Code: "agent.timeout"),
        ]);

    [Fact]
    public async Task Trace_list_returns_structured_tasks()
    {
        var tasks = new FakeTaskInspection();
        tasks.Recent.Add(new AgentTaskSummary("task_abc", "Failed", "discord:discord:11", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 12000, "agent.timeout", 4));

        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        Assert.True(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("task_abc", document.RootElement.GetProperty("tasks")[0].GetProperty("taskId").GetString());
        Assert.Equal("Failed", document.RootElement.GetProperty("tasks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Trace_list_is_empty_but_available_when_the_host_has_no_tasks()
    {
        using var harness = WebConsoleHarness.Create(tasks: new FakeTaskInspection());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        Assert.True(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task Trace_endpoints_report_501_when_admin_read_was_not_granted()
    {
        // No IAgentTaskInspection in the plugin provider == host did not grant admin.read.
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var list = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace", cookie: login.Cookie));
        Assert.Equal(501, list.StatusCode);
        Assert.Equal("host.tasks.unavailable", ErrorCode(list));

        var detail = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace/task_abc", cookie: login.Cookie));
        Assert.Equal(501, detail.StatusCode);
        Assert.Equal("host.tasks.unavailable", ErrorCode(detail));
    }

    [Fact]
    public async Task Trace_detail_returns_header_and_entries()
    {
        var tasks = new FakeTaskInspection();
        tasks.Traces["task_abc"] = SampleTrace();

        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace/task_abc", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        var task = document.RootElement.GetProperty("task");
        Assert.Equal("task_abc", task.GetProperty("taskId").GetString());
        Assert.Equal(4, task.GetProperty("version").GetInt32());
        Assert.Equal("agent.timeout", task.GetProperty("errorCode").GetString());
        Assert.Equal(12000, task.GetProperty("elapsedMs").GetInt64());

        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(4, entries.GetArrayLength());
        Assert.Equal("started", entries[0].GetProperty("kind").GetString());
        Assert.Equal("tool", entries[2].GetProperty("kind").GetString());
        Assert.Equal("clock", entries[2].GetProperty("name").GetString());
        Assert.Equal("agent.timeout", entries[3].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Trace_detail_returns_404_for_unknown_tasks()
    {
        using var harness = WebConsoleHarness.Create(tasks: new FakeTaskInspection());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace/task_missing", cookie: login.Cookie));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("task.not_found", ErrorCode(response));
    }

    [Fact]
    public async Task Trace_endpoints_require_a_session()
    {
        using var harness = WebConsoleHarness.Create(tasks: new FakeTaskInspection());

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace"));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("auth.required", ErrorCode(response));
    }

    [Fact]
    public async Task Viewer_role_can_read_the_trace()
    {
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ViewerToken = WebConsoleHarness.ViewerToken },
            tasks: new FakeTaskInspection());
        var login = await harness.LoginAsync(WebConsoleHarness.ViewerToken);

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Trace_list_passes_every_filter_to_the_host()
    {
        var tasks = new FakeTaskInspection();
        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET",
            "/api/trace",
            cookie: login.Cookie,
            query: "?source=discord:discord:11&status=Failed&session=session-1&since=2026-11-24T10:00:00Z&until=2026-11-24T11:00:00Z&limit=7"));

        Assert.Equal(200, response.StatusCode);
        var filter = tasks.LastFilter;
        Assert.NotNull(filter);
        Assert.Equal("discord:discord:11", filter!.Source);
        Assert.Equal("Failed", filter.Status);
        Assert.Equal("session-1", filter.SessionId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), filter.Since);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture), filter.Until);
    }

    [Theory]
    [InlineData("status=Failed", "Failed", null, null)]
    [InlineData("source=discord:discord:11", null, "discord:discord:11", null)]
    [InlineData("session=session-1", null, null, "session-1")]
    public async Task Trace_filter_works_individually(string query, string? status, string? source, string? session)
    {
        var tasks = new FakeTaskInspection();
        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/trace", cookie: login.Cookie, query: "?" + query));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(status, tasks.LastFilter!.Status);
        Assert.Equal(source, tasks.LastFilter.Source);
        Assert.Equal(session, tasks.LastFilter.SessionId);
    }

    [Fact]
    public async Task Trace_filter_narrows_the_list()
    {
        var tasks = new FakeTaskInspection();
        tasks.Recent.Add(new AgentTaskSummary("task_a", "Failed", "discord:discord:11", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, "x", 1));
        tasks.Recent.Add(new AgentTaskSummary("task_b", "Completed", "discord:discord:11", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, null, 1));

        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/trace", cookie: login.Cookie, query: "?status=Completed"));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        var ids = document.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(task => task.GetProperty("taskId").GetString()).ToArray();
        Assert.Equal(["task_b"], ids);
    }

    [Fact]
    public async Task Trace_detail_reports_the_measured_duration()
    {
        var tasks = new FakeTaskInspection();
        var trace = SampleTrace() with
        {
            Entries =
            [
                new("started", DateTimeOffset.UnixEpoch),
                new("tool", DateTimeOffset.UnixEpoch.AddSeconds(1), Name: "clock", DurationMs: 45),
            ],
        };
        tasks.Traces["task_abc"] = trace;

        using var harness = WebConsoleHarness.Create(tasks: tasks);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/trace/task_abc", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(45, entries[1].GetProperty("durationMs").GetInt64());
        // Unmeasured events omit the field entirely (nulls are not serialised) — the UI must treat a
        // missing durationMs as "estimate", not as zero.
        Assert.False(entries[0].TryGetProperty("durationMs", out _));
    }

    private static string? ErrorCode(WebConsoleHttpResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
