using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// Viewer/admin separation is enforced on the server, not only in the UI: a viewer session may read
/// everything (and run the two non-mutating <c>/validate</c> checks) but every mutating endpoint
/// answers 403 <c>auth.forbidden</c> before the read-only or feature switches are consulted.
/// </summary>
public sealed class RoleEnforcementTests
{
    private static WebConsoleOptions Viewable(bool readOnly = false, bool allowWrites = true) => new()
    {
        Port = 5090,
        ReadOnly = readOnly,
        AllowConfigWrite = allowWrites,
        AllowCommandConsole = allowWrites,
        CommandAllowlist = ["health"],
        ViewerToken = WebConsoleDefaults.ViewerTokenReference,
    };

    private static string Code(WebConsoleHttpResponse response)
        => LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    [Fact]
    public async Task Viewer_login_creates_a_read_only_session()
    {
        using var harness = WebConsoleHarness.Create(Viewable());

        var login = await harness.LoginViewerAsync();

        Assert.Equal(200, login.Response.StatusCode);
        var json = LoginResult.Json(login.Response);
        Assert.Equal("viewer", json.GetProperty("role").GetString());
        Assert.False(json.GetProperty("permissions").GetProperty("write").GetBoolean());
        Assert.False(json.GetProperty("permissions").GetProperty("command").GetBoolean());
        Assert.True(json.GetProperty("viewerLoginEnabled").GetBoolean());

        var session = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/session", cookie: login.Cookie));
        var sessionJson = LoginResult.Json(session);
        Assert.Equal("viewer", sessionJson.GetProperty("role").GetString());
        Assert.True(sessionJson.GetProperty("viewerLoginEnabled").GetBoolean());
    }

    [Fact]
    public async Task Admin_login_still_gets_the_admin_role()
    {
        using var harness = WebConsoleHarness.Create(Viewable());

        var login = await harness.LoginAsync();
        var json = LoginResult.Json(login.Response);

        Assert.Equal("admin", json.GetProperty("role").GetString());
        Assert.True(json.GetProperty("permissions").GetProperty("write").GetBoolean());
    }

    [Fact]
    public async Task Viewer_cannot_save_or_rollback_config()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{\"schemaVersion\":1}");
        using var harness = WebConsoleHarness.Create(Viewable(), adminConfig: store);
        var viewer = await harness.LoginViewerAsync();

        var save = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: JsonSerializer.Serialize(new { content = "{\"schemaVersion\":2}" }),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(403, save.StatusCode);
        Assert.Equal("auth.forbidden", Code(save));
        Assert.Equal("{\"schemaVersion\":1}", store.ContentOf("profiles"));

