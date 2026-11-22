using Natter.Cli.Abstractions;

namespace Natter.Cli.Core;

/// <summary>Exclusive per-state-dir serve lock; one bot token must not run twice against one state.</summary>
internal static class StateDirectoryLock
{
    /// <summary>How a lock acquisition failed, if it did.</summary>
    public enum LockFailure
    {
        None = 0,

        /// <summary>Another live instance holds the lock.</summary>
        Busy,

        /// <summary>The state directory cannot be created/opened (permissions, read-only fs, path).</summary>
        Unusable,
    }

    /// <summary>
    /// Checks that the state directory can actually be created and written before the composition
    /// root touches it (S2-E7). Returns false with a reason instead of letting the SQLite store throw
    /// an unhandled UnauthorizedAccessException with a stack trace.
    /// </summary>
    public static bool Probe(string? stateDirectory, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(stateDirectory))
            return true;

        try
        {
            var directory = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static FileStream? TryAcquire(
        NatterCliOptions options,
        IConsole console,
        bool quiet,
        out LockFailure failure)
    {
        failure = LockFailure.None;
        if (string.IsNullOrWhiteSpace(options.StateDirectory))
            return null;

        string directory;
        try
        {
            directory = Path.GetFullPath(options.StateDirectory);
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            // S2-E7: a read-only/unwritable state directory used to escape as an unhandled exception
            // with a stack trace. Report it as a classified failure instead.
            failure = LockFailure.Unusable;
            if (!quiet)
            {
                console.Error.WriteLine(
                    $"error: HL-CLI-STATE-DIR-UNUSABLE: cannot use state directory "
                    + $"'{options.StateDirectory}': {ex.Message}");
            }

            return null;
        }

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
        catch (UnauthorizedAccessException ex)
        {
            failure = LockFailure.Unusable;
            if (!quiet)
            {
                console.Error.WriteLine(
                    $"error: HL-CLI-STATE-DIR-UNUSABLE: cannot open '{path}': {ex.Message}");
            }

            return null;
        }
        catch (IOException)
        {
            failure = LockFailure.Busy;
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
