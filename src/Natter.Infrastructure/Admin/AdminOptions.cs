using Natter.Abstractions.Composition;
using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Host policy for the admin surface (WebConsole gaps G4/G5/G6/G8). Defaults are the conservative
/// values from the WebConsole design: 256 KiB documents, 64 KiB command output, 10s command
/// timeout, read-only command allowlist.
/// </summary>
public sealed class AdminOptions
{
    /// <summary>Directory that owns <c>profiles.json</c> and <c>&lt;pluginId&gt;.json</c>.</summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>Host state directory; admin audit lines live here.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>When false, no admin service is registered and plugins keep their 501 fallback.</summary>
    public bool EnableAdminSurface { get; set; } = true;

    /// <summary>Backup folder name inside <see cref="ConfigDirectory"/>.</summary>
    public string BackupDirectoryName { get; set; } = ".backup";

    public int WriteLockTimeoutMilliseconds { get; set; } = 5_000;

    public int MaxLogEntries { get; set; } = 500;

    public int MaxLogLimit { get; set; } = 500;

    public int MaxLogMessageLength { get; set; } = 2_000;

    public int CommandTimeoutSeconds { get; set; } = AdminCommandPolicy.DefaultTimeoutSeconds;

    public int MaxCommandOutputBytes { get; set; } = AdminCommandPolicy.MaxOutputBytes;

    /// <summary>Allowlisted Natter CLI command names (exact match after whitespace normalization).</summary>
    public IList<string> CommandAllowlist { get; } = new List<string>(AdminCommandPolicy.DefaultAllowlist);

    public string AuditFileName { get; set; } = "admin-audit.jsonl";

    public bool EnableAudit { get; set; } = true;

    public static AdminOptions FromComposition(CompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new AdminOptions
        {
            ConfigDirectory = options.PluginConfigDirectory,
            StateDirectory = options.StateDirectory,
        };
    }
}
