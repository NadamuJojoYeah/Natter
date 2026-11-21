using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Natter.Abstractions.Plugins;
using Natter.Application.Admin;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Infrastructure.Admin;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Host-side admin contract tests (WebConsole gaps G4/G5/G6/G8 + H1–H10 constraints):
/// config store whitelist/ETag/backup/lock/atomic replace, restricted command runner,
/// plugin-provider permission bridging, host log tail and the admin permission vocabulary.
/// </summary>
public sealed class AdminHostContractTests
{
    // ---------------------------------------------------------------- config store

    [Fact]
    public async Task Read_missing_document_is_available_and_reports_exists_false()
    {
        using var workspace = new AdminWorkspace();
        var store = CreateStore(workspace);

        var result = await store.ReadAsync("profiles");

        Assert.True(result.Available);
        Assert.NotNull(result.Document);
        Assert.False(result.Document!.Exists);
        Assert.Equal(string.Empty, result.Document.Content);
        Assert.Equal(AdminETag.Compute(string.Empty), result.Document.ETag);
        Assert.Equal(AdminConfigDocumentSource.Missing, result.Document.Source);
    }

    [Fact]
    public async Task Read_without_config_directory_reports_store_unavailable()
    {
        using var workspace = new AdminWorkspace();
        var store = CreateStore(workspace, options => options.ConfigDirectory = null);

        var result = await store.ReadAsync("profiles");

        Assert.False(result.Available);
        Assert.Null(result.Document);
        Assert.Equal(AdminCodes.ConfigDirectoryUnavailable, result.Code);
    }

