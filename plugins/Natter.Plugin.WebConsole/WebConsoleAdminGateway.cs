using System.Text;
using Natter.Application.Admin;

namespace Natter.Plugin.WebConsole;

public sealed record WebConsoleAdminIssue(string Pointer, string Code, string Message);

public sealed record WebConsoleAdminDiffView(
    int Added,
    int Removed,
    int Changed,
    bool Truncated,
    IReadOnlyList<string> Lines);

/// <summary>Sanitized host config document returned to the browser.</summary>
public sealed record WebConsoleAdminDocument(
    string Id,
    bool Exists,
    string ETag,
    DateTimeOffset? LastModified,
    string Content,
    bool ContentRedacted,
    string Source,
    string ReloadHint,
    bool RestartRequired,
    int SizeBytes);

public sealed record WebConsoleAdminValidation(
    string Id,
    bool Valid,
    string ETag,
    int SizeBytes,
    bool ContainsSecretLiteral,
    string Code,
    IReadOnlyList<WebConsoleAdminIssue> Issues,
    string Source,
    string ReloadHint,
    bool RestartRequired,
    WebConsoleAdminDiffView? Diff);

public sealed record WebConsoleAdminSave(
    string Id,
    bool Saved,
    string Code,
    string Message,
    string? ETag,
    string? ConflictETag,
    string? BackupPath,
    string? UndoId,
    string ReloadHint,
    bool RestartRequired,
    IReadOnlyList<WebConsoleAdminIssue> Issues,
    WebConsoleAdminDiffView? Diff);

public sealed record WebConsoleAdminCommand(
    string Command,
    IReadOnlyList<string> Args,
    bool Executed,
    string Code,
    string Message,
    int ExitCode,
    string Stdout,
    string Stderr,
    bool Truncated,
    int OutputBytes,
    long DurationMs,
    bool TimedOut);

public sealed record WebConsoleUndoSummary(
    string UndoId,
    string DocumentId,
    DateTimeOffset RecordedAt,
    int SizeBytes,
    string ETag,
    string? BackupPath);

/// <summary>One whitelisted config document as offered to the editor (no content).</summary>
public sealed record WebConsoleAdminDocumentSummary(
    string Id,
    bool Exists,
    string ETag,
    DateTimeOffset? LastModified,
    string Source,
    bool Writable,
    string ReloadHint);

/// <summary>
/// Phase 2/3 gateway. It is the only place that talks to the host admin contracts: routing,
/// mapping host codes to HTTP semantics, bounding output, hiding secrets and keeping the
/// per-document undo buffer. Everything is fail-soft: a missing contract produces the documented
/// <c>host.*.unavailable</c> failure instead of an exception.
/// </summary>
public sealed class WebConsoleAdminGateway
{
    public const int MaxUndoEntriesPerDocument = 8;
    public const int MaxUndoEntriesTotal = 32;

    private static readonly TimeSpan UndoRetention = TimeSpan.FromMinutes(30);

    private readonly WebConsoleAdminSurface _surface;
    private readonly WebConsoleOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _undoGate = new();
    private readonly Dictionary<string, List<UndoEntry>> _undo = new(StringComparer.Ordinal);
    private int _undoCount;
    private int _undoSequence;

