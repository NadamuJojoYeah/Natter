using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Natter.Plugin.WebConsole;

/// <summary>One file inside the diagnostics bundle, as recorded in <c>manifest.json</c>.</summary>
public sealed record WebConsoleDiagFile(
    string Path,
    string ContentType,
    int Bytes,
    int? Items,
    int? ShownItems,
    string? Truncation,
    string? Note);

/// <summary>Bundle manifest: version, collection context, per-file accounting and truncation.</summary>
public sealed record WebConsoleDiagManifest(
    string GeneratedAt,
    string Plugin,
    string Version,
    string HostApi,
    string? CollectedBy,
    string RedactionSource,
    long TotalBytes,
    long MaxTotalBytes,
    bool Truncated,
    string SentinelCheck,
    IReadOnlyList<WebConsoleDiagFile> Files);

public sealed record WebConsoleDiagBundle(byte[] Zip, WebConsoleDiagManifest Manifest, string FileName);

/// <summary>
/// Builds the one-shot diagnostics ZIP (S2/UI-DIRECTION §3.4). Every payload goes through the same
/// <see cref="WebConsoleRedactionPolicy"/> the console uses, the manifest records entry counts and
/// truncation, the total is capped, and the packed archive is scanned for plaintext sentinels before
/// it can leave the process.
/// </summary>
public static class WebConsoleDiagnosticsBundle
{
    /// <summary>Hard cap for the whole archive (uncompressed payload budget).</summary>
    public const long MaxTotalBytes = 8 * 1024 * 1024;

    public const int MaxLogEntries = 2000;
    public const int MaxAuditEntries = 500;
    public const int MaxConfigDocuments = 32;
    public const int MaxDocumentBytes = 256 * 1024;

    private const string SentinelMarker = "diag-sentinel";

    private static readonly Regex[] SecretShapes =
    [
        new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9._\-]{8,}", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Builds the archive. <paramref name="sentinels"/> are known-live secret values (console tokens)
    /// that must never appear; <paramref name="forceSentinel"/> is used by tests to prove the check
    /// trips when redaction is bypassed.
    /// </summary>
    public static WebConsoleDiagBundle Build(
        WebConsoleSnapshot snapshot,
        IReadOnlyDictionary<string, string?> documents,
        WebConsoleRedactionPolicy redaction,
        DateTimeOffset now,
        string? collectedBy = null,
        IReadOnlyCollection<string>? sentinels = null,
        string? forceSentinel = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(redaction);

        var files = new List<(string Path, string ContentType, byte[] Bytes, int? Items, int? Shown, string? Truncation)>();
        var budget = MaxTotalBytes;
        var truncated = false;

        byte[] RedactJson(string json) => Encoding.UTF8.GetBytes(redaction.Redact(json));

        void AddJson(string path, string json, int? items = null, int? shown = null, string? truncation = null)
        {
            var bytes = RedactJson(json);
            budget -= bytes.Length;
            files.Add((path, "application/json", bytes, items, shown, truncation));
        }

        AddJson("health.json", JsonSerializer.Serialize(new
        {
            available = snapshot.Health.Available,
            reason = snapshot.Health.Reason,
            gapCode = snapshot.Health.GapCode,
            data = snapshot.Health.Data,
        }, WebConsoleJson.Options), snapshot.Health.Available ? 1 : 0);

        AddJson("upstreams.json", JsonSerializer.Serialize(new
        {
            available = snapshot.Upstreams.Available,
            reason = snapshot.Upstreams.Reason,
            gapCode = snapshot.Upstreams.GapCode,
            data = snapshot.Upstreams.Data,
        }, WebConsoleJson.Options), snapshot.Upstreams.Data?.Count ?? 0);

        AddJson("providers.json", JsonSerializer.Serialize(new
        {
            available = snapshot.Profiles.Available,
            reason = snapshot.Profiles.Reason,
            gapCode = snapshot.Profiles.GapCode,
            data = snapshot.Profiles.Data,
        }, WebConsoleJson.Options), snapshot.Profiles.Data?.Count ?? 0);

        AddJson("plugins.json", JsonSerializer.Serialize(new
        {
            available = snapshot.Plugins.Available,
            reason = snapshot.Plugins.Reason,
            gapCode = snapshot.Plugins.GapCode,
            data = snapshot.Plugins.Data,
        }, WebConsoleJson.Options), snapshot.Plugins.Data?.Nodes.Count ?? 0);

        // config.redacted/<id>.json — one file per document, each capped independently.
        var documentFiles = new List<WebConsoleDiagFile>();
        foreach (var (id, content) in documents.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (documentFiles.Count >= MaxConfigDocuments)
            {
                truncated = true;
                break;
            }

            var text = redaction.Redact(content ?? string.Empty);
            var originalBytes = Encoding.UTF8.GetByteCount(text);
            var truncation = (string?)null;
            if (originalBytes > MaxDocumentBytes)
            {
                text = TruncateUtf8(text, MaxDocumentBytes);
                truncation = $"originalBytes={originalBytes} shownBytes={Encoding.UTF8.GetByteCount(text)}";
                truncated = true;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            budget -= bytes.Length;
            files.Add(($"config.redacted/{SanitizeName(id)}.json", "application/json", bytes, null, null, truncation));
        }

        // logs.jsonl / audit.jsonl — line-oriented, redacted per line, capped per file and by count.
        var logEntries = snapshot.Logs.Available ? snapshot.Logs.Data ?? [] : [];
        var shownLogs = logEntries.Take(MaxLogEntries).ToArray();
        if (shownLogs.Length < logEntries.Count)
            truncated = true;

        var logLines = new StringBuilder();
        var logBudget = 1024 * 1024;
        var writtenLogs = 0;
        foreach (var entry in shownLogs)
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = entry.Timestamp,
                level = entry.Level,
                source = entry.Source,
                message = entry.Message,
            }, WebConsoleJson.Options);

