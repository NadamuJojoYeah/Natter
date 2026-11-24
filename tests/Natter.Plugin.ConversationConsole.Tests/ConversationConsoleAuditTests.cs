using System.Text.Json;
using Natter.Application.Chat;
using Xunit;

namespace Natter.Plugin.ConversationConsole.Tests;

/// <summary>
/// Audit requirements (design §4.2): every view is recorded, the record carries only
/// action/sessionId/messageCount/actor, and the file lives under the writable state root — never
/// inside the plugin directory, which is commonly mounted read-only.
/// </summary>
public sealed class ConversationConsoleAuditTests
{
    [Fact]
    public async Task Viewing_a_session_writes_an_audit_record_without_message_text()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery()
            .WithSession("s-1", "discord:discord:11")
            .WithMessages("s-1", "secret-customer-text-A", "secret-customer-text-B");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        var response = await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions/s-1/messages"));
        Assert.Equal(200, response.StatusCode);

        var entry = Assert.Single(workspace.ReadAudit());
        Assert.Equal(ConversationConsoleAuditActions.View, entry.Action);
        Assert.Equal("s-1", entry.SessionId);
        Assert.Equal(2, entry.MessageCount);
        Assert.Equal("token:console", entry.Actor);

        // The body text is delivered to the browser but must never be persisted in the audit log.
        var auditText = workspace.ReadAuditText();
        Assert.DoesNotContain("secret-customer-text-A", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-customer-text-B", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(ConversationConsoleTestFactory.ValidToken, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_writes_a_count_only_record()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery()
            .WithSession("s-1", "discord:discord:11")
            .WithSession("s-2", "discord:discord:22");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));

        var entry = Assert.Single(workspace.ReadAudit());
        Assert.Equal(ConversationConsoleAuditActions.List, entry.Action);
        Assert.Null(entry.SessionId);
        Assert.Equal(2, entry.MessageCount);
    }

    [Fact]
    public async Task Audit_records_only_the_documented_fields()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithMessages("s-1", "text");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions/s-1/messages"));

        foreach (var line in File.ReadAllLines(workspace.AuditFile))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

            Assert.All(
                names,
                name => Assert.Contains(name, new[] { "at", "action", "sessionId", "messageCount", "actor" }));
            Assert.Contains("at", names);
            Assert.Contains("action", names);
        }
    }

    [Fact]
    public async Task Audit_file_lives_under_the_state_root_and_never_in_the_plugin_directory()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var pluginFilesBefore = Directory.GetFiles(workspace.PluginDir, "*", SearchOption.AllDirectories);
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:11"));

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));

        Assert.True(File.Exists(workspace.AuditFile), $"expected audit at {workspace.AuditFile}");
        Assert.StartsWith(
            Path.GetFullPath(workspace.StateDir),
            Path.GetFullPath(workspace.AuditFile),
            StringComparison.Ordinal);

        var pluginFilesAfter = Directory.GetFiles(workspace.PluginDir, "*", SearchOption.AllDirectories);
        Assert.Equal(pluginFilesBefore.Length, pluginFilesAfter.Length);
        Assert.DoesNotContain(
            pluginFilesAfter,
            file => file.EndsWith(ConversationConsoleDefaults.AuditFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void State_directory_is_required_because_the_plugin_directory_is_not_writable()
    {
        var exception = Assert.Throws<ConversationConsoleStartupException>(
            () => ConversationConsoleAuditPaths.Resolve(null));

        Assert.Equal("conversation-console.state-dir-required", exception.Code);
        Assert.Throws<ConversationConsoleStartupException>(
            () => ConversationConsoleAuditPaths.Resolve("   "));
    }

    [Fact]
    public void Audit_location_reports_the_state_root_and_targets_the_plugin_subdirectory()
    {
        var location = ConversationConsoleAuditPaths.Resolve("/var/lib/natter");

        Assert.True(location.IsStateDirectory);
        Assert.Equal("state-directory", location.Root);
        Assert.Equal(
            Path.Combine("/var/lib/natter", "conversation-console", "audit", ConversationConsoleDefaults.AuditFileName),
            location.FilePath);
    }

    [Fact]
    public async Task Denied_authentication_is_audited_without_the_presented_token()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:11"));

        const string attempted = "attacker-supplied-token-0000000000";
        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions", token: attempted));

        var entry = Assert.Single(workspace.ReadAudit());
        Assert.Equal(ConversationConsoleAuditActions.AuthDenied, entry.Action);
        Assert.DoesNotContain(attempted, workspace.ReadAuditText(), StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", entry.Actor ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unauthenticated_audit_writes_are_throttled_per_client()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: new FakeConversationQuery());

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await handler.HandleAsync(
                ConversationConsoleTestFactory.Request("/api/sessions", token: "wrong-token-000000000000"));
        }

        var denied = workspace.ReadAudit()
            .Count(entry => entry.Action == ConversationConsoleAuditActions.AuthDenied);

        Assert.InRange(denied, 1, ConversationConsoleAuditGate.Burst);
    }

    [Fact]
    public async Task Successful_views_are_not_throttled()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithMessages("s-1", "text");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            await handler.HandleAsync(
                ConversationConsoleTestFactory.Request("/api/sessions/s-1/messages"));
        }

        var views = workspace.ReadAudit()
            .Count(entry => entry.Action == ConversationConsoleAuditActions.View);

        Assert.Equal(8, views);
    }

    [Fact]
    public async Task Audit_file_is_private_on_posix()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var workspace = new ConversationConsoleWorkspace();
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(
            workspace,
            query: new FakeConversationQuery().WithSession("s-1", "discord:discord:11"));

        await handler.HandleAsync(ConversationConsoleTestFactory.Request("/api/sessions"));

        var mode = File.GetUnixFileMode(workspace.AuditFile);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);

        var directoryMode = File.GetUnixFileMode(Path.GetDirectoryName(workspace.AuditFile)!);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            directoryMode);
    }

    [Fact]
    public async Task Session_ids_are_sanitized_before_being_written()
    {
        using var workspace = new ConversationConsoleWorkspace();
        var fake = new FakeConversationQuery().WithMessages("s%0Ainjected", "text");
        var (handler, _) = ConversationConsoleTestFactory.CreateHandler(workspace, query: fake);

        await handler.HandleAsync(
            ConversationConsoleTestFactory.Request("/api/sessions/s%0Ainjected/messages"));

        foreach (var line in File.ReadAllLines(workspace.AuditFile))
            Assert.DoesNotContain('\n', line);

        var entry = Assert.Single(workspace.ReadAudit());
        Assert.DoesNotContain('\n', entry.SessionId ?? string.Empty);
    }
}