    public WebConsoleAdminGateway(WebConsoleAdminSurface surface, WebConsoleOptions options, TimeProvider clock)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Redaction = new WebConsoleRedactionPolicy(surface.Redactor);
    }

    public WebConsoleAdminSurface Surface => _surface;

    /// <summary>Redaction policy: host <c>IAdminRedactor</c> when bridged, local rules otherwise (S2-D5).</summary>
    public WebConsoleRedactionPolicy Redaction { get; }

    /// <summary>
    /// Best-effort document list (<c>ListAsync</c> is an optional host capability). A missing or
    /// failing list never fails the config preview; the UI falls back to the two known ids.
    /// </summary>
    public async ValueTask<IReadOnlyList<WebConsoleAdminDocumentSummary>> ListDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_surface.ConfigStore is not { } store)
            return [];

        try
        {
            var documents = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            return documents
                .Take(100)
                .Select(document => new WebConsoleAdminDocumentSummary(
                    document.Id,
                    document.Exists,
                    document.ETag,
                    document.LastModified,
                    SourceName(document.Source),
                    document.Writable,
                    ReloadHintName(document.ReloadHint)))
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async ValueTask<WebConsoleAdminResult<WebConsoleAdminDocument>> ReadAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (_surface.ConfigStore is not { } store)
            return WebConsoleAdminResult<WebConsoleAdminDocument>.Fail(WebConsoleAdminFailures.ConfigUnavailable);

        try
        {
            var result = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false);
            if (!result.Available)
                return WebConsoleAdminResult<WebConsoleAdminDocument>.Fail(
                    WebConsoleAdminFailures.FromConfigCode(result.Code, result.Message));

            var document = result.Document
                           ?? new AdminConfigDocument(
                               id,
                               string.Empty,
                               AdminETag.Compute(string.Empty),
                               _clock.GetUtcNow(),
                               false,
                               AdminConfigDocumentSource.Missing,
                               AdminConfigDocumentPolicy.ReloadHintFor(id));

            var safeContent = Redaction.Redact(document.Content);
            var redacted = !string.Equals(safeContent, document.Content, StringComparison.Ordinal);

            return WebConsoleAdminResult<WebConsoleAdminDocument>.Success(new WebConsoleAdminDocument(
                document.Id,
                document.Exists,
                document.ETag,
                document.Exists ? document.LastModified : null,
                safeContent,
                redacted,
                SourceName(document.Source),
                ReloadHintName(document.ReloadHint),
                IsRestartHint(document.ReloadHint),
                Encoding.UTF8.GetByteCount(document.Content)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return WebConsoleAdminResult<WebConsoleAdminDocument>.Fail(
                WebConsoleAdminFailures.ConfigHostFailed(Describe(exception)));
        }
    }

    public async ValueTask<WebConsoleAdminResult<WebConsoleAdminValidation>> ValidateAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (_surface.ConfigStore is not { } store)
            return WebConsoleAdminResult<WebConsoleAdminValidation>.Fail(WebConsoleAdminFailures.ConfigUnavailable);

        try
        {
            var validation = await store.ValidateAsync(id, content, cancellationToken).ConfigureAwait(false);
            var issued = MapIssues(validation.Issues);

            if (validation.Valid)
            {
                return WebConsoleAdminResult<WebConsoleAdminValidation>.Success(new WebConsoleAdminValidation(
                    validation.Id,
                    true,
                    validation.ETag,
                    validation.SizeBytes,
                    validation.ContainsSecretLiteral,
                    AdminCodes.ConfigValid,
                    issued,
                    "host",
                    ReloadHintName(validation.ReloadHint),
                    IsRestartHint(validation.ReloadHint),
                    MapDiff(validation.Diff)));
            }

            // A schema/content rejection is a normal validation outcome (HTTP 200), not a failure.
            var code = issued.Count > 0 ? issued[0].Code : AdminCodes.ConfigInvalid;
            return WebConsoleAdminResult<WebConsoleAdminValidation>.Success(new WebConsoleAdminValidation(
                validation.Id,
                false,
                validation.ETag,
                validation.SizeBytes,
                validation.ContainsSecretLiteral,
                code,
                issued,
                "host",
                ReloadHintName(validation.ReloadHint),
                IsRestartHint(validation.ReloadHint),
                MapDiff(validation.Diff)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return WebConsoleAdminResult<WebConsoleAdminValidation>.Fail(
                WebConsoleAdminFailures.ConfigHostFailed(Describe(exception)));
        }
    }

    public async ValueTask<WebConsoleAdminResult<WebConsoleAdminSave>> SaveAsync(
        string id,
        string content,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        if (_surface.ConfigStore is not { } store)
            return WebConsoleAdminResult<WebConsoleAdminSave>.Fail(WebConsoleAdminFailures.ConfigUnavailable);

        // Read first so a successful save can be undone from the in-memory buffer.
        AdminConfigReadResult? before = null;
        try
        {
            before = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            before = null;
        }

        AdminConfigSaveResult result;
        try
        {
            result = await store.SaveAsync(id, content, etag, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return WebConsoleAdminResult<WebConsoleAdminSave>.Fail(
                WebConsoleAdminFailures.ConfigHostFailed(Describe(exception)));
        }

        if (!result.Saved)
        {
            var failure = WebConsoleAdminFailures.FromConfigCode(result.Code, result.Message);
            var issues = MapIssues(result.Issues);
            if (result.ConflictETag is not null || issues.Count > 0)
                failure = failure with { Detail = new { conflictETag = result.ConflictETag, issues } };

            return WebConsoleAdminResult<WebConsoleAdminSave>.Fail(failure);
        }

        string? undoId = null;
        if (before is { Available: true, Document.Exists: true } existing)
        {
            undoId = RecordUndo(
                id,
                existing.Document.Content,
                existing.Document.ETag,
                result.BackupPath);
        }

        return WebConsoleAdminResult<WebConsoleAdminSave>.Success(new WebConsoleAdminSave(
            result.Id,
            true,
            result.Code,
            result.Message,
            result.ETag,
            null,
            result.BackupPath,
            undoId,
            ReloadHintName(result.ReloadHint),
            result.RestartRequired,
            MapIssues(result.Issues),
            MapDiff(result.Diff)));
    }

    /// <summary>
    /// Restores the in-memory snapshot taken before the previous save. The host has no
    /// backup-listing contract, so host backups stay a documented manual path (they are shown in the
    /// UI); this endpoint is the console's own "undo".
    /// </summary>
    public async ValueTask<WebConsoleAdminResult<WebConsoleAdminSave>> RollbackAsync(
        string id,
        string? etag,
        string? undoId,
        CancellationToken cancellationToken = default)
    {
        if (_surface.ConfigStore is null)
            return WebConsoleAdminResult<WebConsoleAdminSave>.Fail(WebConsoleAdminFailures.ConfigUnavailable);

        var entry = PeekUndo(id, undoId);
        if (entry is null)
        {
            return WebConsoleAdminResult<WebConsoleAdminSave>.Fail(
                "config.undo_empty",
                "No in-memory snapshot is available for this document; restore the host backup manually.",
                409);
        }

        var result = await SaveAsync(id, entry.Content, etag, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
            ConsumeUndo(id, entry.UndoId);

        return result;
    }

    public IReadOnlyList<WebConsoleUndoSummary> ListUndo(string id)
    {
        Prune();
        lock (_undoGate)
        {
            if (!_undo.TryGetValue(id, out var entries))
                return [];

            return entries
                .OrderByDescending(entry => entry.Sequence)
                .Select(entry => new WebConsoleUndoSummary(
                    entry.UndoId,
                    entry.DocumentId,
                    entry.RecordedAt,
                    entry.SizeBytes,
                    entry.ETag,
                    entry.BackupPath))
                .ToArray();
        }
    }

    public async ValueTask<WebConsoleAdminResult<WebConsoleAdminCommand>> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (_surface.CommandRunner is not { } runner)
            return WebConsoleAdminResult<WebConsoleAdminCommand>.Fail(WebConsoleAdminFailures.CommandUnavailable);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));

        AdminCommandResult result;
        try
        {
            result = await runner.RunAsync(command, args, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return WebConsoleAdminResult<WebConsoleAdminCommand>.Fail(
                WebConsoleAdminFailures.FromCommandCode(AdminCodes.CommandTimeout, $"Command exceeded {_options.CommandTimeoutSeconds}s."));
        }
        catch (Exception exception)
        {
            return WebConsoleAdminResult<WebConsoleAdminCommand>.Fail(
                WebConsoleAdminFailures.CommandHostFailed(Describe(exception)));
        }

        var output = WebConsoleCommandPolicy.NormalizeOutput(result.Stdout, result.Stderr);
        var view = new WebConsoleAdminCommand(
            result.Command,
            result.Args,
            result.Executed,
            result.Code,
            Redaction.SanitizeText(result.Message, 300),
            result.ExitCode,
            output.Stdout,
            output.Stderr,
            result.Truncated || output.Truncated,
            result.OutputBytes > 0 ? result.OutputBytes : output.OriginalBytes,
            result.DurationMs,
            result.TimedOut);

        if (!result.Executed)
        {
            var failure = WebConsoleAdminFailures.FromCommandCode(result.Code, result.Message);
            return new WebConsoleAdminResult<WebConsoleAdminCommand>(view, failure);
        }

        if (result.TimedOut)
        {
            return new WebConsoleAdminResult<WebConsoleAdminCommand>(
                view,
                WebConsoleAdminFailures.FromCommandCode(AdminCodes.CommandTimeout, result.Message));
        }

        return WebConsoleAdminResult<WebConsoleAdminCommand>.Success(view);
    }

    public async ValueTask<WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>>> ReadLogsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_surface.LogReader is not { } reader)
        {
            return WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>>.Unavailable(
                WebConsoleGaps.HostLogs.Code,
                WebConsoleGaps.HostLogs.Summary,
                WebConsoleGaps.HostLogs.Doc);
        }

        try
        {
            var entries = await reader.ReadAsync(limit, cancellationToken).ConfigureAwait(false);
            var mapped = entries
                .Select(entry => new WebConsoleLogEntry(
                    entry.Timestamp,
                    entry.Level,
                    entry.Category,
                    Redaction.SanitizeText(entry.Message, 400)))
                .ToArray();

            return WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>>.Ok(mapped, _clock.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return WebConsoleSection<IReadOnlyList<WebConsoleLogEntry>>.Unavailable(
                "host.logs.failed",
                $"宿主日志读取失败：{exception.GetType().Name}。",
                WebConsoleGaps.HostLogs.Doc);
        }
    }

    private string? RecordUndo(string id, string content, string etag, string? backupPath)
    {
        if (string.IsNullOrEmpty(content) || Encoding.UTF8.GetByteCount(content) > WebConsoleConfigEditPolicy.MaxContentBytes)
            return null;

        Prune();
        lock (_undoGate)
        {
            if (_undoCount >= MaxUndoEntriesTotal)
                return null;

            if (!_undo.TryGetValue(id, out var entries))
            {
                entries = [];
                _undo[id] = entries;
            }

            while (entries.Count >= MaxUndoEntriesPerDocument)
            {
                entries.RemoveAt(0);
                _undoCount--;
            }

            var undoId = "undo-" + (++_undoSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
            entries.Add(new UndoEntry(
                undoId,
                id,
                content,
                etag,
                backupPath,
                _clock.GetUtcNow(),
                Encoding.UTF8.GetByteCount(content),
                _undoSequence));
            _undoCount++;
            return undoId;
        }
    }

    private UndoEntry? PeekUndo(string id, string? undoId)
    {
        Prune();
        lock (_undoGate)
        {
            if (!_undo.TryGetValue(id, out var entries) || entries.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(undoId))
                return entries.FirstOrDefault(entry => string.Equals(entry.UndoId, undoId, StringComparison.Ordinal));

            return entries[^1];
        }
    }

    private void ConsumeUndo(string id, string undoId)
    {
        lock (_undoGate)
        {
            if (!_undo.TryGetValue(id, out var entries))
                return;

            var match = entries.FirstOrDefault(entry => string.Equals(entry.UndoId, undoId, StringComparison.Ordinal));
            if (match is null)
                return;

            entries.Remove(match);
            _undoCount--;
            if (entries.Count == 0)
                _undo.Remove(id);
        }
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - UndoRetention;
        lock (_undoGate)
        {
            foreach (var key in _undo.Keys.ToArray())
            {
                var entries = _undo[key];
                var removed = entries.RemoveAll(entry => entry.RecordedAt < cutoff);
                _undoCount -= removed;
                if (entries.Count == 0)
                    _undo.Remove(key);
            }
        }
    }

    private IReadOnlyList<WebConsoleAdminIssue> MapIssues(IReadOnlyList<AdminConfigIssue>? issues)
        => issues is null
            ? []
            : issues
                .Select(issue => new WebConsoleAdminIssue(
                    issue.Pointer,
                    issue.Code,
                    Redaction.SanitizeText(issue.Message, 300)))
                .ToArray();

    /// <summary>
    /// Diffs are built against the <em>stored</em> document, so the <c>"-"</c> lines are the current
    /// config text. Every line is redacted before it can leave the process (S1-04); the counters only
    /// carry numbers and stay as-is.
    /// </summary>
    private WebConsoleAdminDiffView? MapDiff(AdminConfigDiff? diff)
        => diff is null
            ? null
            : new WebConsoleAdminDiffView(
                diff.Added,
                diff.Removed,
                diff.Changed,
                diff.Truncated,
                diff.Lines.Select(RedactDiffLine).ToArray());

    /// <summary>Redacts one diff line, keeping the <c>" "</c>/<c>"-"</c>/<c>"+"</c> prefix readable.</summary>
    private string RedactDiffLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return line ?? string.Empty;

        var prefix = line[0] is ' ' or '-' or '+' ? line[..1] : string.Empty;
        var body = prefix.Length == 0 ? line : line[1..];

        var redacted = Redaction.Redact(body);
        return prefix + redacted;
    }

    private string Describe(Exception exception)
    {
        var message = Redaction.SanitizeText(exception.Message, 300);
        return string.IsNullOrWhiteSpace(message)
            ? $"Host admin call failed ({exception.GetType().Name})."
            : $"Host admin call failed ({exception.GetType().Name}): {message}";
    }

    private static bool IsRestartHint(AdminConfigReloadHint hint)
        => hint is AdminConfigReloadHint.RestartHost or AdminConfigReloadHint.RestartPlugin;

    private static string ReloadHintName(AdminConfigReloadHint hint) => hint switch
    {
        AdminConfigReloadHint.ReloadProfiles => "reload-profiles",
        AdminConfigReloadHint.RestartHost => "restart-host",
        AdminConfigReloadHint.RestartPlugin => "restart-plugin",
        _ => "none",
    };

    private static string SourceName(AdminConfigDocumentSource source) => source switch
    {
        AdminConfigDocumentSource.ConfigDirectory => "config-directory",
        AdminConfigDocumentSource.PluginPackage => "plugin-package",
        _ => "missing",
    };

    private sealed record UndoEntry(
        string UndoId,
        string DocumentId,
        string Content,
        string ETag,
        string? BackupPath,
        DateTimeOffset RecordedAt,
        int SizeBytes,
        int Sequence);
}
