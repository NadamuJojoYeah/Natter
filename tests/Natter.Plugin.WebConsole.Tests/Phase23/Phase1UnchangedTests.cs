using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// Phase 1 must stay byte-compatible when the host admin surface is absent (no <c>admin.*</c>
/// permission, admin surface disabled, or an older host), and must improve — not regress — when the
/// admin read contract is bridged (plugin runtime diagnostics and host log tail).
/// </summary>
public sealed class Phase1UnchangedTests
{
    [Fact]
    public async Task Snapshot_still_reports_the_admin_gaps_without_host_services()
    {
        using var harness = WebConsoleHarness.Create();

        var snapshot = await harness.Diagnostics.CreateSnapshotAsync();

        Assert.False(snapshot.Admin.ConfigStoreAvailable);
        Assert.False(snapshot.Admin.CommandRunnerAvailable);
        Assert.False(snapshot.Admin.LogReaderAvailable);
        // Default options are read-only, so the plugin switch — not the host — reports the gap.
        Assert.Equal("webconsole.write_disabled", snapshot.Admin.ConfigGapCode);
        Assert.Equal("webconsole.command_disabled", snapshot.Admin.CommandGapCode);
        Assert.Equal("host.logs.unavailable", snapshot.Logs.GapCode);
        Assert.Contains(snapshot.Gaps, gap => gap.Code == "host.admin_config.unavailable");
        Assert.Contains(snapshot.Gaps, gap => gap.Code == "host.admin_command.unavailable");
        Assert.Contains(snapshot.Gaps, gap => gap.Code == "host.logs.unavailable");
    }

    [Fact]
    public async Task Write_gap_reflects_the_host_surface_and_the_plugin_switch()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{}");

        using (var readOnly = WebConsoleHarness.Create(adminConfig: store))
        {
            var snapshot = await readOnly.Diagnostics.CreateSnapshotAsync();
            // readOnly=true wins: writing is disabled by plugin configuration, not by the host.
            Assert.Equal("webconsole.write_disabled", snapshot.Config.WriteGapCode);
            Assert.True(snapshot.Admin.ConfigStoreAvailable);
        }

