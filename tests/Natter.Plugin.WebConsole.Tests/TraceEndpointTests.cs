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

    private static string? ErrorCode(WebConsoleHttpResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }
}
