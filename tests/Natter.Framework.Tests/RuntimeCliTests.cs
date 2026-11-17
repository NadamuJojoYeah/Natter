using System.Text.Json;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class RuntimeCliTests
{
    [Fact]
    public async Task Health_json_reports_profiles_and_host_store()
    {
        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, new NatterCliOptions { LoadPlugins = false });

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal("unknown", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("profiles").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("conversations").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("providers").GetArrayLength());
        Assert.True(document.RootElement.GetProperty("uptimeSeconds").GetInt64() > 0);
        Assert.NotEqual(
            default,
            document.RootElement.GetProperty("startedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Offline_health_does_not_start_upstreams()
    {
        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(
            ["health", "--offline", "--json"],
            console,
            new NatterCliOptions { LoadPlugins = false });
        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal("offline", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("upstreams").GetArrayLength());
    }

    [Fact]
    public async Task Health_uses_config_and_state_directories()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "config");
        var state = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "only",
              "profiles": [
                { "id": "only", "supportTier": 2, "match": {}, "ai": { "provider": "p#r" } }
              ]
            }
            """);

        var console = new StringConsole();
        var options = new NatterCliOptions
        {
            LoadPlugins = false,
            ConfigDirectory = config,
            StateDirectory = state,
        };
        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, options);

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal(1, document.RootElement.GetProperty("profiles").GetInt32());
        Assert.Single(document.RootElement.GetProperty("providers").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(state, "natter.db")));
    }

    [Fact]
    public async Task Missing_tier1_provider_fails_startup()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "t1",
              "profiles": [
                { "id": "t1", "supportTier": 1, "match": {}, "ai": { "provider": "missing#deepseek" } }
              ]
            }
            """);

        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(
            ["health", "--json"],
            console,
            new NatterCliOptions { LoadPlugins = false, ConfigDirectory = config });

        Assert.Equal(CliExitCodes.Failure, exitCode);
        Assert.Contains("requires provider", console.ErrorText);
    }

    [Fact]
    public async Task Serve_duration_returns_success()
    {
        var console = new StringConsole();
        var exitCode = await NatterCli.RunAsync(
            ["serve", "--duration", "1", "--json"],
            console,
            new NatterCliOptions { LoadPlugins = false });

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(console.Output);
        Assert.Equal("serve", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("profiles").GetInt32());
    }

    [Fact]
    public async Task Remember_and_forget_persist_explicit_memory()
    {
        using var temp = new TempDirectory();
        var state = Path.Combine(temp.Path, "state");
        var options = new NatterCliOptions { LoadPlugins = false, StateDirectory = state };
        var console = new StringConsole();
        var remember = await NatterCli.RunAsync(
            ["remember", "likes", "tea", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1", "--json"],
            console,
            options);
        Assert.Equal(CliExitCodes.Success, remember);
        Assert.Contains("mem_", console.Output);

        var list = new StringConsole();
        var listed = await NatterCli.RunAsync(
            ["memory", "list", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1", "--json"],
            list,
            options);
        Assert.Equal(CliExitCodes.Success, listed);
        Assert.Contains("likes tea", list.Output);

        var forget = new StringConsole();
        var deleted = await NatterCli.RunAsync(
            ["forget", "likes tea", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1"],
            forget,
            options);
        Assert.Equal(CliExitCodes.Success, deleted);

        var after = new StringConsole();
        await NatterCli.RunAsync(
            ["memory", "list", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1", "--json"],
            after,
            options);
        Assert.DoesNotContain("likes tea", after.Output);
    }

    [Fact]
    public async Task Memory_list_hides_private_items_in_guild_context()
    {
        using var temp = new TempDirectory();
        var options = new NatterCliOptions { LoadPlugins = false, StateDirectory = Path.Combine(temp.Path, "state") };
        var remember = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["remember", "private note", "--sensitivity", "private", "--source", "discord:discord:bot",
             "--actor", "u1", "--conversation", "c1"], remember, options));

        var dm = new StringConsole();
        await NatterCli.RunAsync(
            ["memory", "list", "--json", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1"],
            dm, options);
        Assert.Contains("private note", dm.Output);

        var guild = new StringConsole();
        await NatterCli.RunAsync(
            ["memory", "list", "--json", "--source", "discord:discord:bot", "--actor", "u1",
             "--conversation", "c1", "--guild", "g1"], guild, options);
        Assert.DoesNotContain("private note", guild.Output);
    }

    [Fact]
    public async Task Cli_memory_follows_the_profile_selected_memory_store()
    {
        using var temp = new TempDirectory();
        var config = Path.Combine(temp.Path, "config");
        var state = Path.Combine(temp.Path, "state");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "profiles.json"), """
            {
              "schemaVersion": 1,
              "defaultProfile": "sample",
              "profiles": [
                {
                  "id": "sample",
                  "supportTier": 2,
                  "match": { "protocols": ["discord"] },
                  "ai": { "provider": "missing#provider" },
                  "memory": { "store": "natter.sample.memory#file" }
                }
              ]
            }
            """);
        var samplePath = Path.Combine(temp.Path, "sample-memory.json");
        var plugin = new Natter.Sample.Plugin.Memory.MemoryStorePlugin(samplePath);
        var options = new NatterCliOptions
        {
            LoadPlugins = false,
            ConfigDirectory = config,
            StateDirectory = state,
        };
        var remember = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["remember", "sample profile memory", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1"],
            remember,
            options,
            builder => builder.AddEmbeddedNpsPlugin(
                Natter.Sample.Plugin.Memory.MemoryStorePlugin.Manifest,
                plugin)));

        var list = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["memory", "list", "--json", "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1"],
            list,
            options,
            builder => builder.AddEmbeddedNpsPlugin(
                Natter.Sample.Plugin.Memory.MemoryStorePlugin.Manifest,
                plugin)));
        Assert.Contains("sample profile memory", list.Output);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(state, "natter.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM memory_items;";
        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task Memory_export_import_roundtrip_is_scoped_and_private_file()
    {
        using var temp = new TempDirectory();
        var stateA = Path.Combine(temp.Path, "state-a");
        var stateB = Path.Combine(temp.Path, "state-b");
        var file = Path.Combine(temp.Path, "memory.json");
        var optionsA = new NatterCliOptions { LoadPlugins = false, StateDirectory = stateA };
        var optionsB = new NatterCliOptions { LoadPlugins = false, StateDirectory = stateB };
        var common = new[]
        {
            "--source", "discord:discord:bot", "--actor", "u1", "--conversation", "c1", "--guild", "g1",
        };

        var remember = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["remember", "likes oat milk", "--scope", "user", .. common], remember, optionsA));

        var export = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["memory", "export", "--output", file, .. common], export, optionsA));
        Assert.True(File.Exists(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var import = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["memory", "import", "--file", file, "--json", .. common], import, optionsB));
        Assert.Contains("\"imported\":1", import.Output);

        var list = new StringConsole();
        Assert.Equal(CliExitCodes.Success, await NatterCli.RunAsync(
            ["memory", "list", "--json", .. common], list, optionsB));
        Assert.Contains("likes oat milk", list.Output);

        var mismatch = new StringConsole();
        Assert.Equal(CliExitCodes.Failure, await NatterCli.RunAsync(
            ["memory", "import", "--file", file, "--source", "discord:discord:bot", "--actor", "other", "--conversation", "c1"],
            mismatch,
            optionsB));
        Assert.Contains("do not belong", mismatch.ErrorText);
    }

    [Fact]
    public async Task Serve_health_port_answers_loopback_health()
    {
        var port = FindFreePort();
        var console = new StringConsole();
        var run = NatterCli.RunAsync(
            ["serve", "--duration", "3", "--health-port", port.ToString(), "--json"],
            console,
            new NatterCliOptions { LoadPlugins = false });

        using var http = new HttpClient();
        string? body = null;
        for (var attempt = 0; attempt < 40 && body is null; attempt++)
        {
            try
            {
                body = await http.GetStringAsync($"http://127.0.0.1:{port}/health");
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }

        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("unknown", document.RootElement.GetProperty("status").GetString());
        var exitCode = await run;
        Assert.True(exitCode == CliExitCodes.Success, console.ErrorText + console.Output);
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StringConsole : IConsole
    {
        public StringWriter OutWriter { get; } = new();
        public StringWriter ErrorWriter { get; } = new();
        public TextWriter Out => OutWriter;
        public TextWriter Error => ErrorWriter;
        public bool IsInteractive => false;
        public string? ReadLine() => null;
        public string Output => OutWriter.ToString();
        public string ErrorText => ErrorWriter.ToString();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
