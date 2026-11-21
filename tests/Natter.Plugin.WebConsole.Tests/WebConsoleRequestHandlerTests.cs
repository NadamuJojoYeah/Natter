using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleRequestHandlerTests
{
    private const string Origin = "http://127.0.0.1:5090";

    [Fact]
    public async Task Index_serves_static_html_with_security_headers()
    {
        using var harness = WebConsoleHarness.Create();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/"));

        Assert.Equal(200, response.StatusCode);
        Assert.StartsWith("text/html", response.ContentType);
        Assert.Contains("default-src 'none'", response.Headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task Snapshot_requires_login()
    {
        using var harness = WebConsoleHarness.Create();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/snapshot"));

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("auth.required", LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_rejects_wrong_token_and_audits()
    {
        using var harness = WebConsoleHarness.Create();

        var login = await harness.LoginAsync("definitely-wrong-token-value");

        Assert.Equal(401, login.Response.StatusCode);
        Assert.Null(login.Cookie);

        var audit = harness.Audit.Read(10);
        Assert.Contains(audit, entry => entry.Action == "auth.login" && entry.Result == "denied");
    }

    [Fact]
    public async Task Login_sets_strict_cookie_and_csrf()
    {
        using var harness = WebConsoleHarness.Create();

        var login = await harness.LoginAsync();

        Assert.Equal(200, login.Response.StatusCode);
        Assert.NotNull(login.Cookie);
        Assert.NotNull(login.Csrf);
        Assert.Contains("HttpOnly", login.Response.Headers["Set-Cookie"]);
        Assert.Contains("SameSite=Strict", login.Response.Headers["Set-Cookie"]);
        Assert.DoesNotContain(WebConsoleHarness.Token, login.Response.Headers["Set-Cookie"]);
    }

    [Fact]
    public async Task Repeated_failures_rate_limit_even_correct_token()
    {
        using var harness = WebConsoleHarness.Create();

        for (var index = 0; index < WebConsoleLoginThrottle.MaxFailuresPerClient; index++)
        {
            var failed = await harness.LoginAsync("wrong-token-" + index);
            Assert.Equal(401, failed.Response.StatusCode);
        }

        var blocked = await harness.LoginAsync();

        Assert.Equal(429, blocked.Response.StatusCode);
        Assert.True(blocked.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Session_endpoint_reports_authenticated_state()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var anonymous = await harness.Handler.HandleAsync(harness.Request("GET", "/api/session"));
        Assert.Equal(401, anonymous.StatusCode);

        var authenticated = await harness.Handler.HandleAsync(harness.Request("GET", "/api/session", cookie: login.Cookie));
        Assert.Equal(200, authenticated.StatusCode);
        Assert.True(LoginResult.Json(authenticated).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Snapshot_returns_sections_and_audit()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/snapshot", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = LoginResult.Json(response);
        Assert.Equal("natter.plugin.webconsole", json.GetProperty("self").GetProperty("pluginId").GetString());
        Assert.False(json.GetProperty("health").GetProperty("available").GetBoolean());
        Assert.Equal("host.plugin_diagnostics.unavailable",
            json.GetProperty("plugins").GetProperty("gapCode").GetString());
        Assert.True(json.GetProperty("gaps").GetArrayLength() >= 5);
        Assert.Contains(harness.Audit.Read(10), entry => entry.Action == "snapshot.view");
    }

    [Fact]
    public async Task Config_preview_masks_secrets()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = LoginResult.Json(response);
        Assert.Equal("***", json.GetProperty("values").GetProperty("nested").GetProperty("password").GetString());
        Assert.Equal("${secret:WEBCONSOLE_TOKEN}", json.GetProperty("values").GetProperty("token").GetString());
        Assert.False(json.GetProperty("writeEnabled").GetBoolean());
    }

    [Fact]
    public async Task Config_write_requires_csrf_then_reports_disabled_or_gap()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var withoutCsrf = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: "{}", cookie: login.Cookie));
        Assert.Equal(403, withoutCsrf.StatusCode);
        Assert.Equal("csrf.invalid", LoginResult.Json(withoutCsrf).GetProperty("error").GetProperty("code").GetString());

        // readOnly=true now reports the unified console.read_only code (see task-14).
        var readonlyResponse = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: "{}", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(403, readonlyResponse.StatusCode);
        Assert.Equal("console.read_only",
            LoginResult.Json(readonlyResponse).GetProperty("error").GetProperty("code").GetString());

        using var writable = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowConfigWrite = true,
        });
        var writableLogin = await writable.LoginAsync();
        var gap = await writable.Handler.HandleAsync(writable.Request(
            "POST",
            "/api/config/plugin:natter.plugin.webconsole",
            body: JsonSerializer.Serialize(new { content = "{}" }),
            cookie: writableLogin.Cookie,
            csrf: writableLogin.Csrf));

        Assert.Equal(501, gap.StatusCode);
        Assert.Equal("host.admin_config.unavailable", LoginResult.Json(gap).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Config_id_whitelist_rejects_paths()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowConfigWrite = true,
        });
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/../../etc/passwd", body: "{}", cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("config.id_invalid", LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Command_console_reports_disabled_and_gap()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var disabled = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: "{\"command\":\"health --json\"}", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(403, disabled.StatusCode);

        using var enabled = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowCommandConsole = true,
            CommandAllowlist = ["health"],
        });
        var enabledLogin = await enabled.LoginAsync();
        var gap = await enabled.Handler.HandleAsync(enabled.Request(
            "POST",
            "/api/commands",
            body: JsonSerializer.Serialize(new { command = "health", args = new[] { "--json" } }),
            cookie: enabledLogin.Cookie,
            csrf: enabledLogin.Csrf));

        Assert.Equal(501, gap.StatusCode);
        Assert.Equal("host.admin_command.unavailable", LoginResult.Json(gap).GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(enabled.Audit.Read(20), entry => entry.Action == "command.run" && entry.Result == "unavailable");
    }

    [Fact]
    public async Task Logout_requires_csrf_then_expires_session()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var withoutCsrf = await harness.Handler.HandleAsync(harness.Request(
            "DELETE", "/api/session", cookie: login.Cookie));
        Assert.Equal(403, withoutCsrf.StatusCode);

        var logout = await harness.Handler.HandleAsync(harness.Request(
            "DELETE", "/api/session", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(200, logout.StatusCode);
        Assert.Contains("Max-Age=0", logout.Headers["Set-Cookie"]);

        var after = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/snapshot", cookie: login.Cookie));
        Assert.Equal(401, after.StatusCode);
    }

    [Fact]
    public async Task Cross_origin_requests_are_rejected()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/snapshot", cookie: login.Cookie, origin: "https://evil.example"));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("request.origin_denied", LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Same_origin_header_is_accepted()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/snapshot", cookie: login.Cookie, origin: Origin));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_and_traversal_paths_return_404()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var traversal = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/../plugin.json", cookie: login.Cookie));
        Assert.Equal(404, traversal.StatusCode);

        var unknown = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/does-not-exist", cookie: login.Cookie));
        Assert.Equal(404, unknown.StatusCode);
    }

    [Fact]
    public async Task Wrong_method_returns_405_with_allow_header()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "PUT", "/api/snapshot", body: "{}", cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(405, response.StatusCode);
        Assert.Equal("GET", response.Headers["Allow"]);
    }

    [Fact]
    public async Task Logs_endpoint_returns_audit_and_documented_host_gap()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "limit=10"));

        Assert.Equal(200, response.StatusCode);
        var json = LoginResult.Json(response);
        Assert.False(json.GetProperty("hostLogs").GetProperty("available").GetBoolean());
        Assert.Equal("host.logs.unavailable", json.GetProperty("hostLogs").GetProperty("gapCode").GetString());
        Assert.True(json.GetProperty("audit").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Forwarded_for_is_only_trusted_for_configured_proxies()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var trusted = await harness.Handler.HandleAsync(harness.Request(
            "GET",
            "/api/snapshot",
            cookie: login.Cookie,
            ip: "127.0.0.1",
            extraHeaders: new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.7" }));
        Assert.Equal(200, trusted.StatusCode);

        var untrusted = await harness.Handler.HandleAsync(harness.Request(
            "GET",
            "/api/snapshot",
            cookie: login.Cookie,
            ip: "203.0.113.9",
            extraHeaders: new Dictionary<string, string> { ["X-Forwarded-For"] = "198.51.100.7" }));
        Assert.Equal(200, untrusted.StatusCode);

        var audit = harness.Audit.Read(20);
        Assert.Contains(audit, entry => entry.Action == "snapshot.view" && entry.Ip == "198.51.100.7");
        Assert.Contains(audit, entry => entry.Action == "snapshot.view" && entry.Ip == "203.0.113.9");
    }

    [Fact]
    public async Task Config_validate_requires_csrf_and_reports_policy_issues()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();
        var validBody = JsonSerializer.Serialize(new { content = "{\"a\":1}" });

        var withoutCsrf = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: validBody, cookie: login.Cookie));
        Assert.Equal(403, withoutCsrf.StatusCode);

        var valid = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: validBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(200, valid.StatusCode);
        Assert.True(LoginResult.Json(valid).GetProperty("validation").GetProperty("valid").GetBoolean());

        var invalidBody = JsonSerializer.Serialize(new { content = "{oops}" });
        var invalid = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: invalidBody, cookie: login.Cookie, csrf: login.Csrf));
        var validation = LoginResult.Json(invalid).GetProperty("validation");
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validation.GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "config.invalid_json");
    }

    [Fact]
    public async Task Config_validate_rejects_path_traversal_ids()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();
        var body = JsonSerializer.Serialize(new { content = "{}" });

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/../etc/passwd/validate", body: body, cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("config.id_invalid", LoginResult.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Config_validate_flags_plaintext_secret_literals()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();
        var body = JsonSerializer.Serialize(new { content = "{\"token\":\"plain-value\"}" });

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: body, cookie: login.Cookie, csrf: login.Csrf));

        var validation = LoginResult.Json(response).GetProperty("validation");
        Assert.True(validation.GetProperty("containsSecretLiteral").GetBoolean());
        Assert.False(validation.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Config_validate_rejects_oversized_body()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();
        var body = JsonSerializer.Serialize(new
        {
            content = "{\"padding\":\"" + new string('a', WebConsoleConfigEditPolicy.MaxContentBytes + 16 * 1024) + "\"}",
        });

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: body, cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(413, response.StatusCode);
    }

    [Fact]
    public async Task Config_write_validates_before_reporting_host_gap()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowConfigWrite = true,
        });
        var login = await harness.LoginAsync();

        var invalidBody = JsonSerializer.Serialize(new { content = "{oops}" });
        var invalid = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: invalidBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(400, invalid.StatusCode);
        Assert.Equal("config.invalid",
            LoginResult.Json(invalid).GetProperty("error").GetProperty("code").GetString());

        var validBody = JsonSerializer.Serialize(new { content = "{}" });
        var valid = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: validBody, cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(501, valid.StatusCode);
        Assert.Equal("host.admin_config.unavailable",
            LoginResult.Json(valid).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Command_validate_honors_allowlist()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowCommandConsole = true,
            CommandAllowlist = ["health", "plugins list"],
        });
        var login = await harness.LoginAsync();

        var allowedBody = JsonSerializer.Serialize(new { command = "plugins   list", args = new[] { "--detail" } });
        var allowed = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands/validate", body: allowedBody, cookie: login.Cookie, csrf: login.Csrf));
        var allowedDecision = LoginResult.Json(allowed).GetProperty("decision");
        Assert.True(allowedDecision.GetProperty("allowed").GetBoolean());
        Assert.Equal("plugins list", allowedDecision.GetProperty("command").GetString());

        var deniedBody = JsonSerializer.Serialize(new { command = "rm", args = new[] { "-rf" } });
        var denied = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands/validate", body: deniedBody, cookie: login.Cookie, csrf: login.Csrf));
        var deniedDecision = LoginResult.Json(denied).GetProperty("decision");
        Assert.False(deniedDecision.GetProperty("allowed").GetBoolean());
        Assert.Equal("command.not_allowed", deniedDecision.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Command_run_rejects_non_allowlisted_before_reporting_gap()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowCommandConsole = true,
            CommandAllowlist = ["health"],
        });
        var login = await harness.LoginAsync();

        var denied = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands",
            body: JsonSerializer.Serialize(new { command = "rm" }),
            cookie: login.Cookie,
            csrf: login.Csrf));
        Assert.Equal(403, denied.StatusCode);
        Assert.Equal("command.not_allowed",
            LoginResult.Json(denied).GetProperty("error").GetProperty("code").GetString());

        var allowed = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands",
            body: JsonSerializer.Serialize(new { command = "health", args = new[] { "--json" } }),
            cookie: login.Cookie,
            csrf: login.Csrf));
        Assert.Equal(501, allowed.StatusCode);
        Assert.Equal("host.admin_command.unavailable",
            LoginResult.Json(allowed).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Command_body_size_is_limited()
    {
        using var harness = WebConsoleHarness.Create(new WebConsoleOptions
        {
            Port = 5090,
            ReadOnly = false,
            AllowCommandConsole = true,
            CommandAllowlist = ["health"],
        });
        var login = await harness.LoginAsync();
        var body = JsonSerializer.Serialize(new { command = "health", args = new string('a', WebConsoleDefaults.MaxCommandBodyBytes) });

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/commands", body: body, cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(413, response.StatusCode);
    }

    [Fact]
    public async Task Trusted_forwarded_proto_marks_session_cookie_secure()
    {
        using var trusted = WebConsoleHarness.Create();
        var secure = await trusted.LoginAsync(extraHeaders: new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https",
        });
        Assert.Contains("Secure", secure.Response.Headers["Set-Cookie"]);

        using var untrusted = WebConsoleHarness.Create();
        var insecure = await untrusted.Handler.HandleAsync(untrusted.Request(
            "POST",
            "/api/session",
            body: JsonSerializer.Serialize(new { token = WebConsoleHarness.Token }),
            ip: "203.0.113.9",
            extraHeaders: new Dictionary<string, string> { ["X-Forwarded-Proto"] = "https" }));

        Assert.Equal(200, insecure.StatusCode);
        Assert.DoesNotContain("Secure", insecure.Headers["Set-Cookie"]);
    }
}
