using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23Acceptance;

/// <summary>
/// Cross-cutting acceptance (task-5): with the host contracts present nothing may answer 501; with a
/// contract missing the documented gap code is still returned instead of fabricated data. Auth and
/// CSRF boundaries stay in force for every write path.
/// </summary>
public sealed class HostContractAcceptanceTests
{
    [Fact]
    public async Task Admin_status_advertises_both_host_contracts()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var admin = AcceptanceHarness.Json(await harness.Handler.HandleAsync(
            harness.Request("GET", "/api/admin", null, cookie, csrf)));
        Assert.True(admin.GetProperty("configStoreAvailable").GetBoolean());
        Assert.True(admin.GetProperty("commandRunnerAvailable").GetBoolean());

        var config = AcceptanceHarness.Json(await harness.Handler.HandleAsync(
            harness.Request("GET", "/api/config", null, cookie, csrf)));
        // writeEnabled reflects the plugin configuration; real availability is the admin flag below.
        Assert.True(config.GetProperty("writeEnabled").GetBoolean());
        Assert.True(config.GetProperty("admin").GetProperty("configStoreAvailable").GetBoolean());
        Assert.True(config.GetProperty("admin").GetProperty("commandRunnerAvailable").GetBoolean());
        Assert.NotEmpty(config.GetProperty("documents").EnumerateArray());

        var commands = AcceptanceHarness.Json(await harness.Handler.HandleAsync(
            harness.Request("GET", "/api/commands", null, cookie, csrf)));
        Assert.True(commands.GetProperty("runnerAvailable").GetBoolean());
    }

    [Fact]
    public async Task Configured_host_never_answers_501_on_the_config_write_path()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var content = JsonSerializer.Serialize(new { schemaVersion = 1, profiles = Array.Empty<object>() });

        var save = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            JsonSerializer.Serialize(new { content, etag = (string?)null }),
            cookie,
            csrf));

        Assert.NotEqual(501, save.StatusCode);
        Assert.False(AcceptanceHarness.BodyText(save).Contains("host.admin_config.unavailable", StringComparison.Ordinal), "unexpected substring in payload");
    }

    [Fact]
    public async Task Missing_config_store_falls_back_to_the_documented_501_gap()
    {
        using var harness = AcceptanceHarness.Create(withStore: false);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var save = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            JsonSerializer.Serialize(new { content = "{}", etag = (string?)null }),
            cookie,
            csrf));
        Assert.Equal(501, save.StatusCode);
        Assert.Equal("host.admin_config.unavailable", AcceptanceHarness.Json(save).GetProperty("error").GetProperty("code").GetString());

        var config = AcceptanceHarness.Json(await harness.Handler.HandleAsync(
            harness.Request("GET", "/api/config", null, cookie, csrf)));
        Assert.False(config.GetProperty("admin").GetProperty("configStoreAvailable").GetBoolean());
        Assert.Empty(config.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task Missing_command_runner_falls_back_to_the_documented_501_gap()
    {
        using var harness = AcceptanceHarness.Create(withRunner: false);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var run = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            JsonSerializer.Serialize(new { command = "health", args = Array.Empty<string>() }),
            cookie,
            csrf));

        Assert.Equal(501, run.StatusCode);
        Assert.Equal("host.admin_command.unavailable", AcceptanceHarness.Json(run).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("POST", "/api/config/profiles")]
    [InlineData("POST", "/api/config/profiles/rollback")]
    [InlineData("POST", "/api/commands")]
    [InlineData("POST", "/api/commands/validate")]
    public async Task Unauthenticated_writes_are_rejected_with_401(string method, string path)
    {
        using var harness = AcceptanceHarness.Create();

        var response = await harness.Handler.HandleAsync(harness.Request(method, path, "{}"));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("auth.required", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("POST", "/api/config/profiles")]
    [InlineData("POST", "/api/config/profiles/rollback")]
    [InlineData("POST", "/api/commands")]
    public async Task Writes_without_the_csrf_header_are_rejected_with_403(string method, string path)
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, _, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(method, path, "{}", cookie));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("csrf.invalid", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Config_snapshot_redacts_secret_values_while_writes_are_enabled()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/config", null, cookie, csrf));

        Assert.Equal(200, response.StatusCode);
        Assert.False(AcceptanceHarness.BodyText(response).Contains("hunter2", StringComparison.Ordinal), "unexpected substring in payload");
        Assert.False(AcceptanceHarness.BodyText(response).Contains("sk-abcdefghijklmnop", StringComparison.Ordinal), "unexpected substring in payload");
        Assert.True(AcceptanceHarness.BodyText(response).Contains("${secret:", StringComparison.Ordinal), "expected substring missing from payload");
    }
}
