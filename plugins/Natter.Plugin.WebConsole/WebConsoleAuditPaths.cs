namespace Natter.Plugin.WebConsole;

/// <summary>Where the audit log lives, so operators can tell a writable state root from a read-only mount.</summary>
public enum WebConsoleAuditRoot
{
    /// <summary><c>$NATTER_STATE_DIR/webconsole/audit</c> — the only layout that works under a read-only plugin tree.</summary>
    StateDirectory = 0,

    /// <summary><c>&lt;plugin&gt;/audit</c> — legacy fallback when no state root is configured.</summary>
    PluginDirectory = 1,
}

public sealed record WebConsoleAuditLocation(
    string Directory,
    WebConsoleAuditRoot Root,
    string? Warning)
{
    public string FilePath => Path.Combine(Directory, WebConsoleDefaults.AuditFileName);
}

/// <summary>
/// Resolves the audit directory. The plugin package usually lives under a read-only mount
/// (<c>/opt/natter/plugins</c>, systemd <c>ProtectSystem=strict</c>, Docker read-only layer), so the
/// default is the writable state root; writing next to the plugin DLL is only a compatibility
/// fallback for bare <c>dotnet natter</c> runs without <c>NATTER_STATE_DIR</c>.
/// </summary>
public static class WebConsoleAuditPaths
{
    public const string PluginFallbackWarning =
        "NATTER_STATE_DIR is not set: the audit log falls back to <plugin>/audit, which fails on a "
        + "read-only plugin mount (systemd ProtectSystem=strict, read-only container). Set "
        + "NATTER_STATE_DIR (for example /var/lib/natter or /state) to move it to the writable state root.";

    public static WebConsoleAuditLocation Resolve(string pluginDirectory, string? stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        if (!string.IsNullOrWhiteSpace(stateDirectory))
        {
            var root = Path.GetFullPath(stateDirectory.Trim());
            return new WebConsoleAuditLocation(
                Path.Combine(root, WebConsoleDefaults.AuditStateDirectoryName, WebConsoleDefaults.AuditDirectoryName),
                WebConsoleAuditRoot.StateDirectory,
                Warning: null);
        }

        return new WebConsoleAuditLocation(
            Path.Combine(Path.GetFullPath(pluginDirectory), WebConsoleDefaults.AuditDirectoryName),
            WebConsoleAuditRoot.PluginDirectory,
            PluginFallbackWarning);
    }

    /// <summary>Reads <c>NATTER_STATE_DIR</c> once; kept separate so tests never mutate process env.</summary>
    public static string? ReadStateDirectoryFromEnvironment()
        => Environment.GetEnvironmentVariable(WebConsoleDefaults.StateDirectoryEnvironmentVariable);

    public static string RootName(WebConsoleAuditRoot root)
        => root == WebConsoleAuditRoot.StateDirectory ? "state-directory" : "plugin-directory";
}