    [Fact]
    public async Task Read_existing_profiles_reports_etag_and_restart_hint()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);

        var result = await store.ReadAsync("profiles");

        Assert.True(result.Available);
        var document = Assert.IsType<AdminConfigDocument>(result.Document);
        Assert.True(document.Exists);
        Assert.Equal(AdminConfigDocumentSource.ConfigDirectory, document.Source);
        Assert.Equal(AdminETag.Compute(AdminWorkspace.ValidProfiles), document.ETag);
        Assert.Equal(AdminConfigReloadHint.RestartHost, document.ReloadHint);
    }

    [Theory]
    [InlineData("../profiles")]
    [InlineData("/etc/passwd")]
    [InlineData("plugin:../evil")]
    [InlineData("plugin:evil/../../etc/passwd")]
    [InlineData("plugin:evil\\..\\secrets")]
    [InlineData("plugin:secrets")]
    [InlineData("plugin:profiles")]
    [InlineData("secrets")]
    [InlineData("profiles.json")]
    [InlineData("plugin:.")]
    [InlineData("plugin:")]
    public async Task Document_ids_outside_the_whitelist_are_rejected_without_io(string id)
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("secrets.json", "{\"token\":\"do-not-touch\"}");
        var store = CreateStore(workspace);

        var read = await store.ReadAsync(id);
        var save = await store.SaveAsync(id, "{}", etag: null);

        Assert.False(read.Available);
        Assert.Contains(
            read.Code,
            new[] { AdminCodes.ConfigDocumentNotAllowed, AdminCodes.ConfigIdInvalid });
        Assert.False(save.Saved);
        Assert.Equal("{\"token\":\"do-not-touch\"}", File.ReadAllText(workspace.ConfigPath("secrets.json")));
        Assert.False(File.Exists(workspace.ConfigPath("profiles.json")));
    }

    [Fact]
    public async Task Validate_profiles_rejects_structural_rule_violations()
    {
        using var workspace = new AdminWorkspace();
        var store = CreateStore(workspace);

        const string invalidProfiles = """
            {
              "schemaVersion": 1,
              "defaultProfile": "missing",
              "profiles": [
                { "id": "one", "supportTier": 1, "ai": { "provider": "p#r" } },
                { "id": "two", "supportTier": 3, "ai": { "provider": "bad" } }
              ]
            }
            """;

        var result = await store.ValidateAsync("profiles", invalidProfiles);

        Assert.False(result.Valid);
        Assert.Equal(AdminCodes.ConfigInvalid, result.Code);
        Assert.Contains(result.Issues, issue => issue.Code == AdminCodes.ConfigSchemaInvalid);
        Assert.Contains(result.Issues, issue => issue.Pointer == "/profiles/1/supportTier");
        Assert.Contains(result.Issues, issue => issue.Pointer == "/profiles/1/ai/provider");
        Assert.Contains(result.Issues, issue => issue.Pointer == "/defaultProfile");
        Assert.Equal(AdminETag.Compute(invalidProfiles), result.ETag);
    }

    [Fact]
    public async Task Validate_accepts_secret_references_but_rejects_plaintext_secrets()
    {
        using var workspace = new AdminWorkspace();
        var plugin = workspace.CreatePluginPackage("natter.test.admin");
        var store = CreateStore(workspace, catalog: new FakePluginCatalog(plugin));

        var reference = await store.ValidateAsync(
            "plugin:natter.test.admin",
            """{ "token": "${secret:ADMIN_TOKEN}" }""");
        var literal = await store.ValidateAsync(
            "plugin:natter.test.admin",
            """{ "token": "sk-abcdefgh12345678" }""");

        Assert.True(reference.Valid);
        Assert.False(reference.ContainsSecretLiteral);
        Assert.False(literal.Valid);
        Assert.True(literal.ContainsSecretLiteral);
        Assert.Contains(literal.Issues, issue => issue.Code == AdminCodes.ConfigSecretLiteral);
    }

    [Fact]
    public async Task Validate_plugin_config_uses_declared_schema()
    {
        using var workspace = new AdminWorkspace();
        var plugin = workspace.CreatePluginPackage(
            "natter.test.admin",
            schema: """
                {
                  "type": "object",
                  "properties": { "port": { "type": "integer" } },
                  "required": ["port"],
                  "additionalProperties": false
                }
                """);
        var store = CreateStore(workspace, catalog: new FakePluginCatalog(plugin));

        var invalid = await store.ValidateAsync("plugin:natter.test.admin", """{ "port": "no" }""");
        var valid = await store.ValidateAsync("plugin:natter.test.admin", """{ "port": 5090 }""");

        Assert.False(invalid.Valid);
        Assert.Contains(invalid.Issues, issue => issue.Code == AdminCodes.ConfigSchemaInvalid);
        Assert.True(valid.Valid);
    }

    [Fact]
    public async Task Save_writes_backup_and_replaces_atomically()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");
        var updated = AdminWorkspace.ProfilesWithExtraProfile;

        var result = await store.SaveAsync("profiles", updated, read.Document!.ETag);

        Assert.True(result.Saved);
        Assert.Equal(AdminCodes.ConfigSaved, result.Code);
        Assert.Equal(AdminETag.Compute(updated), result.ETag);
        Assert.True(result.RestartRequired);
        Assert.Equal(AdminConfigReloadHint.RestartHost, result.ReloadHint);
        Assert.Equal(updated, File.ReadAllText(workspace.ConfigPath("profiles.json")));
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(".backup/", result.BackupPath);

        var backupFile = Path.Combine(
            workspace.ConfigDir,
            result.BackupPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(backupFile));
        Assert.Equal(AdminWorkspace.ValidProfiles, File.ReadAllText(backupFile));

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(backupFile);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }

        Assert.DoesNotContain(
            Directory.GetFiles(workspace.ConfigDir),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_with_stale_etag_returns_conflict_etag_instead_of_throwing()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");

        workspace.WriteConfig("profiles.json", AdminWorkspace.ProfilesWithExtraProfile);
        var result = await store.SaveAsync("profiles", AdminWorkspace.ValidProfiles, read.Document!.ETag);

        Assert.False(result.Saved);
        Assert.True(result.Conflict);
        Assert.Equal(AdminCodes.ConfigEtagMismatch, result.Code);
        Assert.Equal(AdminETag.Compute(AdminWorkspace.ProfilesWithExtraProfile), result.ConflictETag);
        Assert.Equal(
            AdminWorkspace.ProfilesWithExtraProfile,
            File.ReadAllText(workspace.ConfigPath("profiles.json")));
    }

    [Fact]
    public async Task Save_without_etag_on_existing_document_conflicts()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);

        var result = await store.SaveAsync("profiles", AdminWorkspace.ProfilesWithExtraProfile, etag: null);

        Assert.False(result.Saved);
        Assert.Equal(AdminCodes.ConfigEtagMismatch, result.Code);
        Assert.Equal(AdminETag.Compute(AdminWorkspace.ValidProfiles), result.ConflictETag);
    }

    [Fact]
    public async Task Save_with_null_etag_creates_plugin_override_document()
    {
        using var workspace = new AdminWorkspace();
        var plugin = workspace.CreatePluginPackage("natter.test.admin");
        var store = CreateStore(workspace, catalog: new FakePluginCatalog(plugin));
        const string content = """{ "enabled": true }""";

        var result = await store.SaveAsync("plugin:natter.test.admin", content, etag: null);

        Assert.True(result.Saved);
        Assert.Equal(AdminConfigReloadHint.RestartPlugin, result.ReloadHint);
        Assert.Null(result.BackupPath);
        Assert.Equal(content, File.ReadAllText(workspace.ConfigPath("natter.test.admin.json")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(workspace.ConfigPath("natter.test.admin.json")));
        }
    }

    [Fact]
    public async Task Save_creates_the_config_directory_for_new_documents()
    {
        using var workspace = new AdminWorkspace();
        var configDirectory = Path.Combine(workspace.Root, "config-missing");
        var store = CreateStore(workspace, options => options.ConfigDirectory = configDirectory);

        var result = await store.SaveAsync("profiles", AdminWorkspace.ValidProfiles, etag: null);

        Assert.True(result.Saved);
        Assert.True(File.Exists(Path.Combine(configDirectory, "profiles.json")));
    }

    [Fact]
    public async Task Save_tightens_an_existing_config_directory_to_0700()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var workspace = new AdminWorkspace();
        File.SetUnixFileMode(
            workspace.ConfigDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);

        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");
        var result = await store.SaveAsync("profiles", AdminWorkspace.ProfilesWithExtraProfile, read.Document!.ETag);

        Assert.True(result.Saved);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(workspace.ConfigDir));
        Assert.Equal(
            AdminWorkspace.ProfilesWithExtraProfile,
            File.ReadAllText(workspace.ConfigPath("profiles.json")));
    }

    [Fact]
    public async Task Save_rejects_documents_for_unregistered_plugins()
    {
        using var workspace = new AdminWorkspace();
        var store = CreateStore(workspace);

        var result = await store.SaveAsync("plugin:natter.test.missing", """{ "enabled": true }""", etag: null);

        Assert.False(result.Saved);
        Assert.Equal(AdminCodes.ConfigPluginUnknown, result.Code);
        Assert.False(File.Exists(workspace.ConfigPath("natter.test.missing.json")));
    }

    [Fact]
    public async Task Save_rejects_invalid_content_and_keeps_the_previous_file()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");

        var result = await store.SaveAsync("profiles", "{ not json", read.Document!.ETag);

        Assert.False(result.Saved);
        Assert.Equal(AdminCodes.ConfigInvalid, result.Code);
        Assert.Contains(result.Issues, issue => issue.Code == AdminCodes.ConfigInvalidJson);
        Assert.Equal(AdminWorkspace.ValidProfiles, File.ReadAllText(workspace.ConfigPath("profiles.json")));
    }

    [Fact]
    public async Task Save_rejects_symlinked_documents()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var workspace = new AdminWorkspace();
        var outside = Path.Combine(workspace.Root, "outside.json");
        await File.WriteAllTextAsync(outside, AdminWorkspace.ValidProfiles);
        File.CreateSymbolicLink(workspace.ConfigPath("profiles.json"), outside);

        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");
        var result = await store.SaveAsync(
            "profiles",
            AdminWorkspace.ProfilesWithExtraProfile,
            etag: AdminETag.Compute(AdminWorkspace.ValidProfiles));

        Assert.False(read.Available);
        Assert.Equal(AdminCodes.ConfigSymlinkNotAllowed, read.Code);
        Assert.False(result.Saved);
        Assert.Equal(AdminCodes.ConfigSymlinkNotAllowed, result.Code);
        Assert.Equal(AdminWorkspace.ValidProfiles, await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task Concurrent_saves_with_the_same_etag_write_exactly_once()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var store = CreateStore(workspace);
        var read = await store.ReadAsync("profiles");
        var etag = read.Document!.ETag;

        var first = store.SaveAsync("profiles", AdminWorkspace.ProfilesWithExtraProfile, etag).AsTask();
        var second = store.SaveAsync("profiles", AdminWorkspace.ValidProfiles, etag).AsTask();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Saved);
        Assert.Single(results, result => result.Conflict);
        var saved = results.Single(result => result.Saved);
        Assert.Equal(saved.ETag, AdminETag.Compute(File.ReadAllText(workspace.ConfigPath("profiles.json"))));
    }

    [Fact]
    public async Task List_covers_profiles_and_registered_plugin_documents()
    {
        using var workspace = new AdminWorkspace();
        workspace.WriteConfig("profiles.json", AdminWorkspace.ValidProfiles);
        var plugin = workspace.CreatePluginPackage("natter.test.admin");
        var store = CreateStore(workspace, catalog: new FakePluginCatalog(plugin));

        var documents = await store.ListAsync();

        Assert.Contains(documents, document => document.Id == "profiles" && document.Exists);
        Assert.Contains(documents, document => document.Id == "plugin:natter.test.admin");
    }

    [Fact]
    public void Etag_matches_the_webconsole_policy_format()
    {
        const string content = """{ "schemaVersion": 1 }""";
        var expected = "sha256:"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        Assert.Equal(expected, AdminETag.Compute(content));
        Assert.True(AdminETag.Matches(content, expected));
        Assert.False(AdminETag.Matches(content, "sha256:0"));
    }

    [Fact]
    public void Redactor_masks_key_value_secrets_and_keeps_secret_references()
    {
        const string text = "token=abc123 API_KEY=XYZ-123 password: hunter2 --api-key=plain";
        var redacted = AdminRedactor.Default.Redact(text);

        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("XYZ-123", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("plain", redacted);
        Assert.Contains("token=[redacted]", redacted);
        Assert.Equal(
            "token=${secret:WEBCONSOLE_TOKEN}",
            AdminRedactor.Default.Redact("token=${secret:WEBCONSOLE_TOKEN}"));
    }

    [Fact]
    public void Diff_uses_the_webconsole_line_prefix_convention()
    {
        var diff = AdminConfigDiffBuilder.Build("{\n\"a\": 1\n}", "{\n\"a\": 2\n}");

        Assert.NotNull(diff);
        Assert.Equal(1, diff!.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(1, diff.Changed);
        Assert.Contains(diff.Lines, line => line == "-" + "\"a\": 1");
        Assert.Contains(diff.Lines, line => line == "+" + "\"a\": 2");
        Assert.Contains(diff.Lines, line => line.StartsWith(' '));
        Assert.False(diff.Truncated);
    }

    // ---------------------------------------------------------------- command runner

    [Fact]
    public async Task Allowlisted_command_runs_in_process_and_captures_output()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();

        var result = await fixture.Runner.RunAsync("admin echo", []);

        Assert.True(result.Executed);
        Assert.True(result.Succeeded);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("hello from admin", result.Stdout);
        Assert.Equal(1, fixture.Recorder.EchoInvocations);
    }

    [Fact]
    public async Task Command_outside_the_allowlist_is_denied_without_running()
    {
        await using var fixture = await AdminHostFixture.CreateAsync(options =>
            options.CommandAllowlist.Remove("admin echo"));

        var result = await fixture.Runner.RunAsync("admin echo", []);

        Assert.False(result.Executed);
        Assert.Equal(AdminCodes.CommandNotAllowed, result.Code);
        Assert.Equal(0, fixture.Recorder.EchoInvocations);
    }

    [Fact]
    public async Task Unknown_command_is_reported_as_not_registered()
    {
        await using var fixture = await AdminHostFixture.CreateAsync(options =>
            options.CommandAllowlist.Add("admin missing"));

        var result = await fixture.Runner.RunAsync("admin missing", []);

        Assert.False(result.Executed);
        Assert.Equal(AdminCodes.CommandNotRegistered, result.Code);
    }

    [Fact]
    public async Task Command_timeout_is_enforced()
    {
        await using var fixture = await AdminHostFixture.CreateAsync(options => options.CommandTimeoutSeconds = 1);

        var result = await fixture.Runner.RunAsync("admin slow", []);

        Assert.True(result.Executed);
        Assert.True(result.TimedOut);
        Assert.Equal(AdminCodes.CommandTimeout, result.Code);
        Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
        Assert.True(result.DurationMs < 10_000);
    }

    [Fact]
    public async Task Command_output_is_redacted_and_bounded()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();

        var result = await fixture.Runner.RunAsync("admin noisy", []);

        Assert.True(result.Executed);
        Assert.True(result.Truncated);
        Assert.True(result.OutputBytes <= AdminCommandPolicy.MaxOutputBytes);
        Assert.DoesNotContain("sk-abcdefgh12345678", result.Stdout);
        Assert.DoesNotContain("Bearer abcdefghijklmnop", result.Stdout);
        Assert.DoesNotContain("plain-value-123", result.Stdout);
        Assert.DoesNotContain("hunter2-secret", result.Stdout);
        Assert.Contains("[redacted]", result.Stdout);
    }

    [Fact]
    public async Task Arguments_are_passed_as_argv_and_never_shell_evaluated()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();
        string[] args = ["; rm -rf /tmp/natter-admin-test", "$(id)", "|", "&&"];

        var result = await fixture.Runner.RunAsync("admin args", args);

        Assert.True(result.Executed);
        Assert.Equal(args, fixture.Recorder.LastArgs);
        Assert.False(Directory.Exists("/tmp/natter-admin-test"));
    }

    [Fact]
    public async Task Oversized_or_control_character_arguments_are_rejected()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();
        var tooMany = Enumerable.Range(0, AdminCommandPolicy.MaxArgs + 1).Select(index => "a" + index).ToArray();

        var many = await fixture.Runner.RunAsync("admin args", tooMany);
        var control = await fixture.Runner.RunAsync("admin args", ["bad\u0000arg"]);

        Assert.False(many.Executed);
        Assert.Equal(AdminCodes.CommandInvalid, many.Code);
        Assert.False(control.Executed);
        Assert.Equal(AdminCodes.CommandInvalid, control.Code);
    }

    [Fact]
    public async Task Commands_are_serialized_and_see_their_own_console()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();

        var first = fixture.Runner.RunAsync("admin serial", ["first"]).AsTask();
        var second = fixture.Runner.RunAsync("admin serial", ["second"]).AsTask();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Executed));
        Assert.Equal(1, fixture.Recorder.MaxObservedConcurrency);
        Assert.True(fixture.Recorder.ConsoleAccessorMatched);
    }

    [Fact]
    public async Task Command_runs_are_audited_without_secret_values()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();

        await fixture.Runner.RunAsync("admin echo", []);

        var auditFile = Path.Combine(fixture.Workspace.StateDir, "admin-audit.jsonl");
        Assert.True(File.Exists(auditFile));
        var lines = await File.ReadAllLinesAsync(auditFile);
        Assert.Contains(lines, line => line.Contains(AdminAuditActions.RunCommand, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("sk-abcdefgh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Built_in_health_command_runs_through_the_admin_runner()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();

        var result = await fixture.Runner.RunAsync("health", ["--json", "--offline"]);

        Assert.True(result.Executed);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("\"status\"", result.Stdout);
        Assert.Contains("\"profiles\"", result.Stdout);
    }

    [Fact]
    public void Default_allowlist_contains_only_read_only_diagnostics()
    {
        Assert.Contains("health", AdminCommandPolicy.DefaultAllowlist);
        Assert.Contains("plugins list", AdminCommandPolicy.DefaultAllowlist);
        Assert.DoesNotContain("serve", AdminCommandPolicy.DefaultAllowlist);
        Assert.DoesNotContain("exec", AdminCommandPolicy.DefaultAllowlist);
        Assert.DoesNotContain("events clear", AdminCommandPolicy.DefaultAllowlist);
    }

    // ---------------------------------------------------------------- log tail

    [Fact]
    public async Task Host_log_tail_returns_redacted_recent_entries()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();
        var logger = fixture.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Natter.Test.Admin");

        logger.LogInformation("admin probe one");
        logger.LogWarning("secret probe token=sk-abcdefgh12345678");
        logger.LogInformation("admin probe two");

        var entries = await fixture.LogReader.ReadAsync(10);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, entry => entry.Message.Contains("admin probe two", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("[redacted]", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Message.Contains("sk-abcdefgh12345678", StringComparison.Ordinal));
        Assert.Equal(
            entries.OrderBy(entry => entry.Sequence).Select(entry => entry.Sequence),
            entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task Host_log_tail_limit_is_clamped()
    {
        await using var fixture = await AdminHostFixture.CreateAsync();
        var logger = fixture.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Natter.Test.Admin");
        for (var index = 0; index < 20; index++)
            logger.LogInformation("log {Index}", index);

        var entries = await fixture.LogReader.ReadAsync(100_000);

        Assert.True(entries.Count <= 500);
        Assert.True(entries.Count >= 20);
    }

    // ---------------------------------------------------------------- plugin provider bridge

    [Fact]
    public async Task Plugin_with_admin_read_gets_diagnostics_and_log_reader_only()
    {
        var plugin = new AdminProbePlugin();
        var manifest = AdminManifest(
            "natter.test.admin-read",
            permissions: [PluginPermissions.AdminRead],
            capabilities: ["admin:diagnostics"]);

        await using var probe = await BuildProbeHostAsync(plugin, manifest);
        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);

        Assert.NotNull(provider.GetService<IPluginRuntimeDiagnostics>());
        Assert.NotNull(provider.GetService<IAdminLogReader>());
        Assert.Null(provider.GetService<IAdminConfigStore>());
        Assert.Null(provider.GetService<IAdminCommandRunner>());
        await probe.Host.StopAsync();
    }

    [Fact]
    public async Task Plugin_with_all_admin_permissions_gets_explicit_services_not_the_root_provider()
    {
        var plugin = new AdminProbePlugin();
        var manifest = AdminManifest(
            "natter.test.admin-full",
            permissions:
            [
                PluginPermissions.AdminRead,
                PluginPermissions.AdminConfigWrite,
                PluginPermissions.AdminCommandExecute,
            ],
            capabilities: ["admin:diagnostics"]);

        await using var probe = await BuildProbeHostAsync(plugin, manifest);
        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);

        Assert.NotNull(provider.GetService<IAdminConfigStore>());
        Assert.NotNull(provider.GetService<IAdminCommandRunner>());
        Assert.NotNull(provider.GetService<IAdminLogReader>());
        Assert.Same(
            probe.Host.Services.GetRequiredService<IPluginRuntimeDiagnostics>(),
            provider.GetService<IPluginRuntimeDiagnostics>());

        // The plugin provider never exposes the host root provider or host-only services.
        Assert.NotSame(probe.Host.Services, provider.GetService<IServiceProvider>());
        Assert.Null(provider.GetService<IPluginCatalog>());
        Assert.Null(provider.GetService<AdminOptions>());
        await probe.Host.StopAsync();
    }

    [Fact]
    public async Task Plugin_without_admin_permissions_keeps_the_501_fallback()
    {
        var plugin = new AdminProbePlugin();
        var manifest = AdminManifest("natter.test.no-admin", permissions: [PluginPermissions.Network]);

        await using var probe = await BuildProbeHostAsync(plugin, manifest);
        var provider = Assert.IsAssignableFrom<IServiceProvider>(plugin.Provider);

        Assert.Null(provider.GetService<IPluginRuntimeDiagnostics>());
        Assert.Null(provider.GetService<IAdminLogReader>());
        Assert.Null(provider.GetService<IAdminConfigStore>());
        Assert.Null(provider.GetService<IAdminCommandRunner>());
        await probe.Host.StopAsync();
    }

    // ---------------------------------------------------------------- plugin CLI command snapshot (task-6)

    [Fact]
    public async Task Constructing_the_admin_runner_during_activation_does_not_freeze_cli_commands()
    {
        var baseline = await CountBuiltInCommandsAsync();

        var webConsole = new CliContributionPlugin("web status");
        var greeting = new CliContributionPlugin("greet");

        var webConsoleManifest = AdminManifest(
            "natter.test.webconsole-like",
            permissions:
            [
                PluginPermissions.AdminRead,
                PluginPermissions.AdminConfigWrite,
                PluginPermissions.AdminCommandExecute,
                PluginPermissions.CliCommands,
            ],
            capabilities: ["admin:diagnostics"],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "web.status" }],
            entryType: typeof(CliContributionPlugin));

        var greetingManifest = AdminManifest(
            "natter.test.greet",
            permissions: [PluginPermissions.CliCommands],
            contributions: [new PluginContribution { Type = PluginContributionTypes.CliCommand, Id = "greet" }],
            entryType: typeof(CliContributionPlugin));

        await using var probe = await BuildMultiPluginHostAsync(
            (webConsoleManifest, webConsole),
            (greetingManifest, greeting));

        var registry = probe.Host.Services.GetRequiredService<CliCommandRegistry>();
        Assert.True(registry.TryGet("web status", out _));
        Assert.True(registry.TryGet("greet", out _));
        Assert.Equal(baseline + 2, registry.CommandCount);

        // The admin plugin keeps the runner; the plugin without admin.command.execute keeps 501.
        var webConsoleProvider = Assert.IsAssignableFrom<IServiceProvider>(webConsole.Provider);
        Assert.NotNull(webConsoleProvider.GetService<IAdminCommandRunner>());
        var greetingProvider = Assert.IsAssignableFrom<IServiceProvider>(greeting.Provider);
        Assert.Null(greetingProvider.GetService<IAdminCommandRunner>());

        var runner = probe.Host.Services.GetRequiredService<IAdminCommandRunner>();

        var doctor = await runner.RunAsync("doctor", ["--json"]);
        Assert.True(doctor.Executed);
        Assert.Equal(CliExitCodes.Success, doctor.ExitCode);
        using (var document = JsonDocument.Parse(doctor.Stdout))
        {
            Assert.Equal(baseline + 2, document.RootElement.GetProperty("commands").GetInt32());
        }

        var webStatus = await runner.RunAsync("web status", []);
        Assert.True(webStatus.Succeeded);
        Assert.Contains("web status ok", webStatus.Stdout);
    }

    // ---------------------------------------------------------------- permission vocabulary

    [Fact]
    public void Admin_permissions_are_part_of_the_closed_vocabulary()
    {
        Assert.True(PluginPermissions.IsKnown(PluginPermissions.AdminRead));
        Assert.True(PluginPermissions.IsKnown(PluginPermissions.AdminConfigWrite));
        Assert.True(PluginPermissions.IsKnown(PluginPermissions.AdminCommandExecute));
        Assert.False(PluginPermissions.IsKnown("admin.config.delete"));

        var (errors, _) = PluginManifestReader.ValidateManifest(
            AdminManifest("natter.test.unknown-permission", permissions: ["admin.config.delete"]),
            AppContext.BaseDirectory);
        Assert.Contains(errors, error => error.Code == "unknown-permission");

        var (knownErrors, _) = PluginManifestReader.ValidateManifest(
            AdminManifest(
                "natter.test.known-permission",
                permissions:
                [
                    PluginPermissions.AdminRead,
                    PluginPermissions.AdminConfigWrite,
                    PluginPermissions.AdminCommandExecute,
                ]),
            AppContext.BaseDirectory);
        Assert.DoesNotContain(knownErrors, error => error.Code == "unknown-permission");
    }

    [Fact]
    public void Host_policy_can_force_grant_or_deny_admin_permissions()
    {
        var manifest = AdminManifest(
            "natter.test.force",
            permissions: [PluginPermissions.AdminRead, PluginPermissions.AdminCommandExecute]);

        var forcedGrant = new NpsPluginOptions();
        forcedGrant.ForceGrantedPermissions.Add(PluginPermissions.AdminCommandExecute);
        var grantedDecision = NpsPermissionPolicy.Decide(manifest, PluginTrustLevel.Untrusted, forcedGrant);

        Assert.Contains(PluginPermissions.AdminCommandExecute, grantedDecision.Granted);
        Assert.Contains(PluginPermissions.AdminRead, grantedDecision.Denied);

        var forcedDeny = new NpsPluginOptions();
        forcedDeny.ForceDeniedPermissions.Add(PluginPermissions.AdminRead);
        var deniedDecision = NpsPermissionPolicy.Decide(manifest, PluginTrustLevel.Bundled, forcedDeny);

        Assert.Contains(PluginPermissions.AdminRead, deniedDecision.Denied);
        Assert.Contains(PluginPermissions.AdminCommandExecute, deniedDecision.Granted);
    }

    [Theory]
    [InlineData(PluginTrustLevel.Bundled, true)]
    [InlineData(PluginTrustLevel.Local, true)]
    [InlineData(PluginTrustLevel.Untrusted, false)]
    public void Admin_permissions_follow_the_trust_grant_chain(PluginTrustLevel trust, bool granted)
    {
        var manifest = AdminManifest(
            "natter.test.grant",
            permissions:
            [
                PluginPermissions.AdminRead,
                PluginPermissions.AdminConfigWrite,
                PluginPermissions.AdminCommandExecute,
            ]);

        var decision = NpsPermissionPolicy.Decide(manifest, trust, new NpsPluginOptions());

        Assert.Equal(
            new[]
            {
                PluginPermissions.AdminCommandExecute,
                PluginPermissions.AdminConfigWrite,
                PluginPermissions.AdminRead,
            }.OrderBy(permission => permission, StringComparer.Ordinal),
            decision.Requested.OrderBy(permission => permission, StringComparer.Ordinal));

        if (granted)
        {
            Assert.Contains(PluginPermissions.AdminRead, decision.Granted);
            Assert.Contains(PluginPermissions.AdminConfigWrite, decision.Granted);
            Assert.Contains(PluginPermissions.AdminCommandExecute, decision.Granted);
            Assert.Empty(decision.Denied);
        }
        else
        {
            Assert.Empty(decision.Granted);
            Assert.Contains(PluginPermissions.AdminConfigWrite, decision.Denied);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static HostAdminConfigStore CreateStore(
        AdminWorkspace workspace,
        Action<AdminOptions>? configure = null,
        IPluginCatalog? catalog = null)
    {
        var options = new AdminOptions
        {
            ConfigDirectory = workspace.ConfigDir,
            StateDirectory = workspace.StateDir,
        };
        configure?.Invoke(options);

        var resolvedCatalog = catalog ?? new FakePluginCatalog();
        return new HostAdminConfigStore(
            options,
            resolvedCatalog,
            new HostAdminConfigSchemaValidator(resolvedCatalog, NullLogger<HostAdminConfigSchemaValidator>.Instance),
            NullAdminAuditSink.Instance,
            NullLogger<HostAdminConfigStore>.Instance);
    }

    private static PluginManifest AdminManifest(
        string id,
        string[]? permissions = null,
        string[]? capabilities = null,
        PluginContribution[]? contributions = null,
        Type? entryType = null)
        => new()
        {
            SchemaVersion = 1,
            Id = id,
            Name = id,
            Version = "0.1.0",
            Kind = PluginKind.Tool,
            HostApi = ">=0.1.0 <1.0.0",
            Entry = new PluginEntry
            {
                Assembly = "Natter.Framework.Tests.dll",
                Type = (entryType ?? typeof(AdminProbePlugin)).AssemblyQualifiedName!,
            },
            Permissions = permissions?.ToList(),
            Capabilities = capabilities?.ToList(),
            Contributions = contributions?.ToList(),
        };

    private static async Task<int> CountBuiltInCommandsAsync()
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>();

        await using var host = builder.Build();
        await host.StartAsync();
        return host.Services.GetRequiredService<CliCommandRegistry>().CommandCount;
    }

    private static async Task<ProbeHost> BuildMultiPluginHostAsync(
        params (PluginManifest Manifest, INatterPlugin Plugin)[] plugins)
    {
        var workspace = new AdminWorkspace();
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options =>
            {
                options.PluginConfigDirectory = workspace.ConfigDir;
                options.StateDirectory = workspace.StateDir;
            })
            .ConfigureAdmin(options => options.CommandAllowlist.Add("web status"));

        foreach (var (manifest, plugin) in plugins)
            builder.AddEmbeddedNpsPlugin(manifest, plugin);

        var host = builder.Build();
        await host.StartAsync();
        return new ProbeHost(host, workspace);
    }

    private static async Task<ProbeHost> BuildProbeHostAsync(AdminProbePlugin plugin, PluginManifest manifest)
    {
        var workspace = new AdminWorkspace();
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureOptions(options =>
            {
                options.PluginConfigDirectory = workspace.ConfigDir;
                options.StateDirectory = workspace.StateDir;
            })
            .AddEmbeddedNpsPlugin(manifest, plugin);

        var host = builder.Build();
        await host.StartAsync();
        return new ProbeHost(host, workspace);
    }

    /// <summary>Host wrapper that keeps the temp workspace alive for the lifetime of the test.</summary>
    private sealed class ProbeHost(NatterHost host, AdminWorkspace workspace) : IAsyncDisposable
    {
        public NatterHost Host { get; } = host;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                workspace.Dispose();
            }
        }
    }

    /// <summary>Plugin that records the provider handed to it by the NPS runtime.</summary>
    public sealed class AdminProbePlugin : INatterPlugin
    {
        public IServiceProvider? Provider { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Provider = context.Services.Provider;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>NPS plugin that contributes one CLI command (used to emulate WebConsole/greet).</summary>
    public sealed class CliContributionPlugin(string commandName) : INatterPlugin
    {
        public IServiceProvider? Provider { get; private set; }

        public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            context.Contributions.AddCliCommand(new CliCommandDescriptor
            {
                Name = commandName,
                Description = "Admin bridge regression command.",
                Origin = "test",
                HandlerFactory = _ => new FixedOutputHandler(commandName + " ok"),
            });

            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        {
            Provider = context.Services.Provider;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        private sealed class FixedOutputHandler(string output) : ICliCommandHandler
        {
            public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
            {
                invocation.Console.Out.WriteLine(output);
                return ValueTask.FromResult(CliExitCodes.Success);
            }
        }
    }

    private sealed class FakePluginCatalog(params PluginDescriptor[] plugins) : IPluginCatalog
    {
        public IReadOnlyList<PluginDescriptor> Plugins { get; } = plugins;

        public IReadOnlyList<IPluginModule> Modules { get; } = [];

        public bool Contains(string pluginId)
            => Plugins.Any(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
    }

    /// <summary>Temp root with <c>config/</c>, <c>state/</c> and helper plugin packages.</summary>
    private sealed class AdminWorkspace : IDisposable
    {
        public const string ValidProfiles = """
            {
              "schemaVersion": 1,
              "defaultProfile": "tier1-default",
              "profiles": [
                {
                  "id": "tier1-default",
                  "supportTier": 1,
                  "ai": { "provider": "natter.feature.ai#default" }
                }
              ]
            }
            """;

        public const string ProfilesWithExtraProfile = """
            {
              "schemaVersion": 1,
              "defaultProfile": "tier1-default",
              "profiles": [
                {
                  "id": "tier1-default",
                  "supportTier": 1,
                  "ai": { "provider": "natter.feature.ai#default" }
                },
                {
                  "id": "secondary",
                  "supportTier": 2,
                  "ai": { "provider": "natter.feature.ai#secondary" }
                }
              ]
            }
            """;

        public AdminWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "natter-admin-" + Guid.NewGuid().ToString("N"));
            ConfigDir = Path.Combine(Root, "config");
            StateDir = Path.Combine(Root, "state");
            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(StateDir);
        }

        public string Root { get; }

        public string ConfigDir { get; }

        public string StateDir { get; }

        public string ConfigPath(string fileName) => Path.Combine(ConfigDir, fileName);

        public void WriteConfig(string fileName, string content)
            => File.WriteAllText(ConfigPath(fileName), content);

        public PluginDescriptor CreatePluginPackage(string pluginId, string? schema = null)
        {
            var directory = Path.Combine(Root, "plugins", pluginId);
            Directory.CreateDirectory(directory);

            var manifest = new
            {
                schemaVersion = 1,
                id = pluginId,
                name = pluginId,
                version = "0.1.0",
                kind = "tool",
                hostApi = ">=0.1.0 <1.0.0",
                entry = new { assembly = "Natter.Test.Plugin.dll", type = "Natter.Test.Plugin.Entry" },
                configSchema = schema is null ? null : "config.schema.json",
            };

            File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(manifest));
            if (schema is not null)
                File.WriteAllText(Path.Combine(directory, "config.schema.json"), schema);

            return new PluginDescriptor(
                pluginId,
                pluginId,
                "0.1.0",
                Path.Combine(directory, "Natter.Test.Plugin.dll"),
                directory,
                PluginState.Configured,
                Kind: "tool",
                HostApi: ">=0.1.0 <1.0.0");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>Host fixture with test commands, admin options and log/audit surfaces.</summary>
    private sealed class AdminHostFixture : IAsyncDisposable
    {
        private AdminHostFixture(NatterHost host, AdminWorkspace workspace, CommandRecorder recorder)
        {
            Host = host;
            Workspace = workspace;
            Recorder = recorder;
        }

        public NatterHost Host { get; }

        public AdminWorkspace Workspace { get; }

        public CommandRecorder Recorder { get; }

        public IAdminCommandRunner Runner => Host.Services.GetRequiredService<IAdminCommandRunner>();

        public IAdminLogReader LogReader => Host.Services.GetRequiredService<IAdminLogReader>();

        public static async Task<AdminHostFixture> CreateAsync(Action<AdminOptions>? configure = null)
        {
            var workspace = new AdminWorkspace();
            var recorder = new CommandRecorder();

            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddModule<CliCoreCompositionModule>()
                .ConfigureOptions(options =>
                {
                    options.PluginConfigDirectory = workspace.ConfigDir;
                    options.StateDirectory = workspace.StateDir;
                })
                .ConfigureAdmin(options =>
                {
                    options.ConfigDirectory = workspace.ConfigDir;
                    options.StateDirectory = workspace.StateDir;
                    options.CommandAllowlist.Add("admin echo");
                    options.CommandAllowlist.Add("admin slow");
                    options.CommandAllowlist.Add("admin noisy");
                    options.CommandAllowlist.Add("admin args");
                    options.CommandAllowlist.Add("admin serial");
                    configure?.Invoke(options);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(TestDescriptor("admin echo", (invocation, _) =>
                    {
                        recorder.Echo();
                        invocation.Console.Out.WriteLine("hello from admin");
                        return ValueTask.FromResult(CliExitCodes.Success);
                    }));
                    services.AddSingleton(TestDescriptor("admin slow", async (_, token) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return CliExitCodes.Success;
                    }));
                    services.AddSingleton(TestDescriptor("admin noisy", (invocation, _) =>
                    {
                        invocation.Console.Out.WriteLine("token=sk-abcdefgh12345678");
                        invocation.Console.Out.WriteLine("api_key=plain-value-123");
                        invocation.Console.Out.WriteLine("password: hunter2-secret");
                        invocation.Console.Out.WriteLine("Authorization: Bearer abcdefghijklmnop");
                        invocation.Console.Out.WriteLine(new string('x', 100_000));
                        return ValueTask.FromResult(CliExitCodes.Success);
                    }));
                    services.AddSingleton(TestDescriptor("admin args", (invocation, _) =>
                    {
                        recorder.CaptureArgs(invocation.Positionals.ToArray());
                        return ValueTask.FromResult(CliExitCodes.Success);
                    }));
                    services.AddSingleton(TestDescriptor("admin serial", async (invocation, token) =>
                    {
                        recorder.Enter();
                        try
                        {
                            var accessor = invocation.Services.GetRequiredService<IConsoleAccessor>();
                            recorder.CheckConsole(ReferenceEquals(accessor.Current, invocation.Console));
                            await Task.Delay(150, token);
                        }
                        finally
                        {
                            recorder.Exit();
                        }

                        return CliExitCodes.Success;
                    }));
                });

            var host = builder.Build();
            await host.StartAsync();
            return new AdminHostFixture(host, workspace, recorder);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
            }
            finally
            {
                Workspace.Dispose();
            }
        }

        private static CliCommandDescriptor TestDescriptor(
            string name,
            Func<CliInvocation, CancellationToken, ValueTask<int>> execute)
            => new()
            {
                Name = name,
                Description = "Admin contract test command.",
                Origin = "test",
                HandlerFactory = _ => new TestAdminHandler(execute),
            };

        private sealed class TestAdminHandler(Func<CliInvocation, CancellationToken, ValueTask<int>> execute)
            : ICliCommandHandler
        {
            public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
                => execute(invocation, cancellationToken);
        }

        public sealed class CommandRecorder
        {
            private int _echo;
            private int _active;
            private int _maxConcurrency;
            private int _consoleMatched = 1;

            public int EchoInvocations => Volatile.Read(ref _echo);

            public int MaxObservedConcurrency => Volatile.Read(ref _maxConcurrency);

            public bool ConsoleAccessorMatched => Volatile.Read(ref _consoleMatched) == 1;

            public string[] LastArgs { get; private set; } = [];

            public void Echo() => Interlocked.Increment(ref _echo);

            public void CaptureArgs(string[] args) => LastArgs = args;

            public void Enter()
            {
                var active = Interlocked.Increment(ref _active);
                var observed = Volatile.Read(ref _maxConcurrency);
                while (active > observed)
                {
                    var previous = Interlocked.CompareExchange(ref _maxConcurrency, active, observed);
                    if (previous == observed)
                        break;

                    observed = previous;
                }
            }

            public void Exit() => Interlocked.Decrement(ref _active);

            public void CheckConsole(bool matched)
            {
                if (!matched)
                    Volatile.Write(ref _consoleMatched, 0);
            }
        }
    }
}
