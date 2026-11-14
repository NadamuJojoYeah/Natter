namespace Natter.Cli.Core;

/// <summary>Global CLI options handled before the composition root is built.</summary>
public sealed class NatterCliOptions
{
    public List<string> PluginDirectories { get; } = [];

    public bool LoadPlugins { get; set; } = true;

    public bool Verbose { get; set; }

    public bool TraceEvents { get; set; }

    /// <summary>Arguments left after global options were removed.</summary>
    public string[] CommandArgs { get; set; } = [];

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

                default:
                    if (argument.StartsWith("--plugin-dir=", StringComparison.Ordinal))
                        options.PluginDirectories.Add(argument["--plugin-dir=".Length..]);
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

        var fromEnvironment = Environment.GetEnvironmentVariable("NATTER_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            PluginDirectories.Add(fromEnvironment);

        var local = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(local))
            PluginDirectories.Add(local);

        return this;
    }
}
