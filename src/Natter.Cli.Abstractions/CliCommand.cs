namespace Natter.Cli.Abstractions;

/// <summary>Well-known process exit codes used by the CLI kernel.</summary>
public static class CliExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Cancelled = 130;
}

/// <summary>Describes a command line option (name without the leading dashes).</summary>
public sealed record CliOptionDescriptor(
    string Name,
    string? Description = null,
    bool TakesValue = false,
    string? DefaultValue = null,
    string? ShortName = null);

/// <summary>Describes a positional argument.</summary>
public sealed record CliArgumentDescriptor(
    string Name,
    string? Description = null,
    bool Required = false);

/// <summary>
/// Command metadata + handler factory. Plugins contribute instances through
/// <see cref="ICliCommandContributor"/>; the host only merges them.
/// </summary>
public sealed class CliCommandDescriptor
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Usage { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<CliOptionDescriptor> Options { get; init; } = [];

    public IReadOnlyList<CliArgumentDescriptor> Arguments { get; init; } = [];

    public string Origin { get; init; } = "unknown";

    public required Func<IServiceProvider, ICliCommandHandler> HandlerFactory { get; init; }

    public string[] PathTokens => Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public override string ToString() => Name;
}

/// <summary>Executes one parsed command invocation.</summary>
public interface ICliCommandHandler
{
    ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default);
}

/// <summary>Plugins/subsystems implement this to contribute commands to the CLI.</summary>
public interface ICliCommandContributor
{
    IEnumerable<CliCommandDescriptor> GetCommands();
}

/// <summary>Everything a command handler needs at runtime.</summary>
public sealed class CliInvocation
{
    private readonly IReadOnlyDictionary<string, string?> _options;

    public CliInvocation(
        string commandName,
        IReadOnlyDictionary<string, string?> options,
        IReadOnlyList<string> positionals,
        IServiceProvider services,
        IConsole console)
    {
        CommandName = commandName;
        _options = options;
        Positionals = positionals;
        Services = services;
        Console = console;
    }

    public string CommandName { get; }

    public IReadOnlyList<string> Positionals { get; }

    public IServiceProvider Services { get; }

    public IConsole Console { get; }

    public IReadOnlyDictionary<string, string?> Options => _options;

    public bool HasFlag(string name)
        => _options.TryGetValue(name, out var value)
           && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public string? GetOption(string name)
        => _options.TryGetValue(name, out var value) ? value : null;

    public string GetOption(string name, string fallback)
        => GetOption(name) ?? fallback;

    public int GetIntOption(string name, int fallback)
        => int.TryParse(GetOption(name), out var value) ? value : fallback;

    public double GetDoubleOption(string name, double fallback)
        => double.TryParse(GetOption(name), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public string? GetPositional(int index)
        => index >= 0 && index < Positionals.Count ? Positionals[index] : null;

    public string GetPositional(int index, string fallback)
        => GetPositional(index) ?? fallback;
}
