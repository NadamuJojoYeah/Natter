namespace Natter.Application.Chat;

/// <summary>Host lifetime timestamps used by health and soak sampling.</summary>
public sealed class HostRuntimeClock
{
    private readonly Lock _gate = new();
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public DateTimeOffset StartedAt
    {
        get { lock (_gate) return _startedAt; }
    }

    public void MarkStarted() => _startedAt = DateTimeOffset.UtcNow;

    public long UptimeSeconds
        => Math.Max(1, (long)Math.Ceiling((DateTimeOffset.UtcNow - StartedAt).TotalSeconds));
}
