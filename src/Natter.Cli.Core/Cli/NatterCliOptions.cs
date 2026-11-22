namespace Natter.Cli.Core;

/// <summary>Global CLI options handled before the composition root is built.</summary>
public sealed class NatterCliOptions
{
    public List<string> PluginDirectories { get; } = [];

    public bool LoadPlugins { get; set; } = true;

    public bool Verbose { get; set; }

    public bool TraceEvents { get; set; }

    /// <summary>
    /// Global escape hatch: never start a host, even for commands that normally need runtime state.
    /// Commands that depend on live state print a degradation note instead of silently returning
    /// empty data.
    /// </summary>
    public bool NoHost { get; set; }

    public string? ConfigDirectory { get; set; }

    public string? StateDirectory { get; set; }

    public string? ProfileOverride { get; set; }

    /// <summary>Arguments left after global options were removed.</summary>
    public string[] CommandArgs { get; set; } = [];

    /// <summary>
    /// Set when global option parsing fails (S2-E4). The caller must print usage and exit with
    /// <c>CliExitCodes.Usage</c> instead of silently dropping the argument.
    /// </summary>
    public string? ParseError { get; private set; }

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
                    if (!TryTakeValue(args, ref index, argument, out var pluginDirectory, out var pluginError))
                    {
                        options.ParseError = pluginError;
                        return options;
                    }

                    options.PluginDirectories.Add(pluginDirectory!);
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

                case "--no-host":
                    options.NoHost = true;
                    break;

                case "--config-dir":
                case "--plugin-config-dir":
                    if (!TryTakeValue(args, ref index, argument, out var configDirectory, out var configError))
                    {
                        options.ParseError = configError;
                        return options;
                    }

                    options.ConfigDirectory = configDirectory;
                    break;

                case "--state-dir":
                    if (!TryTakeValue(args, ref index, argument, out var stateDirectory, out var stateError))
                    {
                        options.ParseError = stateError;
                        return options;
                    }

                    options.StateDirectory = stateDirectory;
                    break;

                case "--profile":
                    if (!TryTakeValue(args, ref index, argument, out var profile, out var profileError))
                    {
                        options.ParseError = profileError;
                        return options;
                    }

                    options.ProfileOverride = profile;
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
    /// Reads the value that follows a global option. A missing value, or the next token being
    /// another option, is an error: previously `--config-dir --state-dir /x` silently consumed the
    /// following flag and dropped both arguments (S2-E4).
    /// </summary>
    private static bool TryTakeValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;

        if (index + 1 >= args.Count)
        {
            error = $"option '{option}' requires a value.";
            return false;
        }

        var next = args[index + 1];
        if (next.StartsWith('-') && next.Length > 1)
        {
            error = $"option '{option}' requires a value, but '{next}' looks like another option.";
            return false;
        }

        value = next;
        index++;
        return true;
    }

    /// <summary>
    /// Adds the conventional plugin locations: NATTER_PLUGIN_DIR and
    /// &lt;app base directory&gt;/plugins. Explicit --plugin-dir values are preserved.
    /// </summary>
    public NatterCliOptions AddDefaultPluginLocations()
    {
        // S2-E6: `--no-plugins` only governs plugin loading. Configuration, state and profile
        // resolution — and therefore the instance lock and the stores — must stay untouched.
        ConfigDirectory ??= Environment.GetEnvironmentVariable("NATTER_CONFIG_DIR");
        StateDirectory ??= Environment.GetEnvironmentVariable("NATTER_STATE_DIR");
        ProfileOverride ??= Environment.GetEnvironmentVariable("NATTER_PROFILE");
        ConfigDirectory ??= Path.Combine(AppContext.BaseDirectory, "config");

        if (!LoadPlugins)
            return this;

        var fromEnvironment = Environment.GetEnvironmentVariable("NATTER_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            PluginDirectories.Add(fromEnvironment);

        var local = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(local))
            PluginDirectories.Add(local);

        return this;
    }
}
