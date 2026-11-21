namespace Natter.Application.Admin;

/// <summary>
/// Stable result codes for the host admin surface (WebConsole gaps G4/G5/G6/G8). Codes mirror the
/// WebConsole plugin policy codes so the plugin can map them to HTTP status without translation.
/// </summary>
public static class AdminCodes
{
    // Admin config store.
    public const string ConfigUnavailable = "config.unavailable";
    public const string ConfigDirectoryUnavailable = "config.directory_unavailable";
    public const string ConfigIdInvalid = "config.id_invalid";
    public const string ConfigDocumentNotAllowed = "config.document_not_allowed";
    public const string ConfigSymlinkNotAllowed = "config.symlink_not_allowed";
    public const string ConfigPluginUnknown = "config.plugin_unknown";
    public const string ConfigNotFound = "config.not_found";
    public const string ConfigInvalidJson = "config.invalid_json";
    public const string ConfigSchemaInvalid = "config.schema_invalid";
    public const string ConfigTooLarge = "config.too_large";
    public const string ConfigControlChars = "config.control_chars";
    public const string ConfigSecretLiteral = "config.secret_literal";
    public const string ConfigRootType = "config.root_type";
    public const string ConfigEtagMismatch = "config.etag_mismatch";
    public const string ConfigCreateRequiresMissing = "config.create_requires_missing";
    public const string ConfigBackupFailed = "config.backup_failed";
    public const string ConfigWriteFailed = "config.write_failed";
    public const string ConfigLocked = "config.locked";
    public const string ConfigSaved = "config.saved";
    public const string ConfigValid = "config.valid";
    public const string ConfigInvalid = "config.invalid";

    // Admin command runner.
    public const string CommandAllowed = "command.allowed";
    public const string CommandNotAllowed = "command.not_allowed";
    public const string CommandNotRegistered = "command.not_registered";
    public const string CommandInvalid = "command.invalid";
    public const string CommandSucceeded = "command.succeeded";
    public const string CommandFailed = "command.failed";
    public const string CommandTimeout = "command.timeout";
    public const string CommandCancelled = "command.cancelled";
    public const string CommandBusy = "command.busy";
}

/// <summary>Audit action names recorded by the host admin surface.</summary>
public static class AdminAuditActions
{
    public const string ReadConfig = "admin.config.read";
    public const string ValidateConfig = "admin.config.validate";
    public const string SaveConfig = "admin.config.save";
    public const string RunCommand = "admin.command.run";
    public const string ReadLogs = "admin.logs.read";
}

/// <summary>How the host tells the operator whether a saved document needs a reload/restart.</summary>
public enum AdminConfigReloadHint
{
    None = 0,
    ReloadProfiles = 1,
    RestartHost = 2,
    RestartPlugin = 3,
}

/// <summary>One validation or schema issue with a JSON Pointer.</summary>
public sealed record AdminConfigIssue(
    string Pointer,
    string Code,
    string Message,
    string? Expected = null,
    string? Actual = null);

/// <summary>Where a config document is read from.</summary>
public enum AdminConfigDocumentSource
{
    Missing = 0,
    ConfigDirectory = 1,
    PluginPackage = 2,
}

/// <summary>Read result for one whitelisted config document.</summary>
public sealed record AdminConfigDocument(
    string Id,
    string Content,
    string ETag,
    DateTimeOffset LastModified,
    bool Exists,
    AdminConfigDocumentSource Source,
    AdminConfigReloadHint ReloadHint);

/// <summary>Read result; rejected ids are reported by code instead of by exception.</summary>
public sealed record AdminConfigReadResult(
    bool Available,
    string Code,
    string Message,
    AdminConfigDocument? Document)
{
    public static AdminConfigReadResult Ok(AdminConfigDocument document)
        => new(true, string.Empty, string.Empty, document);

    public static AdminConfigReadResult Failed(string code, string message)
        => new(false, code, message, null);
}

/// <summary>Lightweight listing entry (no content, no secrets).</summary>
public sealed record AdminConfigDocumentInfo(
    string Id,
    bool Exists,
    string ETag,
    DateTimeOffset? LastModified,
    AdminConfigDocumentSource Source,
    bool Writable,
    AdminConfigReloadHint ReloadHint);

/// <summary>
/// Bounded line diff between the stored document and the proposed content. Lines use the same
/// prefix convention as the WebConsole plugin policy: <c>" "</c> context, <c>"-"</c> removed,
/// <c>"+"</c> added.
/// </summary>
public sealed record AdminConfigDiff(
    int Added,
    int Removed,
    int Changed,
    bool Truncated,
    IReadOnlyList<string> Lines);

