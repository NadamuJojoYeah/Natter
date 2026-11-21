using Xunit;

namespace Natter.Plugin.WebConsole.Tests;

public sealed class WebConsoleAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Audit_store_is_writable_and_redacts_targets()
    {
        var directory = CreateDirectory();
        try
        {
            using var store = new WebConsoleAuditStore(directory, new FakeTimeProvider(Now));

            Assert.True(store.CanWrite(out var error));
            Assert.Null(error);

            store.Write(
                "config.write",
                "admin",
                "127.0.0.1",
                "token sk-abcdefghijklmnop",
                "denied",
                TimeSpan.FromMilliseconds(12));

            var entries = store.Read(10);
            var entry = Assert.Single(entries);
            Assert.Equal("config.write", entry.Action);
            Assert.Contains("[redacted]", entry.Target);
            Assert.DoesNotContain("sk-abcdefghijklmnop", entry.Target);
            Assert.Equal(12, entry.DurationMs);

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(store.FilePath);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                            | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Audit_reports_read_only_files()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = CreateDirectory();
        try
        {
            using var store = new WebConsoleAuditStore(directory, new FakeTimeProvider(Now));
            store.Write("auth.login", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            File.SetUnixFileMode(store.FilePath, UnixFileMode.UserRead);

            Assert.False(store.CanWrite(out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            File.SetUnixFileMode(
                Path.Combine(directory, WebConsoleDefaults.AuditFileName),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Audit_read_skips_corrupt_lines()
    {
        var directory = CreateDirectory();
        try
        {
            using var store = new WebConsoleAuditStore(directory, new FakeTimeProvider(Now));
            store.Write("auth.login", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            File.AppendAllText(store.FilePath, "{not-json}\n");
            store.Write("auth.logout", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            var entries = store.Read(10);
            Assert.Equal(2, entries.Count);
            Assert.Equal("auth.login", entries[0].Action);
            Assert.Equal("auth.logout", entries[1].Action);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Audit_read_limit_returns_newest_entries()
    {
        var directory = CreateDirectory();
        try
        {
            using var store = new WebConsoleAuditStore(directory, new FakeTimeProvider(Now));

            for (var index = 0; index < 5; index++)
                store.Write($"action.{index}", "admin", "127.0.0.1", null, "ok", TimeSpan.Zero);

            var entries = store.Read(2);
            Assert.Equal(["action.3", "action.4"], entries.Select(entry => entry.Action));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-webconsole-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
