using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Independent acceptance for the 2026-11-22 review fixes (task-30): plugin-store uninstall ids
/// (S1-01), SSRF with proxy environment variables (S1-02), Android Release manifest (S1-08),
/// CI coverage (S1-09) and the prompt fragment responsibility boundary (task-29).
/// The WebConsole validate redaction (S1-04) and soak verdicts (S1-10) are verified with HTTP probes
/// and script reruns; evidence is recorded in docs/testing/2026-11-22-review-acceptance.md.
/// </summary>
public sealed class ReviewAcceptanceTests
{
    private const string Task29BaselineSha256 =
        "750fb332407ebe809b2b15920ed513f8bcfa9a524028dfa2b5f5c0520e37f474";

    // ------------------------------------------------------------ S1-01 uninstall

    [Fact]
    public async Task Uninstall_rejects_an_absolute_path_and_leaves_the_target_intact()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        var outside = temp.Combine("outside-target");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await File.WriteAllTextAsync(Path.Combine(outside, "second.txt"), "keep");

        var exception = await Assert.ThrowsAsync<PluginStoreException>(() =>
            store.UninstallAsync(outside, deleteData: true, deleteConfig: true, force: true));

        Assert.Contains(exception.Issues, issue => issue.Code == "plugin-id-invalid");
        Assert.True(File.Exists(marker), "the absolute-path target must not be deleted");
        Assert.Equal(2, Directory.GetFiles(outside).Length);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("Natter.Upper")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("natter/../x")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Uninstall_rejects_ids_that_are_not_manifest_safe(string? id)
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        var guard = temp.Combine("guard");
        Directory.CreateDirectory(guard);
        var marker = Path.Combine(guard, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");

        await Assert.ThrowsAsync<PluginStoreException>(() =>
            store.UninstallAsync(id!, deleteData: true, deleteConfig: true, force: true));

        Assert.True(File.Exists(marker));
        Assert.True(Directory.Exists(guard));
    }

