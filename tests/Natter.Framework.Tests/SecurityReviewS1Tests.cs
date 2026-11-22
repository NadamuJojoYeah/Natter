using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Commands;
using Natter.Infrastructure.Chat;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Regression tests for docs/CODE_REVIEW_2026-11-22.md S1-01 (plugin store path traversal delete),
/// S1-02 (proxy silences the fetch SSRF connect check) and S1-08 (Android Release INTERNET).
/// </summary>
public sealed class SecurityReviewS1Tests
{
    // ------------------------------------------------------------------ S1-01

    [Fact]
    public async Task Uninstall_rejects_absolute_paths_without_deleting_the_target()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        var outside = temp.Combine("outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");

        var exception = await Assert.ThrowsAsync<PluginStoreException>(() =>
            store.UninstallAsync(outside, deleteData: true, deleteConfig: true, force: true));

        Assert.Contains(exception.Issues, issue => issue.Code == "plugin-id-invalid");
        Assert.True(File.Exists(marker), "absolute-path id must not delete the target directory");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("Natter.Upper")]
    [InlineData("a/b")]
    [InlineData("natter/../x")]
    [InlineData("")]
    public async Task Uninstall_rejects_ids_outside_the_manifest_shape(string id)
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        await Assert.ThrowsAsync<PluginStoreException>(() =>
            store.UninstallAsync(id, deleteData: true, deleteConfig: true, force: true));
    }

    [Fact]
    public async Task Uninstall_rejects_overlong_ids()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        var id = "a" + new string('b', PluginStoreIds.MaxLength);

        await Assert.ThrowsAsync<PluginStoreException>(() =>
            store.UninstallAsync(id, deleteData: true, deleteConfig: true, force: true));
        Assert.False(PluginStoreIds.IsValid(id));
        Assert.True(PluginStoreIds.IsValid("natter.plugin.sample"));
    }

    [Fact]
    public async Task Unknown_plugin_data_and_config_are_kept_without_force_and_removed_with_force()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.test.unknown";
        var dataDirectory = store.GetPluginDataDirectory(id);
        var configPath = store.GetPluginConfigPath(id);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "state.txt"), "state");
        await File.WriteAllTextAsync(configPath, "{}");

        // Not installed and no --force: nothing may be deleted.
        await store.UninstallAsync(id, deleteData: true, deleteConfig: true);
        Assert.True(Directory.Exists(dataDirectory));
        Assert.True(File.Exists(configPath));

        // Explicit --force is the only way to clean an already-uninstalled plugin's files.
        await store.UninstallAsync(id, deleteData: true, deleteConfig: true, force: true);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public async Task Uninstall_does_not_follow_symlinked_data_directories()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.test.symlink";
        var outside = temp.Combine("outside-data");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        Directory.CreateDirectory(store.DataDirectory);
        var link = store.GetPluginDataDirectory(id);
        File.CreateSymbolicLink(link, outside);

        await store.UninstallAsync(id, deleteData: true, force: true);

        Assert.True(File.Exists(marker), "the symlink target must never be deleted");
        Assert.False(Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is null);
    }

    [Fact]
    public void Path_helpers_reject_invalid_ids()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        Assert.Throws<PluginStoreException>(() => store.GetPluginDataDirectory("/tmp/evil"));
        Assert.Throws<PluginStoreException>(() => store.GetPluginConfigPath("../evil"));
        Assert.EndsWith(Path.Combine("plugin-data", "natter.ok"), store.GetPluginDataDirectory("natter.ok"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_uninstall_rejects_invalid_id_with_a_stable_error_code()
    {
        var command = new PluginsStoreUninstallCommand();
        var console = new StringConsole();
        using var provider = new ServiceCollection().BuildServiceProvider();

        var exitCode = await command.ExecuteAsync(new CliInvocation(
            "plugins store uninstall",
            new Dictionary<string, string?>(),
            ["/abs/path"],
            provider,
            console));

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("plugin-id-invalid", console.ErrorText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ S1-02

    [Fact]
    public async Task Fetch_handler_disables_proxy_so_connect_time_validation_cannot_be_bypassed()
    {
        var handler = ChatCompositionModule.CreateHttpFetchHandler();

        // S1-02: no proxy hop, so the connect callback always sees the real target.
        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);

        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://localhost:1/"));

        Assert.True(
            ContainsMessage(exception, "private"),
            $"expected the SSRF refusal from the connect callback, got: {exception}");
    }

    // ------------------------------------------------------------------ S1-08

    [Fact]
    public void Android_manifest_declares_internet_for_release_builds()
    {
        var manifestPath = Path.Combine(
            RepoRoot(),
            "src",
            "Natter.Hosting.Android",
            "Properties",
            "AndroidManifest.xml");
        Assert.True(File.Exists(manifestPath), $"missing manifest: {manifestPath}");
        var manifest = File.ReadAllText(manifestPath);

        Assert.Equal(1, CountOccurrences(manifest, "android.permission.INTERNET"));
        Assert.Equal(1, CountOccurrences(manifest, "<uses-permission"));
        Assert.DoesNotContain("android.permission.FOREGROUND_SERVICE", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_release_manifest_keeps_internet_when_present()
    {
        var androidRoot = Path.Combine(RepoRoot(), "src", "Natter.Hosting.Android");
        var sourceManifest = Path.Combine(androidRoot, "Properties", "AndroidManifest.xml");
        var sourceWriteTime = File.GetLastWriteTimeUtc(sourceManifest);
        var generated = Directory.Exists(androidRoot)
            ? Directory.GetFiles(androidRoot, "AndroidManifest.xml", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.Combine("obj", "Release"), StringComparison.Ordinal))
                // A manifest generated before this fix is stale output, not evidence about the
                // current source; only check artifacts built after the source change.
                .Where(path => File.GetLastWriteTimeUtc(path) >= sourceWriteTime)
                .ToArray()
            : [];
        if (generated.Length == 0)
            return; // No fresh Release build locally; the source assertion above covers it.

        foreach (var path in generated)
            Assert.Contains("android.permission.INTERNET", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static PluginStore CreateStore(TempDirectory temp)
        => new(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            AllowUnsigned = true,
        });

    private static bool ContainsMessage(Exception? exception, string fragment)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-s1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
