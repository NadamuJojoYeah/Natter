using System.Text.Json;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class CliSmokeTests
{
    [Fact]
    public async Task Version_command_runs_on_the_composed_host()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["version"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Natter framework", console.Output);
        Assert.Contains("runtime", console.Output);
    }

    [Fact]
    public async Task Demo_dispatches_domain_events_through_rx()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["demo", "--name", "test", "--times", "2"],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("counter 'test'", console.Output);
        Assert.Contains("3 domain event(s) dispatched", console.Output);
        Assert.Contains("CounterCreated", console.Output);
        Assert.Contains("CounterIncremented", console.Output);
    }

    [Fact]
    public async Task Doctor_json_is_machine_readable()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["doctor", "--json"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(console.Output);
        var framework = document.RootElement.GetProperty("framework").GetString();
        Assert.False(string.IsNullOrWhiteSpace(framework));
        Assert.True(document.RootElement.GetProperty("commands").GetInt32() > 0);
    }

    [Fact]
    public async Task Exec_keeps_state_inside_one_host()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["exec", "demo --name chained --times 1", "events list --limit 10"],
            console,
            NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("$ natter demo", console.Output);
        Assert.Contains("$ natter events list", console.Output);
        Assert.Contains("CounterIncremented", console.Output);
    }

    [Fact]
    public async Task Unknown_command_returns_usage_exit_code()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["nope"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Contains("Unknown command", console.ErrorText);
    }

    [Fact]
    public async Task Help_lists_plugin_and_built_in_commands()
    {
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["help"], console, NoPlugins());

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("plugins list", console.Output);
        Assert.Contains("demo", console.Output);
        Assert.Contains("--plugin-dir", console.Output);
    }

    private static NatterCliOptions NoPlugins() => new() { LoadPlugins = false };

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
}
