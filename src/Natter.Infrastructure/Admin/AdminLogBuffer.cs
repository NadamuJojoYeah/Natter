using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Bounded in-memory ring buffer for the host log tail (WebConsole gap G8). Entries are redacted
/// and truncated by <see cref="AdminLogBufferLoggerProvider"/> before they reach the buffer.
/// </summary>
public sealed class AdminLogBuffer
{
    public const int MinCapacity = 16;

    public const int MaxCapacity = 4_096;

    public const int MaxCategoryLength = 256;

    private readonly AdminLogEntry?[] _entries;
    private readonly Lock _gate = new();
    private long _nextSequence = 1;

    public AdminLogBuffer(int capacity, int maxMessageLength)
    {
        Capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
        MaxMessageLength = Math.Max(64, maxMessageLength);
        _entries = new AdminLogEntry[Capacity];
    }

    public int Capacity { get; }

    public int MaxMessageLength { get; }

    public void Add(DateTimeOffset timestamp, string level, string category, string message)
    {
        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "Information" : Sanitize(level, 32);
        var normalizedCategory = Sanitize(string.IsNullOrWhiteSpace(category) ? "Natter" : category, MaxCategoryLength);
        var normalizedMessage = Truncate(message ?? string.Empty, MaxMessageLength);

        lock (_gate)
        {
            var sequence = _nextSequence++;
            var index = (int)((sequence - 1) % Capacity);
            _entries[index] = new AdminLogEntry(
                timestamp,
                normalizedLevel,
                normalizedCategory,
                normalizedMessage,
                sequence);
        }
    }

    /// <summary>Returns up to <paramref name="limit"/> most recent entries in chronological order.</summary>
    public IReadOnlyList<AdminLogEntry> Snapshot(int limit)
    {
        if (limit <= 0)
            return [];

        lock (_gate)
        {
            var total = (int)Math.Min(_nextSequence - 1, Capacity);
            if (total == 0)
                return [];

            var take = Math.Min(limit, total);
            var result = new List<AdminLogEntry>(take);
            var lastSequence = _nextSequence - 1;

            for (var offset = take - 1; offset >= 0; offset--)
            {
                var sequence = lastSequence - offset;
                var index = (int)((sequence - 1) % Capacity);
                if (_entries[index] is { } entry)
                    result.Add(entry);
            }

            return result;
        }
    }

    private static string Sanitize(string value, int maxLength)
    {
        var builder = new System.Text.StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            if (char.IsControl(character))
                continue;

            builder.Append(character);
            if (builder.Length >= maxLength)
                break;
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
