using System.Text.Json;
using Xunit;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// The audit log must live on a writable root: systemd (<c>/var/lib/natter</c>) and containers
/// (<c>/state</c>) both mount the plugin package read-only. Only an environment without
/// <c>NATTER_STATE_DIR</c> falls back to the plugin directory, and that fallback is reported.
/// </summary>
public sealed class AuditPathTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void State_root_wins_and_matches_the_documented_layout()
    {
        var pluginDirectory = Path.Combine(Path.GetTempPath(), "wc-plugin");

        var location = WebConsoleAuditPaths.Resolve(pluginDirectory, "/var/lib/natter");

        Assert.Equal(WebConsoleAuditRoot.StateDirectory, location.Root);
        Assert.Equal(Path.Combine("/var/lib/natter", "webconsole", "audit"), location.Directory);
        Assert.Equal(Path.Combine(location.Directory, WebConsoleDefaults.AuditFileName), location.FilePath);
        Assert.Null(location.Warning);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Missing_state_root_falls_back_to_the_plugin_directory_with_a_warning(string? stateDirectory)
    {
        var pluginDirectory = Path.Combine(Path.GetTempPath(), "wc-plugin-fallback");

        var location = WebConsoleAuditPaths.Resolve(pluginDirectory, stateDirectory);

        Assert.Equal(WebConsoleAuditRoot.PluginDirectory, location.Root);
        Assert.Equal(Path.Combine(pluginDirectory, "audit"), location.Directory);
        Assert.NotNull(location.Warning);
        Assert.Contains("NATTER_STATE_DIR", location.Warning!, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem", location.Warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_and_padded_state_roots_are_normalized()
    {
        var pluginDirectory = Path.Combine(Path.GetTempPath(), "wc-plugin-normalize");
        var relative = "relative-state";

        var location = WebConsoleAuditPaths.Resolve(pluginDirectory, "  " + relative + Path.DirectorySeparatorChar + " ");

        Assert.Equal(WebConsoleAuditRoot.StateDirectory, location.Root);
        Assert.True(Path.IsPathFullyQualified(location.Directory));
        Assert.EndsWith(Path.Combine("relative-state", "webconsole", "audit"), location.Directory);
    }

    [Fact]
    public void Container_layout_is_supported()
    {
        var location = WebConsoleAuditPaths.Resolve("/opt/natter/plugins/natter.plugin.webconsole", "/state");

        Assert.Equal(Path.Combine("/state", "webconsole", "audit"), location.Directory);
        Assert.Equal(WebConsoleAuditRoot.StateDirectory, location.Root);
    }

    [Fact]
    public void Environment_reader_matches_the_process_value()
    {
        var expected = Environment.GetEnvironmentVariable(WebConsoleDefaults.StateDirectoryEnvironmentVariable);

        Assert.Equal(expected, WebConsoleAuditPaths.ReadStateDirectoryFromEnvironment());
    }

    [Fact]
    public void Store_creates_every_missing_level_as_private()
    {
        var root = CreateDirectory();
        try
        {
            var stateRoot = Path.Combine(root, "state");
            var location = WebConsoleAuditPaths.Resolve(Path.Combine(root, "plugin"), stateRoot);

            using var store = new WebConsoleAuditStore(location.Directory, new FakeTimeProvider(Now));
            store.Write("auth.login", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            Assert.True(Directory.Exists(location.Directory));
            Assert.Equal(location.FilePath, store.FilePath);

            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(Private, File.GetUnixFileMode(stateRoot));
                Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(stateRoot, "webconsole")));
                Assert.Equal(Private, File.GetUnixFileMode(location.Directory));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(store.FilePath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_leaves_a_pre_existing_state_root_mode_untouched()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = CreateDirectory();
        try
        {
            var stateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(stateRoot);
            File.SetUnixFileMode(stateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

            var location = WebConsoleAuditPaths.Resolve(Path.Combine(root, "plugin"), stateRoot);
            using var store = new WebConsoleAuditStore(location.Directory, new FakeTimeProvider(Now));
            store.Write("auth.login", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            // Only directories we created are tightened; the service-managed state root is left alone.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute,
                File.GetUnixFileMode(stateRoot));
            Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(stateRoot, "webconsole")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unwritable_audit_root_surfaces_an_io_failure()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        var root = CreateDirectory();
        try
        {
            var stateRoot = Path.Combine(root, "state");
            Directory.CreateDirectory(stateRoot);
            File.SetUnixFileMode(stateRoot, UnixFileMode.None);

            var exception = Record.Exception(() =>
                new WebConsoleAuditStore(Path.Combine(stateRoot, "webconsole", "audit"), new FakeTimeProvider(Now)));

            Assert.NotNull(exception);
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"unexpected exception type: {exception!.GetType().Name}");
        }
        finally
        {
            File.SetUnixFileMode(
                Path.Combine(root, "state"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    private const UnixFileMode Private =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-audit-path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
