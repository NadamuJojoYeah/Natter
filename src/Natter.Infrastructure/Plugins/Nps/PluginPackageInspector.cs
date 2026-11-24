namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>Lightweight package hygiene checks from NPS §3 (secrets must not ship in plugin dirs).</summary>
internal static class PluginPackageInspector
{
    private static readonly string[] SensitiveExtensions =
    [
        ".pfx", ".p12", ".key", ".keystore", ".jks", ".pem",
    ];

    private static readonly string[] SensitiveNames =
    [
        ".env", "token", "tokens", "credentials", "credentials.json", "secrets.json",
    ];

    public static IReadOnlyList<string> FindSensitiveFiles(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return [];

        var matches = new List<string>();

        // Directory.EnumerateFiles is lazy: the enumeration (and therefore the UnauthorizedAccessException
        // for an unreadable subdirectory) happens inside the foreach, not at call time. A try/catch around
        // the call alone protects nothing, which is exactly how a root-owned 0700 plugin subdirectory used
        // to abort the whole host at startup.
        //
        // Use the eager overload inside the try, and keep the per-directory failure non-fatal: a package we
        // cannot fully read is still a package we must not crash on.
        string[] files;
        try
        {
            files = Directory.GetFiles(pluginDirectory, "*", SearchOption.AllDirectories);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or IOException
                                              or System.Security.SecurityException)
        {
            // Fall back to a best-effort scan: walk what we can reach and skip what we cannot.
            files = EnumerateReadableFiles(pluginDirectory);
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);

            if (SensitiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || SensitiveNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                matches.Add(Path.GetRelativePath(pluginDirectory, file));
            }
        }

        return matches;
    }

    /// <summary>
    /// Depth-first walk that swallows per-directory access failures instead of aborting the scan.
    /// A single unreadable subdirectory (for example a root-owned audit directory) must not hide the
    /// rest of the package, and must never take the process down.
    /// </summary>
    private static string[] EnumerateReadableFiles(string root)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            try
            {
                found.AddRange(Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or IOException
                                                  or System.Security.SecurityException)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                    pending.Push(child);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or IOException
                                                  or System.Security.SecurityException)
            {
                // Reached the file list but not the subdirectories; keep what we have.
            }
        }

        return found.ToArray();
    }
}
