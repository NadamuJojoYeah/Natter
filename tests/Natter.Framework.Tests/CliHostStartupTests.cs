using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Cli.Core;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S1-11: only commands that need live runtime state may start a host. A diagnostic command must
/// never open a second Discord gateway session while the service is running, and a command that
/// does need the runtime must be refused (with a stable exit code) instead of racing the service.
/// </summary>
public sealed class CliHostStartupTests : IDisposable
{
    private readonly string _stateDirectory =
        Path.Combine(Path.GetTempPath(), "natter-cli-host-" + Guid.NewGuid().ToString("N"));

    public CliHostStartupTests() => Directory.CreateDirectory(_stateDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory must never fail the suite.
        }
    }

    private NatterCliOptions Options(Action<IServiceProvider>? onHostStarted = null) => new()
    {
        LoadPlugins = false,
        StateDirectory = _stateDirectory,
        ConfigDirectory = _stateDirectory,
        OnHostStartedAsync = onHostStarted is null
            ? null
            : services =>
            {
                onHostStarted(services);
                return ValueTask.CompletedTask;
            },
    };

    /// <summary>Acts as the running `serve`: holds the same exclusive .lock the host takes.</summary>
    private FileStream OccupyInstanceLock()
        => new(Path.Combine(_stateDirectory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public async Task ReadOnly_command_does_not_start_a_host_while_serve_is_running()
    {
        using var serveLock = OccupyInstanceLock();
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["plugins", "list", "--json"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        Assert.Equal(CliExitCodes.Success, exitCode);
        // OnHostStartedAsync only runs after a host is started, so 0 proves no second gateway
        // session (the fake upstream counter never saw a start).
        Assert.Equal(0, hostStarts);
        Assert.DoesNotContain("HL-CLI-HOST-BUSY", console.ErrorText);
    }

    [Fact]
    public async Task ReadOnly_commands_stay_offline_across_the_whole_diagnostic_set()
    {
        using var serveLock = OccupyInstanceLock();
        var hostStarts = 0;
        var options = Options(_ => Interlocked.Increment(ref hostStarts));

        foreach (var args in new[]
                 {
                     new[] { "plugins", "list" },
                     new[] { "doctor", "--json" },
                     new[] { "ports" },
                     new[] { "version" },
                     new[] { "plugins", "store", "list" },
                 })
        {
            var console = new StringConsole();
            var exitCode = await NatterCli.RunAsync(args, console, options);
            Assert.Equal(CliExitCodes.Success, exitCode);
        }

        Assert.Equal(0, hostStarts);
    }

    [Fact]
    public async Task Runtime_command_is_refused_with_a_stable_code_while_serve_is_running()
    {
        using var serveLock = OccupyInstanceLock();
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["upstreams", "--json"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        Assert.Equal(CliHostStartupCodes.HostBusy, exitCode);
        Assert.Equal(0, hostStarts);
        Assert.Contains("HL-CLI-HOST-BUSY", console.ErrorText);
        Assert.Contains("second gateway session", console.ErrorText);
        Assert.Contains("--no-host", console.ErrorText);
    }

    [Fact]
    public async Task Serve_and_health_are_mutually_exclusive()
    {
        using var serveLock = OccupyInstanceLock();
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(["health", "--json"], console, Options());

        Assert.Equal(CliHostStartupCodes.HostBusy, exitCode);
        Assert.Contains("HL-CLI-HOST-BUSY", console.ErrorText);
    }

    [Fact]
    public async Task Runtime_command_starts_the_host_when_the_lock_is_free()
    {
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["upstreams", "--json"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, hostStarts);
    }

    [Fact]
    public async Task NoHost_flag_degrades_the_command_instead_of_silently_returning_nothing()
    {
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["upstreams", "--no-host"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        // The flag is a global switch: it must not be rejected as an unknown command option.
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(0, hostStarts);
        Assert.Contains("running without one because --no-host", console.ErrorText);
    }

    [Fact]
    public async Task Global_no_host_switch_also_degrades_runtime_commands()
    {
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["--no-host", "upstreams", "--json"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(0, hostStarts);
        Assert.Contains("--no-host", console.ErrorText);
    }

    [Fact]
    public async Task Offline_health_never_starts_a_host()
    {
        var hostStarts = 0;
        var console = new StringConsole();

        var exitCode = await NatterCli.RunAsync(
            ["health", "--offline", "--json"],
            console,
            Options(_ => Interlocked.Increment(ref hostStarts)));

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(0, hostStarts);
        Assert.Contains("offline", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_matrix_is_explicit_and_everything_else_defaults_to_offline()
    {
        var catalog = new CliHostRequirementCatalog([new DefaultCliHostRequirements()]);

        var runtime = catalog.RuntimeRules
            .Select(rule => rule.CommandName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            [
                "demo",
                "discord status",
                "events clear",
                "events list",
                "events watch",
                "exec",
                "forget",
                "health",
                "memory export",
                "memory import",
                "memory list",
                "plugins contributions",
                "plugins doctor",
                "plugins graph",
                "plugins permissions",
                "remember",
                "serve",
                "upstreams",
            ],
            runtime);

        foreach (var command in new[]
                 {
                     "plugins list",
                     "plugins inspect",
                     "plugins config validate",
                     "plugins store list",
                     "doctor",
                     "ports",
                     "satori status",
                     "version",
                     "help",
                     "some.future.plugin.command",
                 })
        {
            Assert.Equal(CliHostRequirement.None, catalog.GetRequirement(command));
        }
    }

    [Fact]
    public void Planner_treats_unresolvable_commands_as_plugin_commands()
    {
        var catalog = new CliHostRequirementCatalog([new DefaultCliHostRequirements()]);

        // An unknown command may be contributed by a plugin, and plugin commands only exist after
        // activation — so the host has to start for it (the runner reports "Unknown command" if no
        // plugin provides it).
        var unknown = CliHostStartupPlanner.Plan(["totally-unknown"], catalog, new NatterCliOptions());
        Assert.True(unknown.StartHost);
        Assert.Equal(CliHostRequirement.Runtime, unknown.Requirement);
        Assert.Contains("plugin commands are registered during activation", unknown.Reason);

        // A bare invocation or a global flag never starts anything.
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--help" }, new[] { "-h" } })
        {
            var none = CliHostStartupPlanner.Plan(args, catalog, new NatterCliOptions());
            Assert.False(none.StartHost);
            Assert.Equal(CliHostRequirement.None, none.Requirement);
        }

        var runtime = CliHostStartupPlanner.Plan(["upstreams", "--json"], catalog, new NatterCliOptions());
        Assert.True(runtime.StartHost);
        Assert.True(runtime.TakeInstanceLock);
        Assert.Contains("live connection state", runtime.Reason);

        var degraded = CliHostStartupPlanner.Plan(
            ["upstreams", "--json"], catalog, new NatterCliOptions { NoHost = true });
        Assert.False(degraded.StartHost);
        Assert.True(degraded.Degraded);
        Assert.Contains("--no-host", degraded.Note);
    }

    [Fact]
    public void Plugin_contributed_commands_need_the_runtime_even_without_a_rule()
    {
        var catalog = new CliHostRequirementCatalog([new DefaultCliHostRequirements()]);

        // Built-in command: offline.
        Assert.Equal(
            CliHostRequirement.None,
            catalog.GetRequirement("plugins list", CliHostRequirementCatalog.FrameworkOrigin));

        // Same command name delivered by a plugin origin: it only exists once that plugin activated.
        Assert.Equal(
            CliHostRequirement.Runtime,
            catalog.GetRequirement("plugins list", "natter.plugin.someone"));

        // Declared rules still win for framework commands.
        Assert.Equal(
            CliHostRequirement.Runtime,
            catalog.GetRequirement("upstreams", CliHostRequirementCatalog.FrameworkOrigin));
    }

    [Fact]
    public void Plugin_can_declare_that_its_command_needs_the_runtime()
    {
        var catalog = new CliHostRequirementCatalog(
        [
            new DefaultCliHostRequirements(),
            new StubProvider(new CliHostRule("webconsole serve", CliHostRequirement.Runtime, "serves HTTP")),
        ]);

        Assert.Equal(CliHostRequirement.Runtime, catalog.GetRequirement("webconsole serve"));
        Assert.Equal(CliHostRequirement.None, catalog.GetRequirement("webconsole status"));
    }

    [Fact]
    public void No_host_is_honoured_for_plugin_commands_that_are_not_in_the_catalog_yet()
    {
        // Regression: an unknown command (a plugin command, only registered during activation) used to
        // take the RuntimePlan early return, which skipped the offline branch entirely. The result was
        // that `natter web status --no-host` still took the instance lock and failed with
        // HL-CLI-HOST-BUSY whenever a host was running - exactly the case the flag exists for.
        var catalog = new CliHostRequirementCatalog([new DefaultCliHostRequirements()]);

        var withHost = CliHostStartupPlanner.Plan(["web", "status", "--json"], catalog, new NatterCliOptions());
        Assert.True(withHost.StartHost);
        Assert.True(withHost.TakeInstanceLock);

        // The real entry point strips the global flag before planning (NatterCli.cs), so the planner
        // sees it either in options or mid-argument - never as args[0] (that would be "usage", not a
        // command). Mirror that here instead of handing the planner a shape it never receives.
        foreach (var (argv, label) in new (string[], string)[]
                 {
                     (["web", "status", "--json"], "options.NoHost"),
                     (["web", "status", "--json", "--no-host"], "flag after the command"),
                     (["web", "--no-host", "status", "--json"], "flag between command tokens"),
                 })
        {
            var parsed = NatterCliOptions.Parse(argv);
            var stripped = argv.Where(a => a != CliHostStartupPlanner.NoHostFlag).ToArray();
            var plan = label == "options.NoHost"
                ? CliHostStartupPlanner.Plan(argv, catalog, new NatterCliOptions { NoHost = true })
                : CliHostStartupPlanner.Plan(stripped, catalog, parsed, parsed.NoHost);

            Assert.False(plan.StartHost);
            Assert.False(plan.TakeInstanceLock);
            Assert.True(plan.Degraded);
            Assert.Contains("--no-host", plan.Note);
        }
    }

    private sealed class StubProvider(CliHostRule rule) : ICliHostRequirementProvider
    {
        public IEnumerable<CliHostRule> GetRules() => [rule];
    }
}
