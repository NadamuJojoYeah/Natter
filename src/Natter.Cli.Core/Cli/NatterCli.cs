using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Logging;
using Natter.Infrastructure.Composition;

namespace Natter.Cli.Core;

/// <summary>
/// Unified cross-platform CLI entry point. The console host and the Android head both
/// call this method; they only differ in the <see cref="IConsole"/> implementation and
/// the modules they add through <paramref name="configure"/>.
/// </summary>
public static class NatterCli
{
    /// <summary>Parses global options from <paramref name="args"/> and runs the CLI.</summary>
    public static Task<int> RunAsync(
        string[] args,
        IConsole console,
        Action<NatterApplicationBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var options = NatterCliOptions.Parse(args);
        return RunAsync(options.CommandArgs, console, options, configure, cancellationToken);
    }

    /// <summary>Runs the CLI with explicit host options (used by embedded hosts).</summary>
    public static async Task<int> RunAsync(
        string[] args,
        IConsole console,
        NatterCliOptions options,
        Action<NatterApplicationBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(options);

        options.AddDefaultPluginLocations();

        var durationSeconds = ReadServeDuration(args);
        using var commandBudget = durationSeconds > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        commandBudget?.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
        var runToken = commandBudget?.Token ?? cancellationToken;

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<CliCoreCompositionModule>()
            .ConfigureServices(services => services.AddSingleton(options));

        if (!options.LoadPlugins)
        {
            builder.ConfigureOptions(composition => composition.EnablePluginLoading = false);
        }
        else
        {
            foreach (var directory in options.PluginDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
                builder.AddPluginDirectory(directory);
        }

        if (!string.IsNullOrWhiteSpace(options.ConfigDirectory)
            || !string.IsNullOrWhiteSpace(options.StateDirectory)
            || !string.IsNullOrWhiteSpace(options.ProfileOverride))
        {
            builder.ConfigureOptions(composition =>
            {
                composition.PluginConfigDirectory = options.ConfigDirectory ?? composition.PluginConfigDirectory;
                composition.StateDirectory = options.StateDirectory ?? composition.StateDirectory;
                composition.ProfileOverride = options.ProfileOverride ?? composition.ProfileOverride;
            });
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
            logging.AddProvider(new CliLoggerProvider(console));
        });

        configure?.Invoke(builder);

        await using var host = builder.Build();

        // S1-11: whether to start a host is a property of the command, not of the CLI entry point.
        // Building the graph (above) only scans plugin metadata; it never connects a gateway.
        // Do NOT resolve CliCommandRegistry here: it is a singleton built from the plugin command
        // contributors, so touching it before activation would freeze an empty plugin command list.
        var hostRequirements = host.Services.GetService<CliHostRequirementCatalog>()
                               ?? new CliHostRequirementCatalog();
        var noHostRequested = options.NoHost
                              || args.Contains(CliHostStartupPlanner.NoHostFlag, StringComparer.Ordinal);
        var argsForPlanning = args
            .Where(argument => !string.Equals(argument, CliHostStartupPlanner.NoHostFlag, StringComparison.Ordinal))
            .ToArray();
        var plan = CliHostStartupPlanner.Plan(
            argsForPlanning.Length > 0 ? argsForPlanning : args,
            hostRequirements,
            options,
            noHostRequested);

        // Commands that need runtime state take the same per-state-directory lock as `serve`, so a
        // second host can never race the running one for the bot session.
        FileStream? instanceLock = null;
        if (plan.TakeInstanceLock)
        {
            instanceLock = StateDirectoryLock.TryAcquire(options, console, quiet: true);
            if (instanceLock is null && !string.IsNullOrWhiteSpace(options.StateDirectory))
            {
                CliHostStartupPlanner.WriteHostBusyMessage(
                    console, plan, Path.GetFullPath(options.StateDirectory));
                return CliHostStartupCodes.HostBusy;
            }
        }
        using var instanceLockScope = instanceLock;

        if (plan.Note is not null)
            console.Error.WriteLine(plan.Note);

        if (plan.StartHost)
        {
            try
            {
                await host.StartAsync(runToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                if (durationSeconds > 0 && !cancellationToken.IsCancellationRequested)
                {
                    console.Error.WriteLine("Serve duration reached during host startup; canceled.");
                    return CliExitCodes.Success;
                }

                console.Error.WriteLine("Host startup canceled.");
                return CliExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                console.Error.WriteLine($"Host startup failed: {ex.Message}");
                return CliExitCodes.Failure;
            }

            if (options.OnHostStartedAsync is not null)
                await options.OnHostStartedAsync(host.Services).ConfigureAwait(false);
        }

        // `--no-host` is a global switch, not a command option: strip it before the command parser
        // sees it, otherwise every runtime command would fail with a usage error instead.
        var commandArgs = args
            .Where(argument => !string.Equals(argument, CliHostStartupPlanner.NoHostFlag, StringComparison.Ordinal))
            .ToArray();
        if (commandArgs.Length == 0)
            commandArgs = args;

        try
        {
            var runner = host.Services.GetRequiredService<CliRunner>();
            return await runner.RunAsync(commandArgs, console, runToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await host.StopAsync(CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                console.Error.WriteLine("Host shutdown exceeded 10 seconds.");
            }
        }
    }
    private static int ReadServeDuration(string[] args)
    {
        if (args.Length == 0
            || !string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
            return 0;

        for (var index = 1; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--duration", StringComparison.Ordinal)
                && int.TryParse(args[index + 1], out var seconds)
                && seconds > 0)
                return seconds;
        }
        return 0;
    }

}
