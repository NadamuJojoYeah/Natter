namespace Natter.Application.Chat;

public sealed class TaskMetrics
{
    private long _created;
    private long _completed;
    private long _failed;
    private long _cancelled;
    private long _active;

    public void Created() { Interlocked.Increment(ref _created); Interlocked.Increment(ref _active); }
    public void Completed() { Interlocked.Increment(ref _completed); Interlocked.Decrement(ref _active); }
    public void Failed() { Interlocked.Increment(ref _failed); Interlocked.Decrement(ref _active); }
    public void Cancelled() { Interlocked.Increment(ref _cancelled); Interlocked.Decrement(ref _active); }

    public TaskMetricsSnapshot Snapshot()
        => new(
            Interlocked.Read(ref _created),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _cancelled),
            Math.Max(0, Interlocked.Read(ref _active)));
}

public sealed record TaskMetricsSnapshot(
    long Created,
    long Completed,
    long Failed,
    long Cancelled,
    long Active);
