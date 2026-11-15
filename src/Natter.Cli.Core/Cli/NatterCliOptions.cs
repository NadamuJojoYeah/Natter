namespace Natter.Cli.Core;

/// <summary>Global CLI options handled before the composition root is built.</summary>
public sealed class NatterCliOptions
{
    public List<string> PluginDirectories { get; } = [];

    public bool LoadPlugins { get; set; } = true;

    public bool Verbose { get; set; }

    public bool TraceEvents { get; set; }

    public string? ConfigDirectory { get; set; }

    public string? StateDirectory { get; set; }

    public string? ProfileOverride { get; set; }

    /// <summary>Arguments left after global options were removed.</summary>
    public string[] CommandArgs { get; set; } = [];

    /// <summary>
    /// Optional callback invoked after the host and NPS runtime started but before the first
    /// command runs. App-private plugin stores use it to reconcile pending activations.
    /// </summary>
    public Func<IServiceProvider, ValueTask>? OnHostStartedAsync { get; set; }

    /// <summary>Parses global options out of the argument vector, keeping the command arguments.</summary>
    public static NatterCliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new NatterCliOptions();
        var remaining = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--plugin-dir":
                case "--plugin-directory":
                    if (index + 1 < args.Count)
                        options.PluginDirectories.Add(args[++index]);
                    break;

                case "--no-plugins":
                    options.LoadPlugins = false;
                    break;

                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;

                case "--trace-events":
                    options.TraceEvents = true;
                    break;

                case "--config-dir":
                case "--plugin-config-dir":
                    if (index + 1 < args.Count)
                        options.ConfigDirectory = args[++index];
                    break;

                case "--state-dir":
                    if (index + 1 < args.Count)
                        options.StateDirectory = args[++index];
                    break;

                case "--profile":
                    if (index + 1 < args.Count)
                        options.ProfileOverride = args[++index];
                    break;

                default:
                    if (argument.StartsWith("--plugin-dir=", StringComparison.Ordinal))
                        options.PluginDirectories.Add(argument["--plugin-dir=".Length..]);
                    else if (argument.StartsWith("--config-dir=", StringComparison.Ordinal))
                        options.ConfigDirectory = argument["--config-dir=".Length..];
                    else if (argument.StartsWith("--plugin-config-dir=", StringComparison.Ordinal))
                        options.ConfigDirectory = argument["--plugin-config-dir=".Length..];
                    else if (argument.StartsWith("--state-dir=", StringComparison.Ordinal))
                        options.StateDirectory = argument["--state-dir=".Length..];
                    else if (argument.StartsWith("--profile=", StringComparison.Ordinal))
                        options.ProfileOverride = argument["--profile=".Length..];
                    else
                        remaining.Add(argument);
                    break;
            }
        }

        options.CommandArgs = remaining.ToArray();
        return options;
    }

    /// <summary>
    /// Adds the conventional plugin locations: NATTER_PLUGIN_DIR and
    /// &lt;app base directory&gt;/plugins. Explicit --plugin-dir values are preserved.
    /// </summary>
    public NatterCliOptions AddDefaultPluginLocations()
    {
        if (!LoadPlugins)
            return this;

        ConfigDirectory ??= Environment.GetEnvironmentVariable("NATTER_CONFIG_DIR");
        StateDirectory ??= Environment.GetEnvironmentVariable("NATTER_STATE_DIR");
        ProfileOverride ??= Environment.GetEnvironmentVariable("NATTER_PROFILE");
        ConfigDirectory ??= Path.Combine(AppContext.BaseDirectory, "config");

        var fromEnvironment = Environment.GetEnvironmentVariable("NATTER_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            PluginDirectories.Add(fromEnvironment);

        var local = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(local))
            PluginDirectories.Add(local);

        return this;
    }
}
