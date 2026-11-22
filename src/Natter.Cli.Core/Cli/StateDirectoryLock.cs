using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Exclusive per-state-dir serve lock; one bot token must not run twice against one state.</summary>
internal static class StateDirectoryLock
{
    public static FileStream? TryAcquire(NatterCliOptions options, IConsole console, bool quiet = false)
    {
        if (string.IsNullOrWhiteSpace(options.StateDirectory))
            return null;

        var directory = Path.GetFullPath(options.StateDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.WriteLine($"pid={Environment.ProcessId} instance={Environment.ProcessId}-{Guid.NewGuid():N}");
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
        catch (IOException)
        {
            if (!quiet)
            {
                console.Error.WriteLine(
                    $"Another Natter serve instance is using state directory '{directory}'. "
                    + "Use one instance per bot token/state directory.");
            }
            return null;
        }
    }
}
