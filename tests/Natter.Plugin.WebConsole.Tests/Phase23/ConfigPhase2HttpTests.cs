using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>Phase 2: config read/save/conflict/lock/backup/undo over the real HTTP contract.</summary>
public sealed class ConfigPhase2HttpTests
{
    private const string ProfilesId = "profiles";

    private static WebConsoleOptions Writable() => new()
    {
        Port = 5090,
        ReadOnly = false,
        AllowConfigWrite = true,
    };

    private static string Body(string content, string? etag = null)
        => JsonSerializer.Serialize(new { content, etag });

    private static JsonElement Json(WebConsoleHttpResponse response) => LoginResult.Json(response);

    private static string Code(WebConsoleHttpResponse response)
        => Json(response).GetProperty("error").GetProperty("code").GetString() ?? string.Empty;

    private static string BodyText(WebConsoleHttpResponse response)
        => System.Text.Encoding.UTF8.GetString(response.Body);

    [Fact]
    public async Task Read_returns_document_etag_and_restart_hint()
    {
        const string stored = "{\"schemaVersion\":1,\"profiles\":[]}";
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, stored);
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.Equal(ProfilesId, json.GetProperty("id").GetString());
        Assert.True(json.GetProperty("exists").GetBoolean());
        Assert.Equal(AdminETag.Compute(stored), json.GetProperty("eTag").GetString());
        Assert.Equal(stored, json.GetProperty("content").GetString());
        Assert.Equal("config-directory", json.GetProperty("source").GetString());
        Assert.Equal("restart-host", json.GetProperty("reloadHint").GetString());
        Assert.True(json.GetProperty("restartRequired").GetBoolean());
        Assert.False(json.GetProperty("contentRedacted").GetBoolean());
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "config.read" && entry.Result == "ok");
    }

    [Fact]
    public async Task Read_reports_missing_document_without_error()
    {
        var store = new FakeAdminConfigStore();
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/plugin:natter.plugin.webconsole", cookie: login.Cookie));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.False(json.GetProperty("exists").GetBoolean());
        Assert.Equal(string.Empty, json.GetProperty("content").GetString());
        Assert.Equal("missing", json.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Read_without_host_store_reports_the_documented_gap()
    {
        using var harness = WebConsoleHarness.Create(Writable());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles", cookie: login.Cookie));

        Assert.Equal(501, response.StatusCode);
        Assert.Equal("host.admin_config.unavailable", Code(response));
    }

    [Fact]
    public async Task Read_masks_plaintext_secret_values()
    {
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, "{\"debug\":\"token sk-abcdefghijkl\"}");
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles", cookie: login.Cookie));

        var json = Json(response);
        Assert.True(json.GetProperty("contentRedacted").GetBoolean());
        Assert.Contains("[redacted]", json.GetProperty("content").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijkl", BodyText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_writes_content_returns_backup_and_records_undo()
    {
        const string before = "{\"schemaVersion\":1}";
        const string after = "{\"schemaVersion\":1,\"defaultProfile\":\"tier1\"}";
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, before);
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: Body(after, AdminETag.Compute(before)),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        var json = Json(response);
        Assert.True(json.GetProperty("saved").GetBoolean());
        Assert.Equal(AdminETag.Compute(after), json.GetProperty("eTag").GetString());
        Assert.Equal(".backup/20261121T000000000Z/profiles.json", json.GetProperty("backupPath").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("undoId").GetString()));
        Assert.Equal("restart-host", json.GetProperty("reloadHint").GetString());
        Assert.Equal(after, store.ContentOf(ProfilesId));
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "config.write" && entry.Result == "ok");
    }

    [Fact]
    public async Task Save_with_stale_etag_returns_conflict_and_current_etag()
    {
        const string stored = "{\"schemaVersion\":1}";
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, stored);
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/profiles",
            body: Body("{\"schemaVersion\":2}", AdminETag.Compute("{\"stale\":true}")),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(409, response.StatusCode);
        var json = Json(response);
        Assert.Equal("config.conflict", Code(response));
        Assert.Equal(AdminETag.Compute(stored),
            json.GetProperty("detail").GetProperty("conflictETag").GetString());
        Assert.Equal(stored, store.ContentOf(ProfilesId));
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "config.write" && entry.Result == "denied");
    }

    [Fact]
    public async Task Save_creates_a_missing_document_only_with_null_etag()
    {
        var store = new FakeAdminConfigStore();
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var withEtag = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/plugin:natter.plugin.webconsole",
            body: Body("{\"readOnly\":true}", "sha256:0000000000000000000000000000000000000000000000000000000000000000"),
            cookie: login.Cookie,
            csrf: login.Csrf));
        Assert.Equal(409, withEtag.StatusCode);

        var created = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            "/api/config/plugin:natter.plugin.webconsole",
            body: Body("{\"readOnly\":true}", null),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(200, created.StatusCode);
        Assert.True(Json(created).GetProperty("saved").GetBoolean());
        Assert.Equal("restart-plugin", Json(created).GetProperty("reloadHint").GetString());
        Assert.Equal("{\"readOnly\":true}", store.ContentOf("plugin:natter.plugin.webconsole"));
    }

    [Fact]
    public async Task Save_reports_lock_schema_backup_and_host_failures()
    {
        const string stored = "{\"schemaVersion\":1}";
        var storedEtag = AdminETag.Compute(stored);
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, stored);

        using (var locked = WebConsoleHarness.Create(Writable(), adminConfig: store))
        {
            store.ForceLocked = true;
            var login = await locked.LoginAsync();
            var response = await locked.Handler.HandleAsync(locked.Request(
                "POST", "/api/config/profiles", body: Body("{\"schemaVersion\":2}", storedEtag), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(409, response.StatusCode);
            Assert.Equal("config.locked", Code(response));
            store.ForceLocked = false;
        }

        using (var invalid = WebConsoleHarness.Create(Writable(), adminConfig: store))
        {
            store.RejectContent = true;
            var login = await invalid.LoginAsync();
            var response = await invalid.Handler.HandleAsync(invalid.Request(
                "POST", "/api/config/profiles", body: Body("{\"schemaVersion\":2}", storedEtag), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("config.invalid", Code(response));
            Assert.Contains("config.schema_invalid", BodyText(response), StringComparison.Ordinal);
            store.RejectContent = false;
        }

        using (var backup = WebConsoleHarness.Create(Writable(), adminConfig: store))
        {
            store.ForceBackupFailure = true;
            var login = await backup.LoginAsync();
            var response = await backup.Handler.HandleAsync(backup.Request(
                "POST", "/api/config/profiles", body: Body("{\"schemaVersion\":2}", storedEtag), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(502, response.StatusCode);
            Assert.Equal("host.admin_config.failed", Code(response));
            store.ForceBackupFailure = false;
        }

        using (var throwing = WebConsoleHarness.Create(Writable(), adminConfig: store))
        {
            store.FailWithError = true;
            var login = await throwing.LoginAsync();
            var response = await throwing.Handler.HandleAsync(throwing.Request(
                "POST", "/api/config/profiles", body: Body("{\"schemaVersion\":2}", storedEtag), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(502, response.StatusCode);
            Assert.DoesNotContain("sk-abcdefghijkl", BodyText(response), StringComparison.Ordinal);
            store.FailWithError = false;
        }
    }

    [Fact]
    public async Task Save_keeps_phase1_gating_order()
    {
        // Write disabled wins over a missing store.
        using (var disabled = WebConsoleHarness.Create())
        {
            var login = await disabled.LoginAsync();
            var response = await disabled.Handler.HandleAsync(disabled.Request(
                "POST", "/api/config/profiles", body: Body("{}"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(403, response.StatusCode);
            Assert.Equal("config.write_disabled", Code(response));
        }

        // Invalid content is rejected before the host store is consulted.
        using (var invalid = WebConsoleHarness.Create(Writable()))
        {
            var login = await invalid.LoginAsync();
            var response = await invalid.Handler.HandleAsync(invalid.Request(
                "POST", "/api/config/profiles", body: Body("{oops}"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("config.invalid", Code(response));
        }

        // Plaintext secret literal is rejected with the plugin policy code.
        using (var secret = WebConsoleHarness.Create(Writable()))
        {
            var login = await secret.LoginAsync();
            var response = await secret.Handler.HandleAsync(secret.Request(
                "POST", "/api/config/profiles", body: Body("{\"token\":\"plain-value\"}"), cookie: login.Cookie, csrf: login.Csrf));
            Assert.Equal(400, response.StatusCode);
            Assert.Contains("config.secret_literal", BodyText(response), StringComparison.Ordinal);
        }

        // Oversized bodies are refused before parsing.
        using (var large = WebConsoleHarness.Create(Writable()))
        {
            var login = await large.LoginAsync();
            var response = await large.Handler.HandleAsync(large.Request(
                "POST", "/api/config/profiles",
                body: Body(new string('x', WebConsoleDefaults.MaxAdminBodyBytes)),
                cookie: login.Cookie,
                csrf: login.Csrf));
            Assert.Equal(413, response.StatusCode);
        }
    }

    [Fact]
    public async Task Save_without_host_store_still_reports_the_gap_after_validation()
    {
        using var harness = WebConsoleHarness.Create(Writable());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: Body("{\"schemaVersion\":1}"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(501, response.StatusCode);
        Assert.Equal("host.admin_config.unavailable", Code(response));
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "config.write" && entry.Result == "unavailable");
    }

    [Fact]
    public async Task Rollback_restores_the_previous_content_and_lists_undo_entries()
    {
        const string first = "{\"schemaVersion\":1}";
        const string second = "{\"schemaVersion\":1,\"defaultProfile\":\"tier1\"}";
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, first);
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var saved = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles", body: Body(second, AdminETag.Compute(first)),
            cookie: login.Cookie, csrf: login.Csrf));
        Assert.Equal(200, saved.StatusCode);
        var savedEtag = Json(saved).GetProperty("eTag").GetString();
        Assert.Equal(second, store.ContentOf(ProfilesId));

        var undo = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles/undo", cookie: login.Cookie));
        Assert.Equal(200, undo.StatusCode);
        Assert.Single(Json(undo).GetProperty("entries").EnumerateArray());

        var rollback = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/rollback",
            body: JsonSerializer.Serialize(new { etag = savedEtag }),
            cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(200, rollback.StatusCode);
        Assert.True(Json(rollback).GetProperty("saved").GetProperty("saved").GetBoolean());
        Assert.Equal(first, store.ContentOf(ProfilesId));
        Assert.Contains(harness.Audit.Read(50), entry => entry.Action == "config.rollback" && entry.Result == "ok");
    }

    [Fact]
    public async Task Rollback_without_snapshot_reports_undo_empty()
    {
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, "{\"schemaVersion\":1}");
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/rollback", body: "{}", cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("config.undo_empty", Code(response));
    }

    [Fact]
    public async Task Validate_uses_the_host_schema_and_diff_when_available()
    {
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, "{\"schemaVersion\":1}");
        store.RejectContent = true;
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate",
            body: Body("{\"schemaVersion\":2}"),
            cookie: login.Cookie,
            csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        var validation = Json(response).GetProperty("validation");
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Equal("host", validation.GetProperty("source").GetString());
        Assert.Equal(AdminETag.Compute("{\"schemaVersion\":2}"), validation.GetProperty("eTag").GetString());
        Assert.Contains(
            validation.GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "config.schema_invalid");
        Assert.Equal(1, validation.GetProperty("diff").GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task Validate_falls_back_to_plugin_policy_without_store()
    {
        using var harness = WebConsoleHarness.Create();
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/profiles/validate", body: Body("{}"), cookie: login.Cookie, csrf: login.Csrf));

        Assert.Equal(200, response.StatusCode);
        var validation = Json(response).GetProperty("validation");
        Assert.True(validation.GetProperty("valid").GetBoolean());
        Assert.Equal("plugin", validation.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Config_preview_and_document_list_expose_the_admin_surface()
    {
        var store = new FakeAdminConfigStore().WithDocument(ProfilesId, "{}");
        using var harness = WebConsoleHarness.Create(Writable(), adminConfig: store);
        var login = await harness.LoginAsync();

        var preview = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config", cookie: login.Cookie));
        Assert.Equal(200, preview.StatusCode);
        var json = Json(preview);
        Assert.True(json.GetProperty("writeEnabled").GetBoolean());
        Assert.False(json.TryGetProperty("writeGapCode", out _));
        Assert.True(json.GetProperty("admin").GetProperty("configStoreAvailable").GetBoolean());
        Assert.Single(json.GetProperty("documents").EnumerateArray());

        var documents = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/admin/documents", cookie: login.Cookie));
        Assert.Equal(200, documents.StatusCode);
        Assert.Single(Json(documents).GetProperty("documents").EnumerateArray());

        var status = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/admin", cookie: login.Cookie));
        Assert.Equal(200, status.StatusCode);
        Assert.True(Json(status).GetProperty("configStoreAvailable").GetBoolean());
        Assert.Equal(10, Json(status).GetProperty("commandTimeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task Unknown_sub_resource_is_rejected_as_invalid_id()
    {
        using var harness = WebConsoleHarness.Create(Writable());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/config/profiles/whatever", cookie: login.Cookie));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("config.id_invalid", Code(response));
    }

    [Fact]
    public async Task Documents_endpoint_reports_gap_without_store()
    {
        using var harness = WebConsoleHarness.Create(Writable());
        var login = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "GET", "/api/admin/documents", cookie: login.Cookie));

        Assert.Equal(501, response.StatusCode);
        Assert.Equal("host.admin_config.unavailable", Code(response));
    }
}
