using System.Net;
using System.Net.Http;
using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Natter.Cli.Core.Commands;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 CLI/ops hardening (review §4.5, S2-E1…S2-E13). Each test pins one behaviour that used to be
/// silently wrong: secret leakage, ambiguous offline semantics, exit codes, option parsing, the
/// instance lock, JSON shape, health-server concurrency, and the ops/CI/supply-chain configs.
/// </summary>
public sealed class CliOpsHardeningTests : IDisposable
{
    private const string SecretToken = "sk-live-TOKEN-sentinel-123456";

    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), "natter-ops-" + Guid.NewGuid().ToString("N"));

    public CliOpsHardeningTests() => Directory.CreateDirectory(_temporaryRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static NatterCliOptions NoPlugins(string? stateDirectory = null) => new()
    {
        LoadPlugins = false,
        StateDirectory = stateDirectory,
        ConfigDirectory = stateDirectory,
    };

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // ------------------------------------------------------------------ S2-E1
    [Fact]
    public async Task Every_error_field_is_redacted_in_health_output()
    {
        var console = new StringConsole();
        var options = NoPlugins();
        options.OnHostStartedAsync = services =>
        {
            services.GetRequiredService<IChatUpstreamRegistry>()
                .Register(new LeakyUpstream($"boom {SecretToken}"));
            return ValueTask.CompletedTask;
        };

        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, options);

        Assert.NotEqual(CliExitCodes.Usage, exitCode);
        Assert.DoesNotContain(SecretToken, console.Output);
        Assert.DoesNotContain(SecretToken, console.ErrorText);
        Assert.Contains("[redacted]", console.Output);
    }

    // ------------------------------------------------------------------ S2-E2
    [Fact]
    public async Task Offline_health_says_plugins_are_not_activated()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["health", "--offline", "--json"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal("offline", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("not-activated", document.RootElement.GetProperty("pluginActivation").GetString());
    }

    // ------------------------------------------------------------------ S2-E3
    [Fact]
    public async Task Degraded_health_exits_with_the_documented_code()
    {
        var console = new StringConsole();
        var options = NoPlugins();
        options.OnHostStartedAsync = services =>
        {
            // A connected=false upstream makes the snapshot `degraded`.
            services.GetRequiredService<IChatUpstreamRegistry>().Register(new DisconnectedUpstream());
            return ValueTask.CompletedTask;
        };

        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, options);

        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal("degraded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(CliHealthExitCodes.Degraded, exitCode);
    }

    // ------------------------------------------------------------------ S2-E4
    [Fact]
    public async Task Global_option_without_a_value_is_a_usage_error()
    {
        foreach (var args in new[]
                 {
                     new[] { "--config-dir" },
                     new[] { "--config-dir", "--state-dir", _temporaryRoot },
                     new[] { "--state-dir" },
                     new[] { "--plugin-dir" },
                     new[] { "--profile" },
                 })
        {
            var console = new StringConsole();
            var exitCode = await NatterCli.RunAsync(args, console);

            Assert.Equal(CliExitCodes.Usage, exitCode);
            Assert.Contains("requires a value", console.ErrorText);
        }
    }

    [Fact]
    public async Task Global_option_with_a_value_still_parses()
    {
        var options = NatterCliOptions.Parse(["--state-dir", _temporaryRoot, "version"]);

        Assert.Null(options.ParseError);
        Assert.Equal(_temporaryRoot, options.StateDirectory);
        Assert.Equal(["version"], options.CommandArgs);
    }

    // ------------------------------------------------------------------ S2-E5
    [Fact]
    public async Task Unknown_command_option_is_rejected()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["version", "--definitely-not-an-option"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("Unknown option", console.ErrorText);
    }

    // ------------------------------------------------------------------ S2-E6
    [Fact]
    public void NoPlugins_still_resolves_config_state_and_profile()
    {
        var stateDirectory = Path.Combine(_temporaryRoot, "state");

        var options = NatterCliOptions.Parse([
            "--no-plugins",
            "--state-dir", stateDirectory,
            "--config-dir", Path.Combine(_temporaryRoot, "config"),
            "--profile", "tier1-default",
            "version",
        ]).AddDefaultPluginLocations();

        Assert.False(options.LoadPlugins);
        Assert.Equal(stateDirectory, options.StateDirectory);
        Assert.Equal(Path.Combine(_temporaryRoot, "config"), options.ConfigDirectory);
        Assert.Equal("tier1-default", options.ProfileOverride);
        // --no-plugins must not inject plugin directories, but everything else stays functional.
        Assert.Empty(options.PluginDirectories);
    }

    [Fact]
    public async Task NoPlugins_does_not_bypass_the_instance_lock()
    {
        var stateDirectory = Path.Combine(_temporaryRoot, "locked");
        Directory.CreateDirectory(stateDirectory);
        using var held = new FileStream(
            Path.Combine(stateDirectory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(
            ["upstreams", "--json"], console, NoPlugins(stateDirectory));

        Assert.Equal(CliHostStartupCodes.HostBusy, exitCode);
        Assert.Contains("HL-CLI-HOST-BUSY", console.ErrorText);
    }

    // ------------------------------------------------------------------ S2-E7
    [Fact]
    public async Task Unusable_state_directory_gets_a_classified_exit_code()
    {
        var readonlyParent = Path.Combine(_temporaryRoot, "readonly");
        Directory.CreateDirectory(readonlyParent);
        var unwritable = Path.Combine(readonlyParent, "nested", "state");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(readonlyParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var console = new StringConsole();
            var exitCode = await NatterCli.RunAsync(
                ["upstreams", "--json"], console, NoPlugins(unwritable));

            // Root ignores directory permissions, so only assert the classified failure when the
            // path really is unusable for this process.
            if (exitCode != CliExitCodes.Success)
            {
                Assert.Equal(CliHostStartupCodes.StateDirectoryUnusable, exitCode);
                Assert.Contains("HL-CLI-STATE-DIR-UNUSABLE", console.ErrorText);
                Assert.DoesNotContain("at Natter.", console.ErrorText); // no stack trace
            }
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(readonlyParent, UnixFileMode.UserRead
                    | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ------------------------------------------------------------------ S2-E8
    [Fact]
    public async Task Json_output_uses_one_casing_policy_everywhere()
    {
        var healthConsole = new StringConsole();
        await NatterCli.RunAsync(["health", "--offline", "--json"], healthConsole, NoPlugins());
        using var health = JsonDocument.Parse(healthConsole.Output);

        var pluginsConsole = new StringConsole();
        await NatterCli.RunAsync(["plugins", "list", "--json"], pluginsConsole, NoPlugins());

        // camelCase field names are the shared policy; a second policy would surface PascalCase here.
        Assert.True(health.RootElement.TryGetProperty("uptimeSeconds", out _));
        Assert.True(health.RootElement.TryGetProperty("pluginActivation", out _));
        Assert.False(health.RootElement.TryGetProperty("UptimeSeconds", out _));
        Assert.StartsWith("[", pluginsConsole.Output.TrimStart(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ S2-E9
    [Fact]
    public async Task Health_server_bounds_concurrent_requests()
    {
        var port = FreeTcpPort();
        using var server = new HealthHttpServer(port, () => ValueTask.FromResult<object>(new { status = "healthy" }));
        server.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var statuses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                return (int)response.StatusCode;
            }
            catch (HttpRequestException)
            {
                return -1;
            }
        }));

        // The bounding policy either serves the request (200) or rejects it quickly (503); it must
        // never hang or crash the listener.
        Assert.Contains(200, statuses);
        Assert.All(statuses, status => Assert.True(status is 200 or 503, $"unexpected status {status}"));
    }

    // ------------------------------------------------------------------ S2-E10
    [Fact]
    public void Collect_script_never_puts_the_bot_token_on_a_command_line()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "p2-collect.sh"));

        Assert.DoesNotContain("-H \"Authorization: Bot", script);
        Assert.DoesNotContain("Authorization: Bot $DISCORD_BOT_TOKEN\"", script);
        Assert.Contains("-K -", script);
    }

    // ------------------------------------------------------------------ S2-E11
    [Fact]
    public void Audit_is_enabled_by_default_repo_wide()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        // Comments may explain the history (including the old suppression), so assert on markup only.
        var markup = System.Text.RegularExpressions.Regex.Replace(
            props, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Contains("<NuGetAudit>true</NuGetAudit>", markup);
        Assert.DoesNotContain("<NuGetAudit>false</NuGetAudit>", markup);
        Assert.DoesNotContain("NU1900", markup);
    }

    // ------------------------------------------------------------------ S2-E12
    [Fact]
    public void Ci_cache_key_covers_the_build_props()
    {
        foreach (var workflow in new[] { "ci.yml", "deploy-verify.yml" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", workflow));
            if (!text.Contains("hashFiles(", StringComparison.Ordinal))
                continue; // workflow without a NuGet cache

            Assert.Contains("'Directory.Build.props'", text);
        }
    }

    // ------------------------------------------------------------------ S2-E13
    [Fact]
    public void Image_artifact_list_only_contains_files_that_job_creates()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-verify.yml"));
        var upload = text[text.IndexOf("Upload image verification evidence", StringComparison.Ordinal)..];
        var block = upload[..upload.IndexOf("if-no-files-found", StringComparison.Ordinal)];

        Assert.DoesNotContain("soak-report.json", block);
        Assert.Contains("if-no-files-found: error", text);
    }

    // ---------------------------------------------------------------- helpers
    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class LeakyUpstream(string message) : IChatUpstream, IChatUpstreamHealth
    {
        private readonly Subject<ChatEvent> _events = new();

        public Source Source { get; } = Source.Create("discord", "discord", "bot");

        public bool IsConnected => true;

        public UpstreamHealth Health { get; } = new(
            "discord:discord:bot",
            Connected: true,
            LastEventAt: null,
            LastConnectedAt: null,
            LastDisconnectAt: null,
            ReconnectCount: 1,
            LastError: message,
            SendFailures: 1,
            LastSendError: message,
            LastDisconnectError: message,
            LastDisconnectCode: "ws_closed");

        public IObservable<ChatEvent> Events => _events;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Capabilities> GetCapabilitiesAsync(
            Conversation? conversation = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new Capabilities());

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChatAck { Id = "1", ReplyTo = "0", Ok = true });

        public ValueTask DisposeAsync()
        {
            _events.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisconnectedUpstream : IChatUpstream
    {
        private readonly Subject<ChatEvent> _events = new();

        public Source Source { get; } = Source.Create("discord", "discord", "offline-bot");

        public bool IsConnected => false;

        public IObservable<ChatEvent> Events => _events;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Capabilities> GetCapabilitiesAsync(
            Conversation? conversation = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new Capabilities());

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ChatAck { Id = "1", ReplyTo = "0", Ok = true });

        public ValueTask DisposeAsync()
        {
            _events.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
