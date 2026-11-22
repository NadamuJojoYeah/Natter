using System.Text;
using System.Text.Json;
using Natter.Application.Admin;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23Acceptance;

/// <summary>
/// Phase 2 acceptance (task-5): real save / backup / rollback / stale ETag / symlink / id policy over
/// the HTTP contract. Every "nothing was written" claim is checked against the real file on disk.
/// </summary>
public sealed class ConfigPhase2AcceptanceTests
{
    private const string ProfilesId = "profiles";

    [Fact]
    public async Task Save_creates_the_document_on_disk_with_private_permissions()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var content = Profiles("qa-create");

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            $"/api/config/{ProfilesId}",
            Envelope(content, etag: null),
            cookie,
            csrf));

        Assert.Equal(200, response.StatusCode);
        var body = AcceptanceHarness.Json(response);
        Assert.True(body.GetProperty("saved").GetBoolean());
        Assert.Equal(AdminCodes.ConfigSaved, body.GetProperty("code").GetString());

        var path = harness.Store!.PathFor(ProfilesId);
        Assert.True(File.Exists(path), "config document must be written to disk");
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(ProfilesId, body.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("eTag").GetString()));
        Assert.True(body.GetProperty("restartRequired").GetBoolean());

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        // No 501 anywhere on the Phase 2 path: the host contract is present.
        Assert.False(AcceptanceHarness.BodyText(response).Contains("host.admin_config.unavailable", StringComparison.Ordinal), "unexpected substring in payload");
    }

    [Fact]
    public async Task Stale_etag_returns_409_and_leaves_the_file_untouched()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var original = Profiles("qa-original");
        var first = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(original, null), cookie, csrf));
        Assert.Equal(200, first.StatusCode);

        var staleEtag = FileAdminConfigStore.ETagOf("something else entirely");
        var second = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-overwrite"), staleEtag), cookie, csrf));

        Assert.Equal(409, second.StatusCode);
        var json = AcceptanceHarness.Json(second);
        Assert.Equal("config.conflict", json.GetProperty("error").GetProperty("code").GetString());
        var current = FileAdminConfigStore.ETagOf(original);
        Assert.Equal(current, json.GetProperty("detail").GetProperty("conflictETag").GetString());
        Assert.Equal(original, await File.ReadAllTextAsync(harness.Store!.PathFor(ProfilesId)));
        Assert.False(Directory.Exists(harness.Store.BackupRoot), "a rejected write must not create a backup");
    }

    [Fact]
    public async Task Null_etag_is_only_accepted_for_a_missing_document()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-a"), null), cookie, csrf));

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-b"), null), cookie, csrf));

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("config.conflict", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.True((await File.ReadAllTextAsync(harness.Store!.PathFor(ProfilesId))).Contains("qa-a", StringComparison.Ordinal), "expected substring missing from payload");
    }

    [Fact]
    public async Task Save_rejects_invalid_content_without_writing()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope("{ not json", null), cookie, csrf));

        Assert.Equal(400, response.StatusCode);
        var json = AcceptanceHarness.Json(response);
        Assert.Equal("config.invalid", json.GetProperty("error").GetProperty("code").GetString());
        Assert.NotEmpty(json.GetProperty("validation").GetProperty("issues").EnumerateArray());
        Assert.False(File.Exists(harness.Store!.PathFor(ProfilesId)));
    }

    [Fact]
    public async Task Save_writes_a_backup_of_the_previous_content()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var v1 = Profiles("qa-v1");
        var created = AcceptanceHarness.Json(await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(v1, null), cookie, csrf)));

        var v2 = Profiles("qa-v2");
        var saved = AcceptanceHarness.Json(await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(v2, created.GetProperty("eTag").GetString()), cookie, csrf)));

        Assert.True(saved.GetProperty("saved").GetBoolean());
        var backupPath = saved.GetProperty("backupPath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(backupPath));
        var backup = Path.Combine(harness.Store!.Root, backupPath!);
        Assert.True(File.Exists(backup));
        Assert.Equal(v1, await File.ReadAllTextAsync(backup));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
        Assert.Equal(v2, await File.ReadAllTextAsync(harness.Store.PathFor(ProfilesId)));
    }

    [Fact]
    public async Task Rollback_restores_the_previous_document_content()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var v1 = Profiles("qa-before");
        var created = AcceptanceHarness.Json(await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(v1, null), cookie, csrf)));
        var v2 = Profiles("qa-after");
        var saved = AcceptanceHarness.Json(await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(v2, created.GetProperty("eTag").GetString()), cookie, csrf)));

        var undoId = saved.GetProperty("undoId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(undoId));

        var rolled = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            $"/api/config/{ProfilesId}/rollback",
            JsonSerializer.Serialize(new { etag = saved.GetProperty("eTag").GetString(), undoId }),
            cookie,
            csrf));

        Assert.Equal(200, rolled.StatusCode);
        var body = AcceptanceHarness.Json(rolled);
        Assert.True(body.GetProperty("saved").GetProperty("saved").GetBoolean());
        Assert.Equal(v1, await File.ReadAllTextAsync(harness.Store!.PathFor(ProfilesId)));
    }

    [Fact]
    public async Task Rollback_without_a_snapshot_returns_409_and_does_not_write()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST",
            $"/api/config/{ProfilesId}/rollback",
            JsonSerializer.Serialize(new { etag = (string?)null, undoId = (string?)null }),
            cookie,
            csrf));

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("config.undo_empty", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(harness.Store!.PathFor(ProfilesId)));
    }

    [Fact]
    public async Task Symlinked_document_is_neither_read_nor_written()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var target = Path.Combine(harness.Directory, "outside.json");
        await File.WriteAllTextAsync(target, Profiles("qa-outside"));
        var path = harness.Store!.PathFor(ProfilesId);
        Directory.CreateDirectory(harness.Store.Root);
        File.CreateSymbolicLink(path, target);

        var read = await harness.Handler.HandleAsync(harness.Request("GET", $"/api/config/{ProfilesId}", null, cookie, csrf));
        Assert.Equal(409, read.StatusCode);
        Assert.Equal("config.symlink", AcceptanceHarness.Json(read).GetProperty("error").GetProperty("code").GetString());

        var write = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-link-write"), null), cookie, csrf));
        Assert.Equal(409, write.StatusCode);
        Assert.Equal("config.symlink", AcceptanceHarness.Json(write).GetProperty("error").GetProperty("code").GetString());
        Assert.True((await File.ReadAllTextAsync(target)).Contains("qa-outside", StringComparison.Ordinal), "expected substring missing from payload");
        Assert.False((await File.ReadAllTextAsync(target)).Contains("qa-link-write", StringComparison.Ordinal), "unexpected substring in payload");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("plugin:../x")]
    [InlineData("plugin:..%2fx")]
    [InlineData("secrets")]
    [InlineData("plugin:")]
    [InlineData("profiles/../../x")]
    public async Task Path_like_and_reserved_ids_are_rejected_with_400(string id)
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{id}", Envelope("{}", null), cookie, csrf));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("config.id_invalid", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(Directory.Exists(harness.Store!.Root) ? Directory.GetFiles(harness.Store.Root, "*.json") : []);
    }

    [Fact]
    public async Task Unknown_plugin_document_returns_404()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", "/api/config/plugin:nope.nope", Envelope("{}", null), cookie, csrf));

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("config.plugin_unknown", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Concurrent_saves_with_the_same_etag_produce_exactly_one_writer()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();
        var created = AcceptanceHarness.Json(await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-base"), null), cookie, csrf)));
        var etag = created.GetProperty("eTag").GetString();

        var first = harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-first"), etag), cookie, csrf)).AsTask();
        var second = harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-second"), etag), cookie, csrf)).AsTask();
        var responses = await Task.WhenAll(first, second);

        var statuses = responses.Select(response => response.StatusCode).OrderBy(status => status).ToArray();
        Assert.Equal([200, 409], statuses);
        var winner = responses.Single(response => response.StatusCode == 200);
        Assert.True(AcceptanceHarness.Json(winner).GetProperty("saved").GetBoolean());

        var disk = await File.ReadAllTextAsync(harness.Store!.PathFor(ProfilesId));
        var matches = new[] { "qa-first", "qa-second" }.Count(marker => disk.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(1, matches);
        JsonSerializer.Deserialize<JsonElement>(disk); // the stored document stays valid JSON
    }

    [Fact]
    public async Task Config_write_is_refused_with_403_when_the_console_is_read_only()
    {
        using var harness = AcceptanceHarness.Create(allowConfigWrite: false);
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}", Envelope(Profiles("qa-denied"), null), cookie, csrf));

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("config.write_disabled", AcceptanceHarness.Json(response).GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(harness.Store!.PathFor(ProfilesId)));
    }

    [Fact]
    public async Task Validate_reports_host_style_shape_without_writing()
    {
        using var harness = AcceptanceHarness.Create();
        var (cookie, csrf, _) = await harness.LoginAsync();

        var response = await harness.Handler.HandleAsync(harness.Request(
            "POST", $"/api/config/{ProfilesId}/validate", Envelope(Profiles("qa-validate"), null), cookie, csrf));

        Assert.Equal(200, response.StatusCode);
        var validation = AcceptanceHarness.Json(response).GetProperty("validation");
        Assert.True(validation.GetProperty("valid").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(validation.GetProperty("eTag").GetString()));
        Assert.False(File.Exists(harness.Store!.PathFor(ProfilesId)));
    }

    // ------------------------------------------------- F1 harness/product parity

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"profiles":[]}""")]
    [InlineData("""{"a":"中文","b":[1,2,3]}""")]
    public void Harness_etags_match_the_product_implementation(string content)
    {
        // S2-F1: the acceptance harness used to re-implement the ETag algorithm. It now delegates
        // to AdminETag; this parity case fails if either side drifts.
        var expected = AdminETag.Compute(content);

        Assert.Equal(expected, FileAdminConfigStore.ETagOf(content));
        Assert.True(AdminETag.Matches(content, FileAdminConfigStore.ETagOf(content)));
        Assert.StartsWith(AdminETag.Prefix, expected, StringComparison.Ordinal);
    }

    private static string Profiles(string id)
        => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            defaultProfile = id,
            profiles = new[]
            {
                new
                {
                    id,
                    supportTier = 2,
                    ai = new { provider = "host#static-responder" },
                    prompt = new { provider = "host#static", version = "default" },
                },
            },
        });

    private static string Envelope(string content, string? etag)
        => JsonSerializer.Serialize(new { content, etag });
}
