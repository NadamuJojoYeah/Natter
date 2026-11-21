using System.Text.Json;

namespace Natter.Plugin.WebConsole;

/// <summary>One audit record. Never contains tokens, secrets, prompts or full command output.</summary>
public sealed record WebConsoleAuditEntry(
    DateTimeOffset Timestamp,
    string Action,
    string? User,
    string? Ip,
    string? Target,
    string Result,
    long DurationMs);

/// <summary>
/// Append-only JSONL audit store (design §12). The directory is created 0700 and the file 0600
/// (best effort on Unix); callers pass a writable location resolved by <see cref="WebConsoleAuditPaths"/>.
/// </summary>
public sealed class WebConsoleAuditStore : IDisposable
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private bool _disabled;

    public WebConsoleAuditStore(string directory, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        EnsurePrivateDirectory(directory);
        _path = System.IO.Path.Combine(directory, WebConsoleDefaults.AuditFileName);
    }

    public string FilePath => _path;

    /// <summary>True when the audit file can still be appended; surfaced as a health check.</summary>
    public bool CanWrite(out string? error)
    {
        if (_disabled)
        {
            error = "disabled after a previous write failure";
            return false;
        }

        try
        {
            lock (_gate)
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.GetType().Name;
            return false;
        }
    }

    public void Write(string action, string? user, string? ip, string? target, string result, TimeSpan duration)
    {
        if (_disabled)
            return;

        var entry = new WebConsoleAuditEntry(
            _clock.GetUtcNow(),
            action,
            user,
            ip,
            WebConsoleRedaction.SanitizeText(target, 200),
            result,
            (long)Math.Max(0, duration.TotalMilliseconds));

        try
        {
            var line = JsonSerializer.Serialize(entry, SerializerOptions) + "\n";

            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_path, line);
                TrySetFilePermissions(_path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Audit must never break a request. Disable after the first failure to avoid log spam.
            _disabled = true;
        }
    }

    public IReadOnlyList<WebConsoleAuditEntry> Read(int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);

        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                    return [];

                var entries = new Queue<WebConsoleAuditEntry>(limit);

                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var entry = JsonSerializer.Deserialize<WebConsoleAuditEntry>(line, SerializerOptions);
                        if (entry is null)
                            continue;

                        if (entries.Count == limit)
                            entries.Dequeue();

                        entries.Enqueue(entry);
                    }
                    catch (JsonException)
                    {
                        // Skip corrupt lines; a partial write must not hide older records.
                    }
                }

                return entries.ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxFileBytes)
            return;

        var archived = _path + ".1";
        File.Move(_path, archived, overwrite: true);
    }

    /// <summary>
    /// Creates the directory chain and tightens every level it created to 0700. A state root such as
    /// <c>/var/lib/natter</c> is usually created by the service manager and is left untouched.
    /// </summary>
    private static void EnsurePrivateDirectory(string directory)
    {
        var missing = new List<string>();
        var current = Path.GetFullPath(directory);

        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current);
        }

        missing.Reverse();
        foreach (var path in missing)
        {
            Directory.CreateDirectory(path);
            TrySetDirectoryPermissions(path);
        }

        if (missing.Count == 0)
            TrySetDirectoryPermissions(Path.GetFullPath(directory));
    }

    private static void TrySetDirectoryPermissions(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort: the host may mount the plugin directory read-only.
        }
    }

    private static void TrySetFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    public void Dispose()
    {
        // No open handles are kept between calls.
    }
}
