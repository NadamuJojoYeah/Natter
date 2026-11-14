namespace Natter.Cli.Abstractions;

/// <summary>
/// Output/input abstraction of a CLI host. The console host maps it to
/// System.Console, the Android head maps it to a TextView.
/// </summary>
public interface IConsole
{
    TextWriter Out { get; }

    TextWriter Error { get; }

    bool IsInteractive { get; }

    string? ReadLine();
}

/// <summary>Console implementation used when no host console is available.</summary>
public sealed class NullConsole : IConsole
{
    public static NullConsole Instance { get; } = new();

    public TextWriter Out => TextWriter.Null;

    public TextWriter Error => TextWriter.Null;

    public bool IsInteractive => false;

    public string? ReadLine() => null;
}

/// <summary>Ambient access to the current host console (used by lifecycle hooks/logging).</summary>
public interface IConsoleAccessor
{
    IConsole Current { get; }
}
