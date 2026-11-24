using System.Text;
using Natter.Application.Admin;

namespace Natter.Plugin.WebConsole.Tests.Phase23;

/// <summary>
/// In-memory <see cref="IAdminConfigStore"/>: mirrors the host semantics that the console depends on
/// (etag conflict without exceptions, per-document write lock, backup path, reload hint, id
/// whitelist) without touching the filesystem.
/// </summary>
internal sealed class FakeAdminConfigStore : IAdminConfigStore
{
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    public bool Available { get; set; } = true;

    public bool FailWithError { get; set; }

    public bool ForceConflict { get; set; }

    public bool ForceLocked { get; set; }

    public bool ForceBackupFailure { get; set; }

    public bool RejectContent { get; set; }

    public string? LastSavedContent { get; private set; }

    public string? LastEtag { get; private set; }

    public int SaveCount { get; private set; }

    public int ReadCount { get; private set; }

    public IReadOnlyList<string> Documents => _documents.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    public FakeAdminConfigStore WithDocument(string id, string content)
    {
        _documents[id] = content;
        return this;
    }

    public string? ContentOf(string id) => _documents.TryGetValue(id, out var content) ? content : null;

    public ValueTask<IReadOnlyList<AdminConfigDocumentInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Available)
            return ValueTask.FromResult<IReadOnlyList<AdminConfigDocumentInfo>>([]);

        var items = _documents
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AdminConfigDocumentInfo(
                pair.Key,
                true,
                AdminETag.Compute(pair.Value),
                DateTimeOffset.UnixEpoch,
                AdminConfigDocumentSource.ConfigDirectory,
                true,
                AdminConfigDocumentPolicy.ReloadHintFor(pair.Key)))
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<AdminConfigDocumentInfo>>(items);
    }

    public ValueTask<AdminConfigReadResult> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        ReadCount++;

        if (FailWithError)
            throw new InvalidOperationException("fake store failure with sk-abcdefghijkl");

        if (!Available)
        {
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigDirectoryUnavailable,
                "The host has no config directory."));
        }

        if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out var code, out var message))
            return ValueTask.FromResult(AdminConfigReadResult.Failed(code, message));

        if (normalized.StartsWith("plugin:", StringComparison.Ordinal)
            && !string.Equals(normalized, "plugin:natter.tool.admin.webconsole", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigPluginUnknown,
                $"Plugin '{normalized}' is not registered in this host."));
        }

        var exists = _documents.TryGetValue(normalized, out var content);
        var document = new AdminConfigDocument(
            normalized,
            content ?? string.Empty,
            AdminETag.Compute(content ?? string.Empty),
            DateTimeOffset.UnixEpoch,
            exists,
            exists ? AdminConfigDocumentSource.ConfigDirectory : AdminConfigDocumentSource.Missing,
            AdminConfigDocumentPolicy.ReloadHintFor(normalized));

        return ValueTask.FromResult(AdminConfigReadResult.Ok(document));
    }

    public ValueTask<AdminConfigValidation> ValidateAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<AdminConfigIssue>(AdminConfigContentValidator.Validate(content));
        if (RejectContent)
            issues.Add(new AdminConfigIssue("$.profiles", AdminCodes.ConfigSchemaInvalid, "profiles must be a non-empty array."));

        var normalized = AdminConfigDocumentPolicy.TryNormalize(id, out var idValue, out _, out _) ? idValue : id;
        var valid = issues.Count == 0;

        return ValueTask.FromResult(new AdminConfigValidation(
            normalized,
            valid,
            valid ? AdminCodes.ConfigValid : AdminCodes.ConfigInvalid,
            AdminETag.Compute(content),
            Encoding.UTF8.GetByteCount(content),
            AdminConfigContentValidator.ContainsSecretLiteral(content),
            issues,
            AdminConfigDocumentPolicy.ReloadHintFor(normalized),
            AdminConfigDiffBuilder.Build(_documents.GetValueOrDefault(normalized), content)));
    }

    public ValueTask<AdminConfigSaveResult> SaveAsync(
        string id,
        string content,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        SaveCount++;
        LastEtag = etag;

        if (FailWithError)
            throw new InvalidOperationException("fake store save failure with sk-abcdefghijkl");

        if (!Available)
        {
            return ValueTask.FromResult(Rejected(id, AdminCodes.ConfigDirectoryUnavailable, "The host has no config directory."));
        }

        if (!AdminConfigDocumentPolicy.TryNormalize(id, out var normalized, out var code, out var message))
            return ValueTask.FromResult(Rejected(id, code, message));

        var issues = new List<AdminConfigIssue>(AdminConfigContentValidator.Validate(content));
        if (RejectContent)
            issues.Add(new AdminConfigIssue("$.profiles", AdminCodes.ConfigSchemaInvalid, "profiles must be a non-empty array."));

        if (issues.Count > 0)
        {
            return ValueTask.FromResult(new AdminConfigSaveResult(
                false,
                AdminCodes.ConfigInvalid,
                "Config content failed validation.",
                normalized,
                null,
                null,
                null,
                issues,
                null,
                AdminConfigDocumentPolicy.ReloadHintFor(normalized)));
        }

        if (ForceLocked)
        {
            return ValueTask.FromResult(Rejected(normalized, AdminCodes.ConfigLocked, "Another write to this document is in progress."));
        }

        var exists = _documents.TryGetValue(normalized, out var current);
        var currentETag = AdminETag.Compute(current ?? string.Empty);
        var conflict = ForceConflict
                       || (etag is null ? exists : !string.Equals(etag, currentETag, StringComparison.Ordinal));

        if (conflict)
        {
            return ValueTask.FromResult(Rejected(
                normalized,
                AdminCodes.ConfigEtagMismatch,
                "The document changed since it was read; reload before saving.",
                currentETag));
        }

        if (ForceBackupFailure)
        {
            return ValueTask.FromResult(Rejected(
                normalized,
                AdminCodes.ConfigBackupFailed,
                "The current document could not be backed up; nothing was written."));
        }

        var backupPath = exists ? $".backup/20261121T000000000Z/{FileName(normalized)}" : null;
        _documents[normalized] = content;
        LastSavedContent = content;

        return ValueTask.FromResult(new AdminConfigSaveResult(
            true,
            AdminCodes.ConfigSaved,
            "Config saved. Restart the host or reload the plugin to apply it.",
            normalized,
            AdminETag.Compute(content),
            null,
            backupPath,
            [],
            AdminConfigDiffBuilder.Build(current, content),
            AdminConfigDocumentPolicy.ReloadHintFor(normalized)));
    }

    private static AdminConfigSaveResult Rejected(string id, string code, string message, string? conflictETag = null)
        => new(
            false,
            code,
            message,
            id,
            null,
            conflictETag,
            null,
            [],
            null,
            AdminConfigDocumentPolicy.ReloadHintFor(id));

    private static string FileName(string normalizedId)
        => AdminConfigDocumentPolicy.TryGetPluginId(normalizedId, out var pluginId)
            ? pluginId + ".json"
            : "profiles.json";
}

