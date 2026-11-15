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
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            console.Error.WriteLine($"Host startup failed: {ex.Message}");
            return CliExitCodes.Failure;
        }

        if (options.OnHostStartedAsync is not null)
            await options.OnHostStartedAsync(host.Services).ConfigureAwait(false);

        try
        {
            var runner = host.Services.GetRequiredService<CliRunner>();
            return await runner.RunAsync(args, console, cancellationToken).ConfigureAwait(false);
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
}
