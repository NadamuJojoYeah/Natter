namespace Natter.Application.Chat;

/// <summary>Task runtime counters. Old fields are preserved; P5.3 adds tool/duration observations.</summary>
public sealed class TaskMetrics
{
    private long _created;
    private long _completed;
    private long _failed;
    private long _cancelled;
    private long _active;
    private long _toolCalls;
    private long _toolTimeouts;
    private long _durationSamples;
    private long _totalDurationMs;
    private long _maxDurationMs;

    public void Created() { Interlocked.Increment(ref _created); Interlocked.Increment(ref _active); }
    public void Completed() { Interlocked.Increment(ref _completed); Interlocked.Decrement(ref _active); }
    public void Failed() { Interlocked.Increment(ref _failed); Interlocked.Decrement(ref _active); }
    public void Cancelled() { Interlocked.Increment(ref _cancelled); Interlocked.Decrement(ref _active); }

    /// <summary>One tool invocation executed (native or text protocol).</summary>
    public void ToolCalled() => Interlocked.Increment(ref _toolCalls);

    /// <summary>One tool invocation failed because of its timeout budget.</summary>
    public void ToolTimedOut() => Interlocked.Increment(ref _toolTimeouts);

    /// <summary>Records the wall-clock duration of a terminal task (completed/failed/cancelled).</summary>
    public void Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var milliseconds = (long)elapsed.TotalMilliseconds;
        Interlocked.Increment(ref _durationSamples);
        Interlocked.Add(ref _totalDurationMs, milliseconds);
        InterlockedMax(ref _maxDurationMs, milliseconds);
    }

    public TaskMetricsSnapshot Snapshot()
        => new(
            Interlocked.Read(ref _created),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _cancelled),
            Math.Max(0, Interlocked.Read(ref _active)),
            Interlocked.Read(ref _toolCalls),
            Interlocked.Read(ref _toolTimeouts),
            Interlocked.Read(ref _durationSamples),
            Interlocked.Read(ref _totalDurationMs),
            Interlocked.Read(ref _maxDurationMs));

    private static void InterlockedMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;
            current = previous;
        }
    }
}

public sealed record TaskMetricsSnapshot(
    long Created,
    long Completed,
    long Failed,
    long Cancelled,
    long Active,
    long ToolCalls = 0,
    long ToolTimeouts = 0,
    long DurationSamples = 0,
    long TotalDurationMs = 0,
    long MaxDurationMs = 0)
{
    public long AverageDurationMs => DurationSamples <= 0 ? 0 : TotalDurationMs / DurationSamples;
}
