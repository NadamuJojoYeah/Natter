using System.Globalization;
using System.Text.Json;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// Audit location resolution. The plugin package is frequently mounted read-only
/// (<c>/opt/natter/plugins</c>, systemd <c>ProtectSystem=strict</c>, read-only container layer), so
/// the audit trail always lives under the writable state root and **never** inside the plugin
/// directory (task-15 lesson, design §4.2). There is deliberately no plugin-directory fallback: a
/// silent fallback is what breaks under a read-only mount, and an audit trail that quietly
/// disappears is worse than a console that refuses to start.
/// </summary>
public sealed record ConversationConsoleAuditLocation(string Directory, string Root)
{
    public string FilePath => Path.Combine(Directory, ConversationConsoleDefaults.AuditFileName);

    public bool IsStateDirectory => string.Equals(Root, "state-directory", StringComparison.Ordinal);
}

public static class ConversationConsoleAuditPaths
{
    public const string StateDirectoryRoot = "state-directory";

    public static ConversationConsoleAuditLocation Resolve(string? stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new ConversationConsoleStartupException(
                "conversation-console.state-dir-required",
                $"NATTER_STATE_DIR is not set, so the audit trail has nowhere writable to live. The console "
                + "never writes into its own plugin directory (read-only mounts would fail). Set "
                + $"{ConversationConsoleDefaults.StateDirectoryEnvironmentVariable} (for example /var/lib/natter "
                + "or /state) before enabling this plugin.");
        }

        var root = Path.GetFullPath(stateDirectory.Trim());
        return new ConversationConsoleAuditLocation(
            Path.Combine(root, ConversationConsoleDefaults.StateDirectoryName, ConversationConsoleDefaults.AuditDirectoryName),
            StateDirectoryRoot);
    }

    /// <summary>Reads <c>NATTER_STATE_DIR</c> once, so tests never mutate process environment.</summary>
    public static string? ReadStateDirectoryFromEnvironment()
        => Environment.GetEnvironmentVariable(ConversationConsoleDefaults.StateDirectoryEnvironmentVariable);
}

/// <summary>
/// Throttles pre-authentication audit writes. Any web page can make a browser hit
/// <c>127.0.0.1</c>, so unauthenticated 401s are attacker-influenced; per action+client the first
/// <see cref="Burst"/> entries per window are written, the rest only counted, and the suppressed
/// count is reported on the first write of the next window.
/// </summary>
public sealed class ConversationConsoleAuditGate
{
    public const int Burst = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, WindowState> _windows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    public ConversationConsoleAuditGate(TimeProvider clock)
        => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public bool TryAcquire(string action, string client, out int suppressedFromPreviousWindow)
    {
        suppressedFromPreviousWindow = 0;
        var now = _clock.GetUtcNow();
        var key = action + "|" + client;

        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out var window) || now - window.StartedAt >= Window)
            {
                if (window is not null && window.Suppressed > 0)
                    suppressedFromPreviousWindow = window.Suppressed;

                _windows[key] = new WindowState(now);
                return true;
            }

            if (window.Written < Burst)
            {
                window.Written++;
                return true;
            }

            window.Suppressed++;
            return false;
        }
    }

    private sealed class WindowState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public int Written { get; set; } = 1;

        public int Suppressed { get; set; }
    }
}

/// <summary>
/// One audit record. The field set is fixed by design §4.2 — <c>at</c>, <c>action</c>,
/// <c>sessionId</c>, <c>messageCount</c>, <c>actor</c> — and message text is never part of it, so a
/// compromised audit file cannot become a second copy of the conversation.
/// </summary>
public sealed record ConversationConsoleAuditEntry(
    DateTimeOffset At,
    string Action,
    string? SessionId,
    int? MessageCount,
    string? Actor);

/// <summary>Append-only JSONL audit store (0700 directory, 0600 file, rotation keeps N generations).</summary>
public sealed class ConversationConsoleAuditStore
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly int _retentionFiles;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private bool _disabled;

    public ConversationConsoleAuditStore(string directory, TimeProvider clock, int retentionFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _retentionFiles = Math.Clamp(
            retentionFiles,
            ConversationConsoleDefaults.AuditRetentionFiles,
            ConversationConsoleDefaults.MaxAuditRetentionFiles);
        EnsurePrivateDirectory(directory);
        _path = Path.Combine(directory, ConversationConsoleDefaults.AuditFileName);
    }

    public string FilePath => _path;

    /// <summary>True while the audit file still accepts appends; surfaced by the health route.</summary>
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

    /// <summary>
    /// Appends one view record. Any exception is swallowed (and the store disables itself): an audit
    /// failure must never turn a diagnostic read into a 500.
    /// </summary>
    public void Write(string action, string? sessionId, int? messageCount, string? actor)
    {
        if (_disabled)
            return;

        var entry = new ConversationConsoleAuditEntry(
            _clock.GetUtcNow(),
            action,
            Sanitize(sessionId),
            messageCount,
            Sanitize(actor));

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
            _disabled = true;
        }
    }

    public IReadOnlyList<ConversationConsoleAuditEntry> Read(int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);

        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                    return [];

                var entries = new Queue<ConversationConsoleAuditEntry>(limit);
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
                        var entry = JsonSerializer.Deserialize<ConversationConsoleAuditEntry>(line, SerializerOptions);
                        if (entry is null)
                            continue;

                        if (entries.Count == limit)
                            entries.Dequeue();

                        entries.Enqueue(entry);
                    }
                    catch (JsonException)
                    {
                        // A partially written line must not hide older records.
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

    /// <summary>Keeps identifiers one short line each: no newlines, no control characters, bounded.</summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var buffer = new System.Text.StringBuilder(Math.Min(value.Length, 128));
        foreach (var character in value)
        {
            if (buffer.Length >= 128)
                break;

            buffer.Append(char.IsControl(character) ? '?' : character);
        }

        return buffer.ToString();
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxFileBytes)
            return;

        var oldest = _path + "." + _retentionFiles.ToString(CultureInfo.InvariantCulture);
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var generation = _retentionFiles - 1; generation >= 1; generation--)
        {
            var source = _path + "." + generation.ToString(CultureInfo.InvariantCulture);
            var target = _path + "." + (generation + 1).ToString(CultureInfo.InvariantCulture);
            if (File.Exists(source))
                File.Move(source, target, overwrite: true);
        }

        File.Move(_path, _path + ".1", overwrite: true);
    }

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
}

/// <summary>Action names written to the audit trail; one place so tests and code cannot drift.</summary>
public static class ConversationConsoleAuditActions
{
    public const string List = "conversation.list";
    public const string View = "conversation.view";
    public const string AuthDenied = "auth.denied";
    public const string OriginDenied = "origin.denied";
}