            var redacted = redaction.Redact(line);
            if (logBudget - Encoding.UTF8.GetByteCount(redacted) < 0)
            {
                truncated = true;
                break;
            }

            logBudget -= Encoding.UTF8.GetByteCount(redacted);
            logLines.Append(redacted).Append('\n');
            writtenLogs++;
        }

        var logBytes = RedactJson(logLines.ToString());
        budget -= logBytes.Length;
        files.Add((
            "logs.jsonl",
            "application/x-ndjson",
            logBytes,
            logEntries.Count,
            writtenLogs,
            writtenLogs < logEntries.Count ? $"shown={writtenLogs} of {logEntries.Count}" : null));

        var auditShown = snapshot.Audit.Take(MaxAuditEntries).ToArray();
        if (auditShown.Length < snapshot.Audit.Count)
            truncated = true;

        var auditLines = new StringBuilder();
        foreach (var entry in auditShown)
        {
            auditLines.Append(redaction.Redact(JsonSerializer.Serialize(new
            {
                ts = entry.Timestamp,
                action = entry.Action,
                user = entry.User,
                ip = entry.Ip,
                target = entry.Target,
                result = entry.Result,
                durationMs = entry.DurationMs,
            }, WebConsoleJson.Options))).Append('\n');
        }

        var auditBytes = RedactJson(auditLines.ToString());
        budget -= auditBytes.Length;
        files.Add((
            "audit.jsonl",
            "application/x-ndjson",
            auditBytes,
            snapshot.Audit.Count,
            auditShown.Length,
            auditShown.Length < snapshot.Audit.Count ? $"shown={auditShown.Length} of {snapshot.Audit.Count}" : null));

        // Total budget: drop optional payloads from the tail (audit, then logs) until it fits.
        while (budget < 0 && files.Count > 4)
        {
            var dropped = files[^1];
            files.RemoveAt(files.Count - 1);
            budget += dropped.Bytes.Length;
            files.Add((dropped.Path + ".omitted", dropped.ContentType, [], dropped.Items, 0,
                $"omitted: total budget {MaxTotalBytes} bytes exceeded"));
            truncated = true;
            break;
        }

        var manifestFiles = files
            .Select(file => new WebConsoleDiagFile(
                file.Path,
                file.ContentType,
                file.Bytes.Length,
                file.Items,
                file.Shown,
                file.Truncation,
                file is { Shown: not null, Items: not null } && file.Shown < file.Items ? "truncated" : null))
            .ToList();

        var manifest = new WebConsoleDiagManifest(
            now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            snapshot.Self.PluginId,
            snapshot.Self.Version,
            snapshot.Self.HostApi,
            collectedBy,
            redaction.UsesHostRedactor ? "host" : "plugin",
            files.Sum(file => (long)file.Bytes.Length),
            MaxTotalBytes,
            truncated,
            SentinelMarker,
            []);

        var manifestJson = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                generatedAt = manifest.GeneratedAt,
                plugin = manifest.Plugin,
                version = manifest.Version,
                hostApi = manifest.HostApi,
                collectedBy = manifest.CollectedBy,
                redaction = manifest.RedactionSource,
                totalBytes = manifest.TotalBytes,
                maxTotalBytes = manifest.MaxTotalBytes,
                truncated = manifest.Truncated,
                sentinel = manifest.SentinelCheck,
                files = manifestFiles,
            },
            WebConsoleJson.Options));

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifestJson);
            foreach (var (path, _, bytes, _, _, _) in files)
                WriteEntry(archive, path, bytes);

            // Scanner self-test hook: the probe entry deliberately carries the sentinel, so a healthy
            // scan must refuse the bundle. Tests use it to prove the check is not a no-op.
            if (!string.IsNullOrWhiteSpace(forceSentinel))
                WriteEntry(archive, "sentinel-probe.txt", Encoding.UTF8.GetBytes(forceSentinel));
        }

        var zip = buffer.ToArray();
        AssertNoPlaintextSecrets(zip, sentinels, forceSentinel);

        return new WebConsoleDiagBundle(
            zip,
            manifest with { Files = manifestFiles },
            $"natter-diag-{now:yyyyMMdd'T'HHmmss'Z'}.zip");
    }

    /// <summary>Scans every archive entry for live secrets and secret shapes; throws on a hit.</summary>
    private static void AssertNoPlaintextSecrets(byte[] zip, IReadOnlyCollection<string>? sentinels, string? forceSentinel)
    {
        var needles = new List<string>();
        foreach (var sentinel in sentinels ?? [])
        {
            if (!string.IsNullOrWhiteSpace(sentinel) && sentinel.Length >= 6)
                needles.Add(sentinel);
        }

        if (!string.IsNullOrWhiteSpace(forceSentinel) && forceSentinel.Length >= 6)
            needles.Add(forceSentinel);

        using var stream = new MemoryStream(zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            foreach (var needle in needles)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    throw new WebConsoleStartupException(
                        "webconsole.diag-sentinel-failed",
                        $"Diagnostics bundle entry '{entry.FullName}' still contains a live secret; "
                        + "the export was refused. Fix the redaction path before retrying.");
                }
            }

            if (entry.FullName == "manifest.json")
                continue;

            foreach (var shape in SecretShapes)
            {
                if (shape.IsMatch(text))
                {
                    throw new WebConsoleStartupException(
                        "webconsole.diag-sentinel-failed",
                        $"Diagnostics bundle entry '{entry.FullName}' contains a secret-shaped value; export refused.");
                }
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
            return value;

        // Cut on a UTF-8 boundary rather than mid-codepoint.
        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string SanitizeName(string id)
        => string.Concat(id.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
}
