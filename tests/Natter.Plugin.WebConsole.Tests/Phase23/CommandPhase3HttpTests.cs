using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>Phase 3: restricted command console wired to the host <c>IAdminCommandRunner</c>.</summary>
public sealed class CommandPhase3HttpTests
{
    private static WebConsoleOptions Runnable(int timeoutSeconds = 10, params string[] allowlist) => new()
    {
        Port = 5090,
        ReadOnly = false,
        AllowCommandConsole = true,
        CommandAllowlist = allowlist,
        CommandTimeoutSeconds = timeoutSeconds,
    };

    private static string Body(string command, params string[] args)
        => JsonSerializer.Serialize(new { command, args });

    private static JsonElement Json(WebConsoleHttpResponse response) => LoginResult.Json(response);

    private static string Code(WebConsoleHttpResponse response)
        => Json(response).GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    [Fact]
    public async Task Run_executes_allowlisted_command_and_returns_bounded_output()
    {
        var runner = new FakeAdminCommandRunner();
        using var harness = WebConsoleHarness.Create(
            Runnable(allowlist: ["plugins list"]),
            adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            body: Body("plugins   list", "--detail"),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.Equal("plugins list", json.GetProperty("command").GetString());
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("status", json.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.False(json.GetProperty("timedOut").GetBoolean());
        Assert.Equal("--detail", runner.Calls.Single().Args.Single());
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "command.run" && entry.Result == "ok");
    }

    [Fact]
    public async Task Run_without_runner_reports_the_documented_gap()
    {
        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["health"]));
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("health", "--json"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(501, response.StatusCode);
        Assert.Equal("host.admin_command.unavailable", Code(response));
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "command.run" && entry.Result == "unavailable");
    }

    [Fact]
    public async Task Run_honors_the_host_allowlist_rejection()
    {
        var runner = new FakeAdminCommandRunner
        {
            ResultFactory = static (command, args) => FakeAdminCommandRunner.Rejected(
                command, args, AdminCodes.CommandNotAllowed, "Command is not in the host admin allowlist."),
        };

        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["doctor"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("doctor"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("command.not_allowed", Code(response));
        Assert.Single(runner.Calls);
    }

    [Theory]
    [InlineData(AdminCodes.CommandNotRegistered, 400, "command.not_registered")]
    [InlineData(AdminCodes.CommandInvalid, 400, "command.invalid")]
    [InlineData(AdminCodes.CommandBusy, 409, "command.busy")]
    [InlineData(AdminCodes.CommandCancelled, 408, "command.cancelled")]
    public async Task Run_maps_host_rejections_to_stable_http_codes(string hostCode, int status, string expected)
    {
        var runner = new FakeAdminCommandRunner
        {
            ResultFactory = (command, args) => FakeAdminCommandRunner.Rejected(command, args, hostCode, "host says no"),
        };

        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["health"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("health"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(expected, Code(response));
    }

    [Fact]
    public async Task Run_reports_timeout_with_partial_output()
    {
        var runner = new FakeAdminCommandRunner
        {
            ResultFactory = static (command, args) => new AdminCommandResult(
                true,
                AdminCodes.CommandTimeout,
                "Command timed out after 10s.",
                command,
                args,
                130,
                "partial output",
                "timed out",
                false,
                14,
                10_000,
                true),
        };

        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["events watch"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("events watch"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(504, response.StatusCode);
        Assert.Equal("command.timeout", Code(response));
        var json = Json(response);
        Assert.True(json.GetProperty("result").GetProperty("timedOut").GetBoolean());
        Assert.Equal("partial output", json.GetProperty("result").GetProperty("stdout").GetString());
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "command.run" && entry.Result == "timeout");
    }

    [Fact]
    public async Task Run_redacts_and_truncates_host_output()
    {
        var huge = new string('a', WebConsoleCommandPolicy.MaxOutputBytes + 4096);
        var runner = new FakeAdminCommandRunner
        {
            ResultFactory = (command, args) => new AdminCommandResult(
                true,
                AdminCodes.CommandSucceeded,
                "Command completed.",
                command,
                args,
                0,
                "token sk-abcdefghijkl\n" + huge,
                string.Empty,
                false,
                huge.Length,
                7,
                false),
        };

        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["doctor"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("doctor"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.DoesNotContain("sk-abcdefghijkl", json.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json.GetProperty("stdout").GetString()!, StringComparison.Ordinal);
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(json.GetProperty("stdout").GetString()!)
            <= WebConsoleCommandPolicy.MaxOutputBytes);
    }

    [Fact]
    public async Task Timeout_with_oversized_partial_output_stays_bounded_and_maps_to_504()
    {
        var huge = new string('y', WebConsoleCommandPolicy.MaxOutputBytes);
        var runner = new FakeAdminCommandRunner
        {
            ResultFactory = (command, args) => new AdminCommandResult(
                true,
                AdminCodes.CommandTimeout,
                "Command timed out after 10s.",
                command,
                args,
                130,
                huge,
                huge,
                true,
                huge.Length * 2,
                10_000,
                true),
        };

        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["events watch"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("events watch"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(504, response.StatusCode);
        Assert.Equal("command.timeout", Code(response));
        var result = Json(response).GetProperty("result");
        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        var combined = System.Text.Encoding.UTF8.GetByteCount(result.GetProperty("stdout").GetString()!)
                       + System.Text.Encoding.UTF8.GetByteCount(result.GetProperty("stderr").GetString()!);
        Assert.True(combined <= WebConsoleCommandPolicy.MaxOutputBytes);
    }

    [Fact]
    public async Task Run_reports_host_exception_as_bad_gateway()
    {
        var runner = new FakeAdminCommandRunner { Throw = true };
        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["health"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: Body("health"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(502, response.StatusCode);
        Assert.Equal("host.admin_command.failed", Code(response));
    }

    [Fact]
    public async Task Run_keeps_plugin_gating_before_the_host()
    {
        var runner = new FakeAdminCommandRunner();

        using (var readOnly = WebConsoleHarness.Create(adminCommand: runner))
        {
            var login = await readOnly.LoginAsync();
            var response = await readOnly.Handler.HandleAsync(readOnly.Request(
                "POST", "/api/commands", body: Body("health"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(403, response.StatusCode);
            Assert.Equal("console.read_only", Code(response));
        }

        using (var disabled = WebConsoleHarness.Create(
                   new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowCommandConsole = false },
                   adminCommand: runner))
        {
            var login = await disabled.LoginAsync();
            var response = await disabled.Handler.HandleAsync(disabled.Request(
                "POST", "/api/commands", body: Body("health"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(403, response.StatusCode);
            Assert.Equal("command.disabled", Code(response));
        }

        using (var notAllowed = WebConsoleHarness.Create(Runnable(allowlist: ["health"]), adminCommand: runner))
        {
            var login = await notAllowed.LoginAsync();
            var response = await notAllowed.Handler.HandleAsync(notAllowed.Request(
                "POST", "/api/commands", body: Body("rm", "-rf", "/"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(403, response.StatusCode);
            Assert.Equal("command.not_allowed", Code(response));
            Assert.Empty(runner.Calls);
            Assert.Contains(
                notAllowed.Audit.Read(50),
                entry => entry.Action == "command.run" && entry.Result == "denied" && entry.Target == "rm");
        }
    }

    [Fact]
    public async Task Status_endpoint_describes_the_console_and_host_availability()
    {
        var runner = new FakeAdminCommandRunner();
        using var harness = WebConsoleHarness.Create(Runnable(allowlist: ["health"]), adminCommand: runner);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/commands", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.True(json.GetProperty("runnerAvailable").GetBoolean());
        Assert.Equal(10, json.GetProperty("timeoutSeconds").GetInt32());
        Assert.Single(json.GetProperty("allowlist").EnumerateArray());
    }
}
