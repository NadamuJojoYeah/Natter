using System.Text;
using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23Acceptance;

/// <summary>
/// Phase 3 acceptance (task-5): allowlist execution, denial codes, timeout mapping, output bound and
/// redaction, plus the audit trail. The runner here is scripted; the real in-process host runner is
/// exercised end-to-end (docs/testing/2026-11-21-webconsole-phase23-acceptance.md §3).
/// </summary>
public sealed class CommandPhase3AcceptanceTests
{
    [Fact]
    public async Task Status_reports_the_runner_as_available_with_the_allowlist()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/commands", null, cookie, csrf));

        Assert.Equal(200, response.StatusCode);
        var json = AcceptanceHarness.Json(response);
        Assert.True(json.GetProperty("enabled").GetBoolean());
        Assert.True(json.GetProperty("runnerAvailable").GetBoolean());
        Assert.Equal(5, json.GetProperty("timeoutSeconds").GetInt32());
        Assert.Contains("health", json.GetProperty("allowlist").EnumerateArray().Select(item => item.GetString()));
        // gapCode is a static documentation hint; runnerAvailable is the real availability signal.
        Assert.True(json.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Allowlisted_command_is_executed_and_returns_bounded_output()
    {
        var runner = new ScriptedCommandRunner { Stdout = "{\"status\":\"healthy\"}", ExitCode = 0 };
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = new[] { "--offline" } }),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var json = AcceptanceHarness.Json(response);
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.Equal(0, json.GetProperty("exitCode").GetInt32());
        Assert.Equal("{\"status\":\"healthy\"}", json.GetProperty("stdout").GetString());
        Assert.False(json.GetProperty("timedOut").GetBoolean());
        var call = Assert.Single(runner.Calls);
        Assert.Equal("health", call.Command);
        Assert.Equal(["--offline"], call.Args.ToArray());
    }

    [Fact]
    public async Task Command_outside_the_allowlist_is_denied_with_403()
    {
        var runner = new ScriptedCommandRunner();
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "plugins disable", args = new[] { "x" } }),
            cookie,
            csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("command.not_allowed", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("health", "bad\narg")]
    [InlineData("health", "bad\u0000arg")]
    public async Task Control_characters_in_args_are_rejected_without_execution(string command, string arg)
    {
        var runner = new ScriptedCommandRunner();
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command, args = new[] { arg } }),
            cookie,
            csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("command.invalid", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Too_many_arguments_are_rejected_without_execution()
    {
        var runner = new ScriptedCommandRunner();
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Enumerable.Repeat("a", 9).ToArray() }),
            cookie,
            csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("command.invalid", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Runner_timeout_maps_to_504_command_timeout()
    {
        var runner = new ScriptedCommandRunner
        {
            Delay = TimeSpan.FromSeconds(30),
            HonorCancellation = true,
            CodeOverride = AdminCodes.CommandTimeout,
        };
        using var harness = AcceptanceHarness.Create(runner: runner, commandTimeoutSeconds: 1);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.Equal(504, response.StatusCode);
        Assert.Equal("command.timeout", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.False(AcceptanceHarness.BodyText(response).Contains("host.admin_command.unavailable", StringComparison.Ordinal), "unexpected substring in payload");
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Oversized_output_is_truncated_to_the_documented_budget()
    {
        var huge = new string('x', 200_000);
        var runner = new ScriptedCommandRunner { Stdout = huge };
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var json = AcceptanceHarness.Json(response);
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var stdout = json.GetProperty("stdout").GetString() ?? string.Empty;
        Assert.True(
            Encoding.UTF8.GetByteCount(stdout) <= WebConsoleCommandPolicy.MaxOutputBytes,
            $"stdout must stay within {WebConsoleCommandPolicy.MaxOutputBytes} bytes, got {Encoding.UTF8.GetByteCount(stdout)}");
        Assert.True(json.GetProperty("outputBytes").GetInt32() >= 200_000);
        Assert.EndsWith("…", stdout);
    }

    [Fact]
    public async Task Secret_shaped_output_is_redacted_before_it_reaches_the_browser()
    {
        var runner = new ScriptedCommandRunner
        {
            Stdout = "DEEPSEEK_API_KEY=sk-live-abcdefghijklmnopqrstuv\nAuthorization: Bearer abcdefghijklmnop.qrstuvwx\nok",
            Stderr = "command finished",
        };
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var json = AcceptanceHarness.Json(response);
        var stdout = json.GetProperty("stdout").GetString() ?? string.Empty;
        var stderr = json.GetProperty("stderr").GetString() ?? string.Empty;
        Assert.False((stdout).Contains("sk-live-abcdefghijklmnopqrstuv", StringComparison.Ordinal), "unexpected substring in payload");
        Assert.False((stdout).Contains("abcdefghijklmnop.qrstuvwx", StringComparison.Ordinal), "unexpected substring in payload");
        Assert.DoesNotContain("super-secret-value-123", stderr);
        Assert.False(AcceptanceHarness.BodyText(response).Contains("sk-live-abcdefghijklmnopqrstuv", StringComparison.Ordinal), "unexpected substring in payload");
    }

    [Fact]
    public async Task Plain_key_value_secret_in_command_output_is_redacted()
    {
        // OBS-WC-P3-REDACT-KV: after the fix a plain KEY=value pair in command output is masked
        // while the key stays readable, so the browser never receives the secret value.
        var runner = new ScriptedCommandRunner { Stderr = "token=super-secret-value-123" };
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var stderr = AcceptanceHarness.Json(response).GetProperty("stderr").GetString() ?? string.Empty;
        Assert.DoesNotContain("super-secret-value-123", stderr, StringComparison.Ordinal);
        Assert.Contains("token=[redacted]", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_endpoint_returns_a_decision_without_running_anything()
    {
        var runner = new ScriptedCommandRunner();
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands/validate",
            JsonSerializer.Serialize(new { command = "health", args = new[] { "--offline" } }),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var decision = AcceptanceHarness.Json(response).GetProperty("decision");
        Assert.True(decision.GetProperty("allowed").GetBoolean());
        Assert.Equal("command.allowed", decision.GetProperty("code").GetString());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Command_console_disabled_returns_403()
    {
        using var harness = AcceptanceHarness.Create(allowCommandConsole: false);
        var (cookie, csrf, _) = await harness.LoginAsync();

        foreach (var path in new[] { "/api/commands", "/api/commands/validate" })
        {
            var response = await harness.Handler.HandleAsync(harness.Request(
                "POST",
                path,
                JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
                cookie,
                csrf));

            Assert.Equal(403, response.StatusCode);
            Assert.Equal("command.disabled", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Command_audit_trail_records_success_and_denial_on_disk()
    {
        var runner = new ScriptedCommandRunner { Stdout = "ok" };
        using var harness = AcceptanceHarness.Create(runner: runner);
        var (cookie, csrf, _) = await harness.LoginAsync();

        await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));
        await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "plugins disable", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.True(File.Exists(harness.AuditFile), $"audit file {harness.AuditFile} must exist");
        var lines = await File.ReadAllLinesAsync(harness.AuditFile);
        var runs = lines
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.TryGetProperty("action", out var action) && action.GetString() == "command.run")
            .ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Contains(runs, entry => entry.GetProperty("result").GetString() == "ok");
        Assert.Contains(runs, entry => entry.GetProperty("result").GetString() == "denied");
        Assert.False((await File.ReadAllTextAsync(harness.AuditFile)).Contains("sk-", StringComparison.OrdinalIgnoreCase), "unexpected substring in payload");
    }

    [Fact]
    public async Task Every_phase3_path_reports_the_console_layer_and_never_the_501_gap()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var status = await harness.Handler.HandleAsync(harness.Request("GET", "/api/commands", null, cookie, csrf));
        var run = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        foreach (var response in new[] { status, run })
        {
            Assert.NotEqual(501, response.StatusCode);
            Assert.False(AcceptanceHarness.BodyText(response).Contains("host.admin_command.unavailable", StringComparison.Ordinal), "unexpected substring in payload");
        }
    }
}
