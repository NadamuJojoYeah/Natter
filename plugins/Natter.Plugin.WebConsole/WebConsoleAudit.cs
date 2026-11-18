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

/// <summary>Append-only JSONL audit store inside the plugin directory (design §12).</summary>
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
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, WebConsoleDefaults.AuditFileName);
    }

    public string FilePath => _path;

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

    public void Dispose()
    {
        // No open handles are kept between calls.
    }
}
