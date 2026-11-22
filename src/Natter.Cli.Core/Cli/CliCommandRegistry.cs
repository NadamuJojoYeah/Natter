using Microsoft.Extensions.Logging;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Plugins.Nps;

namespace Natter.Cli.Core;

/// <summary>
/// Command catalog assembled from built-in commands and plugin contributors.
///
/// The catalog is a live view: plugins publish and withdraw their commands while the host runs, so
/// every mutation and every read is guarded by one lock. Withdrawal is owner based
/// (<c>plugin:&lt;id&gt;</c>) because a plugin id is the only stable identity across reloads.
/// </summary>
public sealed class CliCommandRegistry : IPluginCliCommandSink
{
    private readonly Dictionary<string, CliCommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CliCommandDescriptor> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _ownerCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICliCommandContributor[] _contributors;
    private readonly object _gate = new();
    private readonly ILogger<CliCommandRegistry> _logger;

    public CliCommandRegistry(
        IEnumerable<CliCommandDescriptor> descriptors,
        IEnumerable<ICliCommandContributor> contributors,
        ILogger<CliCommandRegistry> logger)
    {
        _logger = logger;
        _contributors = contributors.ToArray();

        foreach (var descriptor in descriptors)
            Register(descriptor);

        foreach (var contributor in _contributors)
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
    {
        get
        {
            lock (_gate)
                return _commands.Values.OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public int CommandCount
    {
        get
        {
            lock (_gate)
                return _commands.Count;
        }
    }

    public int MaxDepth
    {
        get
        {
            lock (_gate)
            {
                return _commands.Count == 0
                    ? 1
                    : _commands.Keys.Max(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            }
        }
    }

    public void Register(CliCommandDescriptor descriptor)
    {
        lock (_gate)
            RegisterCore(descriptor);
    }

    /// <summary>
    /// S2/S1-11 follow-up: withdraw every command owned by a plugin. Idempotent — calling it twice,
    /// or for an owner that never registered anything, is a no-op.
    /// </summary>
    public void UnregisterOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
            RemoveOwnerCore(owner);
    }

    /// <inheritdoc />
    public void ReplaceOwnerCommands(string owner, IEnumerable<CliCommandDescriptor> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(commands);

        var incoming = commands.ToArray();
        lock (_gate)
        {
            RemoveOwnerCore(owner);

            if (incoming.Length == 0)
                return;

            var names = new List<string>(incoming.Length);
            foreach (var descriptor in incoming)
            {
                // The owner must not be able to impersonate another owner (or the framework) by
                // handing in a descriptor with a different Origin.
                var normalized = string.Equals(descriptor.Origin, owner, StringComparison.OrdinalIgnoreCase)
                    ? descriptor
                    : WithOrigin(descriptor, owner);
                RegisterCore(normalized);
                names.Add(normalized.Name);
            }

            _ownerCommands[owner] = names;
            _logger.LogDebug("CLI owner '{Owner}' now exposes {Count} command(s).", owner, names.Count);
        }
    }

    public bool TryGet(string name, out CliCommandDescriptor command)
    {
        lock (_gate)
        {
            if (_commands.TryGetValue(name, out var found) || _aliases.TryGetValue(name, out found))
            {
                command = found;
                return true;
            }

            command = null!;
            return false;
        }
    }

    /// <summary>Resolves the longest matching command path ("plugins list" before "plugins").</summary>
    public bool TryResolve(IReadOnlyList<string> args, out CliCommandDescriptor command, out int consumed)
    {
        lock (_gate)
        {
            for (var depth = Math.Min(MaxDepthCore(), args.Count); depth >= 1; depth--)
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

    private static CliCommandDescriptor WithOrigin(CliCommandDescriptor descriptor, string owner) => new()
    {
        Name = descriptor.Name,
        Description = descriptor.Description,
        Usage = descriptor.Usage,
        Aliases = descriptor.Aliases,
        Options = descriptor.Options,
        Arguments = descriptor.Arguments,
        Origin = owner,
        HandlerFactory = descriptor.HandlerFactory,
    };

    private int MaxDepthCore()
        => _commands.Count == 0
            ? 1
            : _commands.Keys.Max(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    private void RegisterCore(CliCommandDescriptor descriptor)
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

        if (IsPluginOwner(descriptor.Origin))
        {
            if (!_ownerCommands.TryGetValue(descriptor.Origin, out var names))
                _ownerCommands[descriptor.Origin] = names = [];

            if (!names.Contains(descriptor.Name, StringComparer.OrdinalIgnoreCase))
                names.Add(descriptor.Name);
        }
    }

    private static bool IsPluginOwner(string? origin)
        => !string.IsNullOrWhiteSpace(origin)
           && origin.StartsWith(NpsCliCommandOwners.Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds the plugin-contributed part of the catalog from the live contributors. Built-in
    /// commands are untouched. Idempotent; used after a plugin is unloaded so its commands vanish
    /// while every still-active plugin keeps its own (task-43).
    /// </summary>
    public void ReloadPluginCommands()
    {
        lock (_gate)
            ReloadPluginCommandsCore();
    }

    private void ReloadPluginCommandsCore()
    {
        foreach (var owner in _ownerCommands.Keys.ToArray())
            RemoveOwnerCore(owner);

        foreach (var contributor in _contributors)
        {
            CliCommandDescriptor[] produced;
            try
            {
                produced = contributor.GetCommands().ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command contributor {Contributor} failed while reloading.",
                    contributor.GetType().FullName);
                continue;
            }

            var byOwner = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in produced)
            {
                var owner = string.IsNullOrWhiteSpace(descriptor.Origin) ? "plugin:unknown" : descriptor.Origin;
                RegisterCore(descriptor);
                if (!byOwner.TryGetValue(owner, out var names))
                    byOwner[owner] = names = [];

                names.Add(descriptor.Name);
            }

            foreach (var (owner, names) in byOwner)
                _ownerCommands[owner] = names;
        }
    }

    private void RemoveOwnerCore(string owner)
    {
        if (!_ownerCommands.Remove(owner, out var names))
            return;

        foreach (var name in names)
        {
            if (!_commands.TryGetValue(name, out var descriptor))
                continue;

            // Only remove commands this owner actually registered: a built-in command may have
            // replaced the plugin command in the meantime, and that one must survive.
            if (!string.Equals(descriptor.Origin, owner, StringComparison.OrdinalIgnoreCase))
                continue;

            _commands.Remove(name);
            foreach (var alias in descriptor.Aliases)
                _aliases.Remove(alias);
        }

        _logger.LogDebug("CLI owner '{Owner}' withdrew {Count} command(s).", owner, names.Count);
    }
}