        using (var writable = WebConsoleHarness.Create(
                   new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true },
                   adminConfig: store))
        {
            var snapshot = await writable.Diagnostics.CreateSnapshotAsync();
            Assert.Null(snapshot.Config.WriteGapCode);
        }

        using (var noStore = WebConsoleHarness.Create(
                   new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true }))
        {
            var snapshot = await noStore.Diagnostics.CreateSnapshotAsync();
            Assert.Equal("host.admin_config.unavailable", snapshot.Config.WriteGapCode);
        }
    }

    [Fact]
    public void Static_config_preview_never_claims_a_host_gap()
    {
        var raw = JsonDocument.Parse("""{ "port": 5090 }""").RootElement;

        var writable = WebConsoleConfig.Build(
            raw,
            "plugin",
            schemaPath: null,
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true });
        var readOnly = WebConsoleConfig.Build(raw, "plugin", schemaPath: null, new WebConsoleOptions { Port = 5090 });

        // Host availability is decided by admin.*Available; the static value only mirrors the switch.
        Assert.Null(writable.WriteGapCode);
        Assert.Equal("webconsole.write_disabled", readOnly.WriteGapCode);
    }

    [Fact]
    public async Task Config_http_endpoint_reports_the_host_gap_only_when_the_store_is_missing()
    {
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true });
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request("GET", "/api/config", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = JsonDocument.Parse(response.Body).RootElement;
        Assert.Equal("host.admin_config.unavailable", json.GetProperty("writeGapCode").GetString());
        Assert.False(json.GetProperty("admin").GetProperty("configStoreAvailable").GetBoolean());
    }

    [Fact]
    public async Task Satisfied_gaps_disappear_from_the_console()
    {
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{}");
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true, AllowCommandConsole = true },
            adminConfig: store,
            adminCommand: new FakeAdminCommandRunner(),
            logReader: new FakeAdminLogReader());

        var snapshot = await harness.Diagnostics.CreateSnapshotAsync();

        Assert.DoesNotContain(snapshot.Gaps, gap => gap.Code == "host.admin_config.unavailable");
        Assert.DoesNotContain(snapshot.Gaps, gap => gap.Code == "host.admin_command.unavailable");
        Assert.DoesNotContain(snapshot.Gaps, gap => gap.Code == "host.logs.unavailable");
        // Still documented as missing: no admin kind/contribution and no ASP.NET Core hosting.
        Assert.Contains(snapshot.Gaps, gap => gap.Code == "host.admin_kind.unavailable");
        Assert.Contains(snapshot.Gaps, gap => gap.Code == "host.aspnetcore.unavailable");
    }

    [Fact]
    public async Task Host_log_tail_replaces_the_log_gap_when_bridged()
    {
        var reader = new FakeAdminLogReader();
        using var harness = WebConsoleHarness.Create(logReader: reader);
        var login = await harness.LoginAsync();

        var snapshot = await harness.Diagnostics.CreateSnapshotAsync(auditLimit: 25);

        Assert.True(snapshot.Logs.Available);
        Assert.Null(snapshot.Logs.GapCode);
        var entry = Assert.Single(snapshot.Logs.Data!);
        Assert.Equal("Natter.Host", entry.Source);
        Assert.DoesNotContain("sk-abcdefghijkl", entry.Message, StringComparison.Ordinal);
        Assert.Equal(25, reader.LastLimit);
        Assert.Contains(snapshot.Checks, check => check.Name == "host.logs" && check.Status == "ok");

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/logs", cookie: login.Cookie, query: "limit=5"));

        Assert.Equal(200, response.StatusCode);
        using var document = JsonDocument.Parse(response.Body);
        Assert.True(document.RootElement.GetProperty("hostLogs").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Phase1_routes_keep_their_status_codes_and_shapes()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var session = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/session", cookie: login.Cookie));
        Assert.Equal(200, session.StatusCode);
        Assert.True(LoginResult.Json(session).GetProperty("authenticated").GetBoolean());

        var snapshot = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/snapshot", cookie: login.Cookie));
        Assert.Equal(200, snapshot.StatusCode);
        var json = LoginResult.Json(snapshot);
        Assert.False(json.GetProperty("health").GetProperty("available").GetBoolean());
        Assert.False(json.GetProperty("logs").GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("self").GetProperty("tokenConfigured").GetBoolean());

        var index = await harness.Handler.HandleAsync(harness.Request("GET", "/"));
        Assert.Equal(200, index.StatusCode);

        var missing = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/nope", cookie: login.Cookie));
        Assert.Equal(404, missing.StatusCode);

        var wrongMethod = await harness.Handler.HandleAsync(harness.Request(
            "DELETE", "/api/config/profiles", cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(405, wrongMethod.StatusCode);
    }

    [Fact]
    public void Transport_body_budget_is_route_aware()
    {
        Assert.Equal(WebConsoleDefaults.MaxAdminBodyBytes, WebConsoleServer.ResolveBodyLimit("/api/config/profiles"));
        Assert.Equal(WebConsoleDefaults.MaxAdminBodyBytes, WebConsoleServer.ResolveBodyLimit("/api/config/plugin:x/validate"));
        Assert.Equal(WebConsoleDefaults.MaxRequestBodyBytes, WebConsoleServer.ResolveBodyLimit("/api/commands"));
        Assert.Equal(WebConsoleDefaults.MaxRequestBodyBytes, WebConsoleServer.ResolveBodyLimit("/api/session"));
        Assert.True(WebConsoleDefaults.MaxAdminBodyBytes > WebConsoleDefaults.MaxRequestBodyBytes);
    }

    [Fact]
    public async Task Oversized_config_document_is_accepted_by_the_handler_budget()
    {
        // 128 KiB document: above the generic 64 KiB transport cap, below the 256 KiB config cap.
        var content = "{\"data\":\"" + new string('y', 128 * 1024) + "\"}";
        var store = new FakeAdminConfigStore().WithDocument("profiles", "{}");
        using var harness = WebConsoleHarness.Create(
            new WebConsoleOptions { Port = 5090, ReadOnly = false, AllowConfigWrite = true },
            adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: JsonSerializer.Serialize(new { content, etag = Natter.Application.Admin.AdminETag.Compute("{}") }),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(content, store.ContentOf("profiles"));
    }
}
