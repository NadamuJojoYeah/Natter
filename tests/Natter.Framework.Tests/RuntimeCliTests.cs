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
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("profiles").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("conversations").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("providers").GetArrayLength());
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
        Assert.Contains("Tier 1", console.ErrorText);
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