/// <summary>Records every invocation and returns a scripted result.</summary>
internal sealed class FakeAdminCommandRunner : IAdminCommandRunner
{
    public List<(string Command, IReadOnlyList<string> Args)> Calls { get; } = [];

    public Func<string, IReadOnlyList<string>, AdminCommandResult> ResultFactory { get; set; } = DefaultResult;

    public bool Throw { get; set; }

    public ValueTask<AdminCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((command, args));

        if (Throw)
            throw new InvalidOperationException("fake runner failure");

        return ValueTask.FromResult(ResultFactory(command, args));
    }

    public static AdminCommandResult DefaultResult(string command, IReadOnlyList<string> args)
        => new(
            true,
            AdminCodes.CommandSucceeded,
            "Command completed.",
            command,
            args,
            0,
            "{\"status\":\"ok\"}",
            string.Empty,
            false,
            15,
            12,
            false);

    public static AdminCommandResult Rejected(string command, IReadOnlyList<string> args, string code, string message)
        => new(false, code, message, command, args, 2, string.Empty, string.Empty, false, 0, 1, false);
}

internal sealed class FakeAdminLogReader : IAdminLogReader
{
    public IReadOnlyList<AdminLogEntry> Entries { get; set; } =
    [
        new(DateTimeOffset.UnixEpoch, "Information", "Natter.Host", "host started token=sk-abcdefghijkl", 1),
    ];

    public int LastLimit { get; private set; }

    public ValueTask<IReadOnlyList<AdminLogEntry>> ReadAsync(int limit, CancellationToken cancellationToken = default)
    {
        LastLimit = limit;
        return ValueTask.FromResult(Entries);
    }
}

/// <summary>Host redactor stand-in with the documented rules.</summary>
internal sealed class FakeAdminRedactor : IAdminRedactor
{
    public string Redact(string? value)
        => WebConsoleRedaction.RedactSecrets(value);
}