    [Fact]
    public async Task Uninstall_rejects_an_overlong_id()
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
    public async Task Installed_plugin_is_moved_to_trash_and_its_data_and_config_are_removed()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.acceptance.installed";
        await MarkInstalledAsync(store, id);
        var pluginRoot = Path.Combine(store.RootDirectory, id);
        Directory.CreateDirectory(pluginRoot);
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "plugin.json"), "{}");
        Directory.CreateDirectory(store.GetPluginDataDirectory(id));
        await File.WriteAllTextAsync(Path.Combine(store.GetPluginDataDirectory(id), "state.txt"), "state");
        await File.WriteAllTextAsync(store.GetPluginConfigPath(id), "{}");

        await store.UninstallAsync(id, deleteData: true, deleteConfig: true);

        Assert.False(Directory.Exists(pluginRoot), "the install directory must leave the root");
        var trashed = Directory.Exists(store.TrashDirectory)
            ? Directory.GetDirectories(store.TrashDirectory, id, SearchOption.AllDirectories)
            : [];
        Assert.Single(trashed);
        Assert.True(File.Exists(Path.Combine(trashed[0], "plugin.json")), "the trashed copy keeps its files");
        Assert.False(Directory.Exists(store.GetPluginDataDirectory(id)));
        Assert.False(File.Exists(store.GetPluginConfigPath(id)));
        Assert.DoesNotContain($"\"{id}\"", await File.ReadAllTextAsync(store.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_deletes_only_the_requested_side_of_the_record()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.acceptance.partial";
        await MarkInstalledAsync(store, id);
        Directory.CreateDirectory(store.GetPluginDataDirectory(id));
        await File.WriteAllTextAsync(Path.Combine(store.GetPluginDataDirectory(id), "state.txt"), "state");
        await File.WriteAllTextAsync(store.GetPluginConfigPath(id), "{}");

        await store.UninstallAsync(id, deleteData: true, deleteConfig: false);

        Assert.False(Directory.Exists(store.GetPluginDataDirectory(id)));
        Assert.True(File.Exists(store.GetPluginConfigPath(id)), "config must survive a data-only uninstall");
    }

    [Fact]
    public async Task Unknown_id_with_delete_data_deletes_nothing_without_force()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.acceptance.unknown";
        StorageOf(store, id, out var dataDirectory, out var configPath);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "state.txt"), "state");
        await File.WriteAllTextAsync(configPath, "{}");

        await store.UninstallAsync(id, deleteData: true, deleteConfig: true);

        Assert.True(Directory.Exists(dataDirectory), "an unknown id must not delete data without --force");
        Assert.True(File.Exists(configPath), "an unknown id must not delete config without --force");

        await store.UninstallAsync(id, deleteData: true, deleteConfig: true, force: true);
        Assert.False(Directory.Exists(dataDirectory));
        Assert.False(File.Exists(configPath));
    }

    [Fact]
    public async Task Symlinked_data_directory_is_unlinked_without_deleting_its_target()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        const string id = "natter.acceptance.symlink";
        var outside = temp.Combine("outside-data");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        Directory.CreateDirectory(store.DataDirectory);
        var link = store.GetPluginDataDirectory(id);
        File.CreateSymbolicLink(link, outside);

        await store.UninstallAsync(id, deleteData: true, force: true);

        Assert.True(File.Exists(marker), "the symlink target content must survive");
        Assert.Equal(1, Directory.GetFiles(outside).Length);
        Assert.False(Directory.Exists(link), "only the link itself is removed");
    }

    // --------------------------------------------------------------- S1-02 SSRF

    [Fact]
    public void Host_fetch_handler_disables_proxy_and_redirects()
    {
        using var handler = ChatCompositionModule.CreateHttpFetchHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Proxy_environment_variables_cannot_bypass_private_target_checks()
    {
        using var proxy = new CountingListener();
        using var upstream = new CountingListener();
        var previous = SnapshotProxyEnvironment();
        try
        {
            SetProxyEnvironment(proxy.EndPoint);
            using var handler = ChatCompositionModule.CreateHttpFetchHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync($"https://127.0.0.1:{upstream.Port}/"));

            Assert.Contains("private", exception.Message, StringComparison.OrdinalIgnoreCase);
            // Neither the proxy nor the target saw a connection: the address check runs first.
            Assert.Equal(0, proxy.AcceptedConnections);
            Assert.Equal(0, upstream.AcceptedConnections);
        }
        finally
        {
            RestoreProxyEnvironment(previous);
        }
    }

    [Fact]
    public async Task Http_fetch_tool_rejects_metadata_private_and_loopback_targets_with_zero_requests()
    {
        using var upstream = new CountingListener();
        using var proxy = new CountingListener();
        var previous = SnapshotProxyEnvironment();
        try
        {
            SetProxyEnvironment(proxy.EndPoint);
            var allowlist = new HashSet<string>(["169.254.169.254", "127.0.0.1", "::1", "10.1.2.3"], StringComparer.OrdinalIgnoreCase);
            using var handler = ChatCompositionModule.CreateHttpFetchHandler();
            using var http = new HttpClient(handler);
            var tool = new HttpFetchTool(http, allowlist);

            foreach (var url in new[]
                     {
                         "https://169.254.169.254/latest/meta-data/",
                         $"https://127.0.0.1:{upstream.Port}/admin",
                         $"https://[::1]:{upstream.Port}/admin",
                         "https://10.1.2.3/internal",
                     })
            {
                var result = await tool.ExecuteAsync(new ToolRequest(url));
                Assert.Equal("address_not_allowed", result.ErrorCode);
            }

            Assert.Equal(0, upstream.AcceptedConnections);
            Assert.Equal(0, proxy.AcceptedConnections);
        }
        finally
        {
            RestoreProxyEnvironment(previous);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.254", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("198.18.0.1", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Blocked_address_matrix_does_not_regress(string address, bool blocked)
    {
        Assert.Equal(blocked, HttpFetchTool.IsBlockedAddress(IPAddress.Parse(address)));
    }

    // ----------------------------------------------------- S1-08 Android manifest

    [Fact]
    public void Source_android_manifest_declares_exactly_one_internet_permission()
    {
        var manifest = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Natter.Hosting.Android", "Properties", "AndroidManifest.xml"));

        Assert.Equal(1, CountOccurrences(manifest, "android.permission.INTERNET"));
        Assert.Equal(1, CountOccurrences(manifest, "<uses-permission"));
    }

    [Fact]
    public void Release_build_manifest_keeps_exactly_one_internet_permission()
    {
        var androidRoot = Path.Combine(RepoRoot(), "src", "Natter.Hosting.Android");
        var generated = Directory.Exists(androidRoot)
            ? Directory.GetFiles(androidRoot, "AndroidManifest.xml", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.Combine("obj", "Release"), StringComparison.Ordinal))
                .ToArray()
            : [];

        Assert.True(
            generated.Length > 0,
            "a Release build of Natter.Hosting.Android is required for this acceptance; run "
            + "`dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c Release`");
        foreach (var path in generated)
        {
            var manifest = File.ReadAllText(path);
            Assert.Equal(1, CountOccurrences(manifest, "android.permission.INTERNET"));
            Assert.Equal(1, CountOccurrences(manifest, "<uses-permission"));
        }
    }

    // ---------------------------------------------------------------- S1-09 CI

    [Fact]
    public void Ci_builds_the_solution_and_runs_every_test_project()
    {
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("dotnet build Natter.slnx", ci, StringComparison.Ordinal);
        Assert.Contains("find tests -name '*.csproj'", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test \"$project\"", ci, StringComparison.Ordinal);

        var promptLibraryTests = Path.Combine(
            RepoRoot(), "tests", "Natter.Plugin.PromptLibrary.Tests", "Natter.Plugin.PromptLibrary.Tests.csproj");
        Assert.True(File.Exists(promptLibraryTests), $"missing PromptLibrary suite: {promptLibraryTests}");
    }

    // --------------------------------------------------- task-29 prompt boundary

    [Fact]
    public async Task Safety_is_on_by_default_disappears_when_disabled_and_restores_on_reenable()
    {
        var provider = new HostStaticPromptProvider();

        var enabled = await provider.GetAsync(Request(Profile()));
        var disabled = await provider.GetAsync(Request(Profile(disabled: [HostStaticPromptProvider.SafetyFragmentId])));
        var reenabled = await provider.GetAsync(Request(Profile()));

        Assert.Contains(
            enabled.Plan!.Fragments,
            fragment => fragment.Id == HostStaticPromptProvider.SafetyFragmentId);
        Assert.DoesNotContain(
            disabled.Plan!.Fragments,
            fragment => fragment.Id == HostStaticPromptProvider.SafetyFragmentId);
        Assert.Contains(
            reenabled.Plan!.Fragments,
            fragment => fragment.Id == HostStaticPromptProvider.SafetyFragmentId);

        // Disabling is allowed and silent: no warning either way.
        Assert.Empty(enabled.Plan.Warnings);
        Assert.Empty(disabled.Plan.Warnings);
        Assert.Empty(reenabled.Plan.Warnings);

        // The plan hash follows the fragment set.
        Assert.NotEqual(enabled.PlanHash, disabled.PlanHash);
        Assert.Equal(enabled.PlanHash, reenabled.PlanHash);
        Assert.DoesNotContain(
            "untrusted data",
            disabled.SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Default_profile_output_matches_the_reviewed_baseline_byte_for_byte()
    {
        var bundle = await new HostStaticPromptProvider().GetAsync(Request(Profile()));

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundle.SystemPrompt))).ToLowerInvariant();

        Assert.Equal(134, bundle.SystemPrompt.Length);
        Assert.Equal(Task29BaselineSha256, digest);
    }

    [Fact]
    public async Task Two_plugins_with_the_same_local_fragment_id_do_not_collide()
    {
        var catalog = new ContextComponentCatalog();
        catalog.RegisterFragmentProvider("alpha#prompt", new StaticProvider("shared", "alpha text"));
        catalog.RegisterFragmentProvider("beta#prompt", new StaticProvider("shared", "beta text"));
        var source = new HostPromptFragmentSource(catalog);

        var result = await source.GetFragmentsAsync(Context());

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Fragments.Count);
        Assert.Equal(
            ["alpha#shared", "beta#shared"],
            result.Fragments.Select(fragment => fragment.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, result.ProviderCount);
    }

    [Fact]
    public async Task One_provider_over_quota_does_not_starve_the_other_plugin()
    {
        var catalog = new ContextComponentCatalog();
        catalog.RegisterFragmentProvider("greedy#prompt", new ManyFragmentsProvider(20));
        catalog.RegisterFragmentProvider("polite#prompt", new StaticProvider("kept", "polite text"));
        var source = new HostPromptFragmentSource(catalog);

        var result = await source.GetFragmentsAsync(Context());

        var kept = result.Fragments.Where(fragment => fragment.Id.StartsWith("polite#", StringComparison.Ordinal)).ToArray();
        Assert.Single(kept);
        Assert.Equal("polite text", kept[0].Text);

        var greedy = result.Fragments.Where(fragment => fragment.Id.StartsWith("greedy#", StringComparison.Ordinal)).ToArray();
        Assert.True(
            greedy.Length <= PromptCompilerDefaults.PluginFragmentsPerProvider,
            $"greedy provider kept {greedy.Length} fragments");
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains(PromptCompilerCodes.PluginFragmentProviderLimit, StringComparison.Ordinal));
        Assert.True(result.RejectedCount > 0);
    }

    [Fact]
    public async Task A_throwing_or_timing_out_provider_only_loses_its_own_fragments()
    {
        var catalog = new ContextComponentCatalog();
        catalog.RegisterFragmentProvider("broken#prompt", new ThrowingProvider());
        catalog.RegisterFragmentProvider("healthy#prompt", new StaticProvider("kept", "healthy text"));
        var source = new HostPromptFragmentSource(catalog);

        var result = await source.GetFragmentsAsync(Context());

        Assert.Single(result.Fragments);
        Assert.Equal("healthy#kept", result.Fragments[0].Id);
        Assert.NotEmpty(result.Warnings);
    }

    // ---------------------------------------------------------------- fixtures

    private static PluginStore CreateStore(TempDirectory temp)
        => new(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "android",
            AllowUnsigned = true,
        });

    private static async Task MarkInstalledAsync(PluginStore store, string id)
    {
        Directory.CreateDirectory(store.RootDirectory);
        var state = new PluginStoreState
        {
            SchemaVersion = 1,
            Plugins =
            [
                new InstalledPluginRecord
                {
                    Id = id,
                    Name = id,
                    Version = "1.0.0",
                    ActiveVersion = "1.0.0",
                    Enabled = true,
                    Trust = "unsigned",
                    InstalledAt = DateTimeOffset.UtcNow,
                },
            ],
        };
        await File.WriteAllTextAsync(
            store.StatePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void StorageOf(PluginStore store, string id, out string dataDirectory, out string configPath)
    {
        dataDirectory = store.GetPluginDataDirectory(id);
        configPath = store.GetPluginConfigPath(id);
    }

    private static RuntimeProfile Profile(
        IReadOnlyList<string>? disabled = null,
        string? systemPrompt = "You are Natter, a concise assistant.")
        => new()
        {
            Id = "tier1-default",
            Prompt = new ProfilePrompt
            {
                Provider = "host#static",
                SystemPrompt = systemPrompt,
                DisabledFragments = disabled is null ? [] : [.. disabled],
                Version = "default",
            },
        };

    private static PromptRequest Request(RuntimeProfile profile)
        => new(
            profile,
            Source.Create("discord", "discord", "bot"),
            new Conversation("c1", ConversationKind.Direct),
            new User("u1", "Alice"),
            Locale: null,
            Variables: new Dictionary<string, string>());

    private static PromptFragmentContext Context()
        => new(
            Profile(),
            PromptCompilerDefaults.StaticPresetId,
            null,
            "discord",
            "discord",
            ConversationKind.Direct,
            new Dictionary<string, string>());

    private static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Natter.slnx not found");
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

    private static (string? HttpProxy, string? HttpsProxy, string? AllProxy, string? NoProxy) SnapshotProxyEnvironment()
        => (Environment.GetEnvironmentVariable("HTTP_PROXY"),
            Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            Environment.GetEnvironmentVariable("ALL_PROXY"),
            Environment.GetEnvironmentVariable("NO_PROXY"));

    private static void SetProxyEnvironment(string proxyUrl)
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl);
        Environment.SetEnvironmentVariable("ALL_PROXY", proxyUrl);
        Environment.SetEnvironmentVariable("NO_PROXY", null);
        Environment.SetEnvironmentVariable("http_proxy", proxyUrl);
        Environment.SetEnvironmentVariable("https_proxy", proxyUrl);
        Environment.SetEnvironmentVariable("all_proxy", proxyUrl);
        Environment.SetEnvironmentVariable("no_proxy", null);
    }

    private static void RestoreProxyEnvironment((string? HttpProxy, string? HttpsProxy, string? AllProxy, string? NoProxy) snapshot)
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", snapshot.HttpProxy);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", snapshot.HttpsProxy);
        Environment.SetEnvironmentVariable("ALL_PROXY", snapshot.AllProxy);
        Environment.SetEnvironmentVariable("NO_PROXY", snapshot.NoProxy);
        Environment.SetEnvironmentVariable("http_proxy", snapshot.HttpProxy);
        Environment.SetEnvironmentVariable("https_proxy", snapshot.HttpsProxy);
        Environment.SetEnvironmentVariable("all_proxy", snapshot.AllProxy);
        Environment.SetEnvironmentVariable("no_proxy", snapshot.NoProxy);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-review-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Loopback TCP listener that records how many connections it accepted.</summary>
    private sealed class CountingListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private int _accepted;
        private volatile bool _stopped;

        public CountingListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; }

        public string EndPoint => $"http://127.0.0.1:{Port}";

        public int AcceptedConnections => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            while (!_stopped)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _accepted);
                    client.Dispose();
                }
                catch (Exception) when (_stopped)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            _stopped = true;
            try
            {
                _listener.Stop();
            }
            catch
            {
            }

            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }
    }

    private sealed class StaticProvider(string id, string text) : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PromptFragment>>(
                [new PromptFragment(id, text, PromptFragmentSource.Plugin, 10)]);
    }

    private sealed class ManyFragmentsProvider(int count) : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<PromptFragment>>(
                Enumerable.Range(0, count)
                    .Select(index => new PromptFragment(
                        "flood" + index.ToString("00"),
                        new string('x', 100),
                        PromptFragmentSource.Plugin,
                        10 + index))
                    .ToArray());
    }

    private sealed class ThrowingProvider : IPromptFragmentProvider
    {
        public ValueTask<IReadOnlyList<PromptFragment>> GetFragmentsAsync(
            PromptFragmentContext context,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider exploded");
    }
}