        var rollback = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/rollback",
            body: "{}",
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(403, rollback.StatusCode);
        Assert.Equal("auth.forbidden", Code(rollback));
        Assert.Contains(
            harness.Audit.Read(50),
            entry => entry.Action == "auth.role_denied" && entry.User == "viewer" && entry.Result == "denied");
    }

    [Fact]
    public async Task Viewer_cannot_run_commands_even_when_the_console_is_enabled()
    {
        var runner = new FakeAdminCommandRunner();
        using var harness = WebConsoleHarness.Create(Viewable(), adminCommand: runner);
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands",
            body: JsonSerializer.Serialize(new { command = "health" }),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("auth.forbidden", Code(response));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Viewer_can_read_every_section_and_validate_without_writing()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{}");
        using var harness = WebConsoleHarness.Create(Viewable(), adminConfig: store, logReader: new FakeAdminLogReader());
        var viewer = await harness.LoginViewerAsync();

        foreach (var path in new[]
                 {
                     "/api/snapshot", "/api/health", "/api/plugins", "/api/upstreams", "/api/profiles",
                     "/api/logs", "/api/audit", "/api/config", "/api/admin", "/api/admin/documents",
                     "/api/commands", "/api/config/profiles", "/api/config/profiles/undo",
                 })
        {
            var response = await harness.Handler.HandleAsync(harness.Request("GET", path, cookie: viewer.Cookie));
            Assert.Equal(200, response.StatusCode);
        }

        var configValidate = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/validate",
            body: JsonSerializer.Serialize(new { content = "{}" }),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));
        Assert.Equal(200, configValidate.StatusCode);

        var commandValidate = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/commands/validate",
            body: JsonSerializer.Serialize(new { command = "health" }),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));
        Assert.Equal(200, commandValidate.StatusCode);
    }

    [Fact]
    public async Task Viewer_posts_still_require_csrf()
    {
        using var harness = WebConsoleHarness.Create(Viewable());
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles/validate",
            body: JsonSerializer.Serialize(new { content = "{}" }),
            cookie: viewer.Cookie));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("csrf.invalid", Code(response));
    }

    [Fact]
    public async Task Viewer_token_is_rejected_when_viewer_login_is_not_configured()
    {
        using var harness = WebConsoleHarness.Create(); // no viewerToken in options

        var response = await harness.LoginViewerAsync();

        Assert.Equal(401, response.Response.StatusCode);
        Assert.Equal("auth.invalid_token", Code(response.Response));
    }

    [Fact]
    public async Task Role_denial_wins_over_the_read_only_code()
    {
        // readOnly=true would also refuse the write; a viewer must see the authorization code first.
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{}");
        using var harness = WebConsoleHarness.Create(Viewable(readOnly: true), adminConfig: store);
        var viewer = await harness.LoginViewerAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: JsonSerializer.Serialize(new { content = "{}" }),
            cookie: viewer.Cookie,
            csrf: viewer.Csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("auth.forbidden", Code(response));
    }

    [Fact]
    public void Credential_resolution_rules()
    {
        var admin = new WebConsoleTokenAuthenticator(WebConsoleHarness.Token);

        // No reference configured -> viewer logins disabled, no problem reported.
        Assert.Null(WebConsoleCredentials.ResolveViewer(null, new FixedSecretProvider(WebConsoleHarness.ViewerToken), admin, out var none));
        Assert.Null(none);

        // Reference configured but secret missing -> documented problem.
        Assert.Null(WebConsoleCredentials.ResolveViewer(
            WebConsoleDefaults.ViewerTokenReference,
            new MissingSecretProvider(),
            admin,
            out var missing));
        Assert.Equal(WebConsoleCredentials.ViewerSecretMissing, missing);

        // Viewer secret equals the admin secret -> refuse instead of silently granting admin.
        Assert.Null(WebConsoleCredentials.ResolveViewer(
            WebConsoleDefaults.ViewerTokenReference,
            new FixedSecretProvider(WebConsoleHarness.Token),
            admin,
            out var duplicate));
        Assert.Equal(WebConsoleCredentials.ViewerTokenDuplicatesAdmin, duplicate);

        // Too short -> unusable.
        Assert.Null(WebConsoleCredentials.ResolveViewer(
            WebConsoleDefaults.ViewerTokenReference,
            new FixedSecretProvider("short"),
            admin,
            out var unusable));
        Assert.Equal(WebConsoleCredentials.ViewerTokenUnusable, unusable);

        // Valid distinct secret -> usable viewer authenticator.
        var viewer = WebConsoleCredentials.ResolveViewer(
            WebConsoleDefaults.ViewerTokenReference,
            new FixedSecretProvider(WebConsoleHarness.ViewerToken),
            admin,
            out var ok);
        Assert.NotNull(viewer);
        Assert.Null(ok);
        Assert.True(viewer!.Verify(WebConsoleHarness.ViewerToken));
        Assert.False(viewer.Verify(WebConsoleHarness.Token));
    }

    /// <summary>Secret provider returning one value for every requested name.</summary>
    private sealed class FixedSecretProvider(string value) : Natter.Plugin.Abstractions.IPluginSecrets
    {
        public string GetRequired(string name)
            => Get(name) ?? throw new Natter.Plugin.Abstractions.PluginSecretException($"missing '{name}'");

        public string? Get(string name) => value;
    }

    /// <summary>Secret provider that only knows the admin token, so the viewer secret is absent.</summary>
    private sealed class MissingSecretProvider : Natter.Plugin.Abstractions.IPluginSecrets
    {
        public string GetRequired(string name)
            => name == WebConsoleDefaults.TokenSecretName
                ? WebConsoleHarness.Token
                : throw new Natter.Plugin.Abstractions.PluginSecretException($"missing '{name}'");

        public string? Get(string name) => null;
    }
}
