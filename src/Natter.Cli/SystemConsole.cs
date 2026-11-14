using Natter.Cli.Abstractions;

namespace Natter.Cli;

/// <summary>System.Console based host console (Windows/macOS/Linux).</summary>
internal sealed class SystemConsole : IConsole
{
    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;

    public bool IsInteractive => !Console.IsOutputRedirected && !Console.IsInputRedirected;

    public string? ReadLine() => Console.ReadLine();
}
