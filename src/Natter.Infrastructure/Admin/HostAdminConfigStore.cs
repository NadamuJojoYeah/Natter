using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Host implementation of <see cref="IAdminConfigStore"/>. Whitelists document ids, validates
/// content and schema, writes a backup, takes a per-document write lock and replaces the file
/// atomically. It never reads or writes secret files and never follows a symlinked document.
/// </summary>
public sealed class HostAdminConfigStore : IAdminConfigStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly AdminOptions _options;
    private readonly IPluginCatalog _catalog;
    private readonly IAdminConfigSchemaValidator _schemaValidator;
    private readonly IAdminAuditSink _audit;
    private readonly ILogger<HostAdminConfigStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new(StringComparer.Ordinal);

    public HostAdminConfigStore(
        AdminOptions options,
        IPluginCatalog catalog,
        IAdminConfigSchemaValidator schemaValidator,
        IAdminAuditSink audit,
        ILogger<HostAdminConfigStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<IReadOnlyList<AdminConfigDocumentInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetConfigRoot(out var root))
            return ValueTask.FromResult<IReadOnlyList<AdminConfigDocumentInfo>>([]);

        var documents = new List<AdminConfigDocumentInfo>
        {
            DescribeDocument(AdminConfigDocumentPolicy.ProfilesId, root, packageDirectory: null),
        };

        foreach (var plugin in _catalog.Plugins
                     .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
                     .Take(200))
        {
            var id = AdminConfigDocumentPolicy.PluginPrefix + plugin.Id;
            if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out _, out _))
                continue;

            documents.Add(DescribeDocument(normalized, root, plugin.Directory));
        }

        return ValueTask.FromResult<IReadOnlyList<AdminConfigDocumentInfo>>(documents);
    }

    public ValueTask<AdminConfigReadResult> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out var code, out var message))
            return ValueTask.FromResult(AdminConfigReadResult.Failed(code, message));

        if (!TryGetConfigRoot(out var root))
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigDirectoryUnavailable,
                "The host has no config directory; admin config is unavailable."));

        if (!TryResolveTarget(normalized, root, out var target, out code, out message))
            return ValueTask.FromResult(AdminConfigReadResult.Failed(code, message));

        var state = ReadTarget(target);
        if (state.SymlinkDetected)
        {
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigSymlinkNotAllowed,
                $"Config document '{normalized}' is a symbolic link and will not be read."));
        }

        var document = new AdminConfigDocument(
            normalized,
            state.Content ?? string.Empty,
            AdminETag.Compute(state.Content ?? string.Empty),
            state.LastModified,
            state.Exists,
            state.Source,
            AdminConfigDocumentPolicy.ReloadHintFor(normalized));

        return ValueTask.FromResult(AdminConfigReadResult.Ok(document));
    }

    public async ValueTask<AdminConfigValidation> ValidateAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out var code, out var message))
        {
            return new AdminConfigValidation(
                id ?? string.Empty,
                false,
                AdminCodes.ConfigIdInvalid,
                AdminETag.Compute(content),
                Encoding.UTF8.GetByteCount(content ?? string.Empty),
                false,
                [new AdminConfigIssue("/id", code, message)],
                AdminConfigReloadHint.None);
        }

        content ??= string.Empty;
        var reloadHint = AdminConfigDocumentPolicy.ReloadHintFor(normalized);

        if (!TryGetConfigRoot(out var root))
        {
            code = AdminCodes.ConfigDirectoryUnavailable;
            message = "The host has no config directory; admin config is unavailable.";
        }
        else if (!TryResolveTarget(normalized, root, out var target, out code, out message))
        {
            // code/message are filled by TryResolveTarget.
        }
        else
        {
            var resolvedIssues = await ValidateContentAsync(normalized, content, cancellationToken).ConfigureAwait(false);
            var resolvedCurrent = ReadTarget(target);
            var resolvedDiff = resolvedCurrent.SymlinkDetected
                ? null
                : AdminConfigDiffBuilder.Build(resolvedCurrent.Exists ? resolvedCurrent.Content : null, content);

            return new AdminConfigValidation(
                normalized,
                resolvedIssues.Count == 0,
                resolvedIssues.Count == 0 ? AdminCodes.ConfigValid : AdminCodes.ConfigInvalid,
                AdminETag.Compute(content),
                Encoding.UTF8.GetByteCount(content),
                AdminConfigContentValidator.ContainsSecretLiteral(content),
                resolvedIssues,
                reloadHint,
                resolvedDiff);
        }

        return new AdminConfigValidation(
            normalized,
            false,
            code,
            AdminETag.Compute(content),
            Encoding.UTF8.GetByteCount(content),
            false,
            [new AdminConfigIssue("$", code, message)],
            reloadHint);
    }

    public async ValueTask<AdminConfigSaveResult> SaveAsync(
        string id,
        string content,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        content ??= string.Empty;

        if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out var code, out var message))
            return await RejectAsync(id ?? string.Empty, AdminCodes.ConfigIdInvalid, message, [], null, startedAt).ConfigureAwait(false);

        var reloadHint = AdminConfigDocumentPolicy.ReloadHintFor(normalized);

        if (!TryGetConfigRoot(out var root))
        {
            return await RejectAsync(
                normalized,
                AdminCodes.ConfigDirectoryUnavailable,
                "The host has no config directory; admin config is unavailable.",
                [],
                reloadHint,
                startedAt).ConfigureAwait(false);
        }

        if (!TryResolveTarget(normalized, root, out var target, out code, out message))
            return await RejectAsync(normalized, code, message, [], reloadHint, startedAt).ConfigureAwait(false);

        var issues = await ValidateContentAsync(normalized, content, cancellationToken).ConfigureAwait(false);
        if (issues.Count > 0)
        {
            return await RejectAsync(
                normalized,
                AdminCodes.ConfigInvalid,
                "Config content failed validation.",
                issues,
                reloadHint,
                startedAt).ConfigureAwait(false);
        }

        var path = target.ConfigDirectoryPath;
        var gate = _writeLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromMilliseconds(_options.WriteLockTimeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false))
        {
            return await RejectAsync(
                normalized,
                AdminCodes.ConfigLocked,
                "Another write to this document is in progress.",
                [],
                reloadHint,
                startedAt).ConfigureAwait(false);
        }

        try
        {
            try
            {
                // OBS-WC-CONFIG-MODE: tighten a pre-existing config directory too; chmod does not
                // touch the document contents.
                Directory.CreateDirectory(root);
                SetUnixMode(root, PrivateDirectoryMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Admin config directory {Directory} could not be created.", root);
                return await RejectAsync(
                    normalized,
                    AdminCodes.ConfigWriteFailed,
                    "The host config directory could not be created.",
                    [],
                    reloadHint,
                    startedAt).ConfigureAwait(false);
            }

            var current = ReadTarget(target);
            if (current.SymlinkDetected)
            {
                return await RejectAsync(
                    normalized,
                    AdminCodes.ConfigSymlinkNotAllowed,
                    $"Config document '{normalized}' is a symbolic link and will not be overwritten.",
                    [],
                    reloadHint,
                    startedAt).ConfigureAwait(false);
            }

            var currentETag = AdminETag.Compute(current.Exists ? current.Content : string.Empty);
            var conflict = etag is null
                ? current.Exists
                : !string.Equals(etag, currentETag, StringComparison.Ordinal);

            if (conflict)
            {
                return await RejectAsync(
                    normalized,
                    AdminCodes.ConfigEtagMismatch,
                    current.Exists
                        ? "The document changed since it was read; reload before saving."
                        : "The document does not exist yet; pass a null ETag to create it.",
                    [],
                    reloadHint,
                    startedAt,
                    conflictETag: currentETag).ConfigureAwait(false);
            }

            string? backupPath = null;
            if (current.Exists)
            {
                try
                {
                    backupPath = WriteBackup(root, target, current.Content ?? string.Empty);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Admin config backup for {DocumentId} failed.", normalized);
                    return await RejectAsync(
                        normalized,
                        AdminCodes.ConfigBackupFailed,
                        "The current document could not be backed up; nothing was written.",
                        [],
                        reloadHint,
                        startedAt).ConfigureAwait(false);
                }
            }

            try
            {
                WriteAtomically(root, path, content, current.Exists);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Admin config write for {DocumentId} failed.", normalized);
                return await RejectAsync(
                    normalized,
                    AdminCodes.ConfigWriteFailed,
                    "The document could not be written; the previous content is unchanged.",
                    [],
                    reloadHint,
                    startedAt).ConfigureAwait(false);
            }

            var diff = AdminConfigDiffBuilder.Build(current.Exists ? current.Content : null, content);
            var result = new AdminConfigSaveResult(
                Saved: true,
                Code: AdminCodes.ConfigSaved,
                Message: "Config saved. Restart the host or reload the plugin to apply it.",
                Id: normalized,
                ETag: AdminETag.Compute(content),
                ConflictETag: null,
                BackupPath: backupPath,
                Issues: [],
                Diff: diff,
                ReloadHint: reloadHint);

            await WriteAuditAsync(AdminAuditActions.SaveConfig, normalized, "ok", AdminCodes.ConfigSaved, backupPath, startedAt)
                .ConfigureAwait(false);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<AdminConfigIssue>> ValidateContentAsync(
        string normalizedId,
        string content,
        CancellationToken cancellationToken)
    {
        var issues = new List<AdminConfigIssue>(AdminConfigContentValidator.Validate(content));
        if (issues.Any(issue => string.Equals(issue.Code, AdminCodes.ConfigInvalidJson, StringComparison.Ordinal)))
            return issues;

        var schemaIssues = await _schemaValidator.ValidateAsync(normalizedId, content, cancellationToken).ConfigureAwait(false);
        issues.AddRange(schemaIssues);
        return issues;
    }

    private async ValueTask<AdminConfigSaveResult> RejectAsync(
        string documentId,
        string code,
        string message,
        IReadOnlyList<AdminConfigIssue> issues,
        AdminConfigReloadHint? reloadHint,
        long startedAt,
        string? conflictETag = null)
    {
        await WriteAuditAsync(AdminAuditActions.SaveConfig, documentId, "denied", code, null, startedAt).ConfigureAwait(false);

        return new AdminConfigSaveResult(
            Saved: false,
            Code: code,
            Message: message,
            Id: documentId,
            ETag: null,
            ConflictETag: conflictETag,
            BackupPath: null,
            Issues: issues,
            Diff: null,
            ReloadHint: reloadHint ?? AdminConfigReloadHint.None);
    }

    private async ValueTask WriteAuditAsync(
        string action,
        string target,
        string result,
        string? code,
        string? detail,
        long startedAt)
    {
        if (!_options.EnableAudit)
            return;

        try
        {
            await _audit.RecordAsync(new AdminAuditEvent(
                DateTimeOffset.UtcNow,
                "host",
                action,
                target,
                result,
                code,
                detail,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin audit line for {Action} could not be recorded.", action);
        }
    }

    private AdminConfigDocumentInfo DescribeDocument(string normalizedId, string root, string? packageDirectory)
    {
        var reloadHint = AdminConfigDocumentPolicy.ReloadHintFor(normalizedId);
        if (TryDescribeFile(
                Path.Combine(root, AdminConfigDocumentPolicy.FileNameFor(normalizedId)),
                AdminConfigDocumentSource.ConfigDirectory) is { } fromConfigDirectory)
        {
            return fromConfigDirectory with { Id = normalizedId, ReloadHint = reloadHint };
        }

        if (packageDirectory is not null
            && TryDescribeFile(Path.Combine(packageDirectory, "config.json"), AdminConfigDocumentSource.PluginPackage)
                is { } fromPackage)
        {
            return fromPackage with { Id = normalizedId, ReloadHint = reloadHint };
        }

        return new AdminConfigDocumentInfo(
            normalizedId,
            false,
            AdminETag.Compute(string.Empty),
            null,
            AdminConfigDocumentSource.Missing,
            true,
            reloadHint);
    }

    private static AdminConfigDocumentInfo? TryDescribeFile(string path, AdminConfigDocumentSource source)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
                return null;

            return new AdminConfigDocumentInfo(
                string.Empty,
                true,
                AdminETag.Compute(File.ReadAllText(path)),
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                source,
                true,
                AdminConfigReloadHint.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool TryGetConfigRoot(out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.ConfigDirectory))
            return false;

        root = Path.GetFullPath(_options.ConfigDirectory);
        return true;
    }

    private bool TryResolveTarget(
        string normalizedId,
        string root,
        out DocumentTarget target,
        out string code,
        out string message)
    {
        target = default;
        code = string.Empty;
        message = string.Empty;

        var fileName = AdminConfigDocumentPolicy.FileNameFor(normalizedId);
        var isProfilesDocument = string.Equals(
            normalizedId,
            AdminConfigDocumentPolicy.ProfilesId,
            StringComparison.Ordinal);
        if (!isProfilesDocument && AdminConfigDocumentPolicy.IsSecretFileName(fileName))
        {
            code = AdminCodes.ConfigDocumentNotAllowed;
            message = "Secret files are not part of the admin config surface.";
            return false;
        }

        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            code = AdminCodes.ConfigDocumentNotAllowed;
            message = "Document path resolves outside the host config directory.";
            return false;
        }

        string? packageDirectory = null;
        if (AdminConfigDocumentPolicy.TryGetPluginId(normalizedId, out var pluginId))
        {
            var descriptor = _catalog.Plugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
            if (descriptor is null)
            {
                code = AdminCodes.ConfigPluginUnknown;
                message = $"Plugin '{pluginId}' is not registered in this host.";
                return false;
            }

            packageDirectory = descriptor.Directory;
        }

        target = new DocumentTarget(normalizedId, fileName, path, packageDirectory);
        return true;
    }

    private DocumentState ReadTarget(DocumentTarget target)
    {
        var path = target.ConfigDirectoryPath;
        var link = new FileInfo(path).LinkTarget;
        if (link is not null)
            return new DocumentState(false, null, default, AdminConfigDocumentSource.Missing, SymlinkDetected: true);

        if (File.Exists(path))
        {
            return new DocumentState(
                true,
                File.ReadAllText(path),
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                AdminConfigDocumentSource.ConfigDirectory,
                SymlinkDetected: false);
        }

        if (target.PackageDirectory is not null)
        {
            var packagePath = Path.Combine(target.PackageDirectory, "config.json");
            var packageLink = new FileInfo(packagePath).LinkTarget;
            if (packageLink is not null)
                return new DocumentState(false, null, default, AdminConfigDocumentSource.Missing, SymlinkDetected: true);

            if (File.Exists(packagePath))
            {
                return new DocumentState(
                    true,
                    File.ReadAllText(packagePath),
                    new DateTimeOffset(File.GetLastWriteTimeUtc(packagePath), TimeSpan.Zero),
                    AdminConfigDocumentSource.PluginPackage,
                    SymlinkDetected: false);
            }
        }

        return new DocumentState(false, null, default, AdminConfigDocumentSource.Missing, SymlinkDetected: false);
    }

    private string WriteBackup(string root, DocumentTarget target, string content)
    {
        var backupRoot = Path.Combine(root, _options.BackupDirectoryName);
        Directory.CreateDirectory(backupRoot);
        SetUnixMode(backupRoot, PrivateDirectoryMode);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var directory = Path.Combine(backupRoot, timestamp);
        var suffix = 0;
        while (Directory.Exists(directory))
        {
            suffix++;
            directory = Path.Combine(backupRoot, timestamp + "-" + suffix.ToString(CultureInfo.InvariantCulture));
        }

        Directory.CreateDirectory(directory);
        SetUnixMode(directory, PrivateDirectoryMode);

        var backupPath = Path.Combine(directory, target.FileName);
        File.WriteAllText(backupPath, content, Utf8NoBom);
        SetUnixMode(backupPath, PrivateFileMode);

        return Path.GetRelativePath(root, backupPath).Replace('\\', '/');
    }

    private static void WriteAtomically(string root, string path, string content, bool exists)
    {
        var tempPath = Path.Combine(
            root,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            File.WriteAllText(tempPath, content, Utf8NoBom);
            if (!OperatingSystem.IsWindows())
            {
                var mode = exists ? File.GetUnixFileMode(path) : PrivateFileMode;
                File.SetUnixFileMode(tempPath, mode);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct DocumentTarget(
        string Id,
        string FileName,
        string ConfigDirectoryPath,
        string? PackageDirectory);

    private readonly record struct DocumentState(
        bool Exists,
        string? Content,
        DateTimeOffset LastModified,
        AdminConfigDocumentSource Source,
        bool SymlinkDetected);
}
