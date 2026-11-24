using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// One refusal vocabulary for the console modes:
/// <c>auth.forbidden</c> (role) → <c>console.read_only</c> (global switch) →
/// <c>config.write_disabled</c> / <c>command.disabled</c> (feature switch), while the non-mutating
/// <c>/validate</c> checks stay usable in read-only mode.
/// </summary>
public sealed class ReadOnlyCodesTests
{
    private const string ProfilesBody = "{\"content\":\"{\\\"schemaVersion\\\":1}\"}";

    private static string Code(WebConsoleHttpResponse response)
        => LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    private static WebConsoleOptions ReadOnly() => new()
    {
        Port = 5090,
        ReadOnly = true,
        AllowConfigWrite = false,
        AllowCommandConsole = false,
        CommandAllowlist = ["health"],
    };

    private static WebConsoleOptions Writable(bool allowConfigWrite, bool allowCommands) => new()
    {
        Port = 5090,
        ReadOnly = false,
        AllowConfigWrite = allowConfigWrite,
        AllowCommandConsole = allowCommands,
        CommandAllowlist = ["health"],
    };

    [Fact]
    public async Task Read_only_mode_uses_one_code_for_every_mutating_endpoint()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{\"schemaVersion\":1}");
        using var harness = WebConsoleHarness.Create(ReadOnly(), adminConfig: store, adminCommand: new FakeAdminCommandRunner());
        var login = await harness.LoginAsync();

        var save = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: ProfilesBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(403, save.StatusCode);
        Assert.Equal("console.read_only", Code(save));

        var rollback = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/rollback", body: "{}", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(403, rollback.StatusCode);
        Assert.Equal("console.read_only", Code(rollback));

        var run = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: JsonSerializer.Serialize(new { command = "health" }), cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(403, run.StatusCode);
        Assert.Equal("console.read_only", Code(run));

        Assert.Equal(3, harness.Audit.Read(50).Count(entry => entry.Action == "console.read_only"));
    }

    [Fact]
    public async Task Feature_switches_keep_their_own_codes_when_the_console_is_writable()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{\"schemaVersion\":1}");
        using var harness = WebConsoleHarness.Create(
            Writable(allowConfigWrite: false, allowCommands: false),
            adminConfig: store,
            adminCommand: new FakeAdminCommandRunner());
        var login = await harness.LoginAsync();

        var save = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: ProfilesBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal("config.write_disabled", Code(save));

        var run = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: JsonSerializer.Serialize(new { command = "health" }), cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal("command.disabled", Code(run));

        var validate = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands/validate", body: JsonSerializer.Serialize(new { command = "health" }), cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal("command.disabled", Code(validate));
    }

    [Fact]
    public async Task Validate_endpoints_stay_usable_in_read_only_mode()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = true,
            AllowConfigWrite = true,
            AllowCommandConsole = true,
            CommandAllowlist = ["health"],
        });
        var login = await harness.LoginAsync();

        var configValidate = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: ProfilesBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(200, configValidate.StatusCode);

        var commandValidate = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands/validate", body: JsonSerializer.Serialize(new { command = "health" }), cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(200, commandValidate.StatusCode);
    }

    [Fact]
    public async Task Read_only_mode_does_not_break_method_and_route_semantics()
    {
        using var harness = WebConsoleHarness.Create(ReadOnly());
        var login = await harness.LoginAsync();

        var wrongMethod = await harness.Handler.HandleAsync(harness.Request(
            "DELETE", "/api/config/profiles", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(405, wrongMethod.StatusCode);
        Assert.Equal("GET, POST", wrongMethod.Headers["Allow"]);

        var unknown = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/nope", cookie: login.Cookie));
        Assert.Equal(404, unknown.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_still_401()
    {
        using var harness = WebConsoleHarness.Create(ReadOnly());

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: JsonSerializer.Serialize(new { command = "health" })));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("auth.required", Code(response));
    }

    [Fact]
    public async Task Session_payload_reports_the_effective_permissions()
    {
        using var readOnly = WebConsoleHarness.Create(ReadOnly());
        var readOnlyLogin = await readOnly.LoginAsync();
        var readOnlySession = LoginResult.Json(await readOnly.Handler.HandleAsync(readOnly.Request(
            "GET", "/api/session", cookie: readOnlyLogin.Cookie)));

        Assert.False(readOnlySession.GetProperty("permissions").GetProperty("write").GetBoolean());
        Assert.False(readOnlySession.GetProperty("permissions").GetProperty("command").GetBoolean());

        using var writable = WebConsoleHarness.Create(Writable(allowConfigWrite: true, allowCommands: true));
        var writableLogin = await writable.LoginAsync();
        var writableSession = LoginResult.Json(await writable.Handler.HandleAsync(writable.Request(
            "GET", "/api/session", cookie: writableLogin.Cookie)));

        Assert.True(writableSession.GetProperty("permissions").GetProperty("write").GetBoolean());
        Assert.True(writableSession.GetProperty("permissions").GetProperty("command").GetBoolean());
    }
}
