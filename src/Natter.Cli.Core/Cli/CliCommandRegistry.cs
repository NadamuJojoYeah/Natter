using Microsoft.Extensions.Logging;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Command catalog assembled from built-in commands and plugin contributors.</summary>
public sealed class CliCommandRegistry
{
    private readonly Dictionary<string, CliCommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CliCommandDescriptor> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CliCommandRegistry> _logger;

    public CliCommandRegistry(
        IEnumerable<CliCommandDescriptor> descriptors,
        IEnumerable<ICliCommandContributor> contributors,
        ILogger<CliCommandRegistry> logger)
    {
        _logger = logger;

        foreach (var descriptor in descriptors)
            Register(descriptor);

        foreach (var contributor in contributors)
        {
            try
            {
                foreach (var descriptor in contributor.GetCommands())
                    Register(descriptor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command contributor {Contributor} failed.", contributor.GetType().FullName);
            }
        }
    }

    public IReadOnlyList<CliCommandDescriptor> Commands
        => _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public int CommandCount => _commands.Count;

    public int MaxDepth => _commands.Count == 0
        ? 1
        : _commands.Keys.Max(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    public void Register(CliCommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("Command name must not be empty.", nameof(descriptor));

        if (_commands.TryGetValue(descriptor.Name, out var existing))
        {
            _logger.LogDebug(
                "Command '{Command}' from '{NewOrigin}' overrides '{OldOrigin}'.",
                descriptor.Name,
                descriptor.Origin,
                existing.Origin);

            foreach (var staleAlias in existing.Aliases)
                _aliases.Remove(staleAlias);
        }

        _commands[descriptor.Name] = descriptor;

        foreach (var alias in descriptor.Aliases)
            _aliases[alias] = descriptor;
    }

    public bool TryGet(string name, out CliCommandDescriptor command)
    {
        if (_commands.TryGetValue(name, out var found) || _aliases.TryGetValue(name, out found))
        {
            command = found;
            return true;
        }

        command = null!;
        return false;
    }

    /// <summary>Resolves the longest matching command path ("plugins list" before "plugins").</summary>
    public bool TryResolve(IReadOnlyList<string> args, out CliCommandDescriptor command, out int consumed)
    {
        for (var depth = Math.Min(MaxDepth, args.Count); depth >= 1; depth--)
        {
            var candidate = string.Join(' ', args.Take(depth));
            if (_commands.TryGetValue(candidate, out var found) || _aliases.TryGetValue(candidate, out found))
            {
                command = found;
                consumed = depth;
                return true;
            }
        }

        command = null!;
        consumed = 0;
        return false;
    }
}