/// <summary>Validation result; <see cref="Diff"/> is only filled when a base document exists.</summary>
public sealed record AdminConfigValidation(
    string Id,
    bool Valid,
    string Code,
    string ETag,
    int SizeBytes,
    bool ContainsSecretLiteral,
    IReadOnlyList<AdminConfigIssue> Issues,
    AdminConfigReloadHint ReloadHint,
    AdminConfigDiff? Diff = null);

/// <summary>
/// Result of a config save. <see cref="Saved"/> is false for every rejected write;
/// <see cref="ConflictETag"/> carries the current document ETag on an optimistic-concurrency
/// conflict (no exception is thrown for a stale write).
/// </summary>
public sealed record AdminConfigSaveResult(
    bool Saved,
    string Code,
    string Message,
    string Id,
    string? ETag,
    string? ConflictETag,
    string? BackupPath,
    IReadOnlyList<AdminConfigIssue> Issues,
    AdminConfigDiff? Diff,
    AdminConfigReloadHint ReloadHint)
{
    public bool RestartRequired
        => ReloadHint is AdminConfigReloadHint.RestartHost or AdminConfigReloadHint.RestartPlugin;

    public bool Conflict => string.Equals(Code, AdminCodes.ConfigEtagMismatch, StringComparison.Ordinal);
}

/// <summary>
/// Host-side config store for the admin surface. Document ids are whitelisted
/// (<c>profiles</c> and <c>plugin:&lt;pluginId&gt;</c>); secret files are never read or written and
/// symbolic links are rejected.
/// </summary>
public interface IAdminConfigStore
{
    /// <summary>Lists the whitelisted documents that exist or could be created.</summary>
    ValueTask<IReadOnlyList<AdminConfigDocumentInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one document. Rejected ids return <see cref="AdminConfigReadResult.Available"/> = false.</summary>
    ValueTask<AdminConfigReadResult> ReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Validates proposed content without touching disk.</summary>
    ValueTask<AdminConfigValidation> ValidateAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates, backs up, locks, writes atomically and returns a reload hint.
    /// <paramref name="etag"/> must match the current document; null is only accepted when the
    /// document does not exist yet. A stale ETag returns a conflict result, never an exception.
    /// </summary>
    ValueTask<AdminConfigSaveResult> SaveAsync(
        string id,
        string content,
        string? etag,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of one restricted command execution.</summary>
public sealed record AdminCommandResult(
    bool Executed,
    string Code,
    string Message,
    string Command,
    IReadOnlyList<string> Args,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool Truncated,
    int OutputBytes,
    long DurationMs,
    bool TimedOut)
{
    public bool Succeeded => Executed && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs allowlisted Natter CLI commands in-process with argv semantics. Never starts a shell or a
/// child process; output is bounded and redacted before it is returned.
/// </summary>
public interface IAdminCommandRunner
{
    ValueTask<AdminCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default);
}

/// <summary>One redacted host log line.</summary>
public sealed record AdminLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    long Sequence);

/// <summary>Explicit read-only host log tail contract (gap G8).</summary>
public interface IAdminLogReader
{
    /// <summary>Returns the most recent entries in chronological order; <paramref name="limit"/> is clamped.</summary>
    ValueTask<IReadOnlyList<AdminLogEntry>> ReadAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Shared host redaction rules for admin output/logs (gap G9).</summary>
public interface IAdminRedactor
{
    /// <summary>Redacts secret-shaped values and secret-named JSON/assignment pairs.</summary>
    string Redact(string? value);
}

/// <summary>One admin audit line. Values are already redacted by the producer.</summary>
public sealed record AdminAuditEvent(
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string Target,
    string Result,
    string? Code = null,
    string? Detail = null,
    long DurationMs = 0);

/// <summary>Append-only audit sink used by the host admin surface.</summary>
public interface IAdminAuditSink
{
    ValueTask RecordAsync(AdminAuditEvent entry, CancellationToken cancellationToken = default);
}

/// <summary>Audit sink used when the host does not persist admin audit lines.</summary>
public sealed class NullAdminAuditSink : IAdminAuditSink
{
    public static NullAdminAuditSink Instance { get; } = new();

    public ValueTask RecordAsync(AdminAuditEvent entry, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
