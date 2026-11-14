using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Mutable accessor populated by the CLI runner before a command executes.</summary>
public sealed class ConsoleAccessor : IConsoleAccessor
{
    private IConsole _current = NullConsole.Instance;

    public IConsole Current => Volatile.Read(ref _current);

    public void Set(IConsole console) => Volatile.Write(ref _current, console);
}
